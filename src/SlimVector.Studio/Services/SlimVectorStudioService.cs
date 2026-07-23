using System.Reflection;
using Microsoft.Extensions.Options;
using SlimVector.Application;
using SlimVector.Application.Backups;
using SlimVector.Application.Configuration;
using SlimVector.Application.Writes;
using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Models;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Studio.Contracts;

namespace SlimVector.Studio.Services;

public sealed class SlimVectorStudioService
{
    private readonly ISlimVectorDatabase _database;
    private readonly IDocumentIngestionPipeline _pipeline;
    private readonly IEmbeddingGenerator _embeddings;
    private readonly IBackupService _backups;
    private readonly IConsensusCoordinator _consensus;
    private readonly IWriteScheduler _writes;
    private readonly OperationalMetrics _operations;
    private readonly StorageOptions _storageOptions;
    private readonly StudioOptions _studioOptions;

    public SlimVectorStudioService(
        ISlimVectorDatabase database,
        IDocumentIngestionPipeline pipeline,
        IEmbeddingGenerator embeddings,
        IBackupService backups,
        IConsensusCoordinator consensus,
        IWriteScheduler writes,
        OperationalMetrics operations,
        IOptions<StorageOptions> storageOptions,
        IOptions<StudioOptions> studioOptions)
    {
        _database = database;
        _pipeline = pipeline;
        _embeddings = embeddings;
        _backups = backups;
        _consensus = consensus;
        _writes = writes;
        _operations = operations;
        _storageOptions = storageOptions.Value;
        _studioOptions = studioOptions.Value;
    }

    public async ValueTask<StudioBootstrapResponse> GetBootstrapAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<CollectionDefinition> collections = await _database.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
        Task<CollectionSummary>[] summaries = collections.Select(async collection => new CollectionSummary
        {
            Definition = StudioCollectionDefinition.FromDomain(collection),
            DocumentCount = await _database.CountDocumentsAsync(collection.Name, cancellationToken).ConfigureAwait(false),
        }).ToArray();
        CollectionSummary[] resolved = await Task.WhenAll(summaries).ConfigureAwait(false);
        EmbeddingModelStatus model = await _embeddings.GetStatusAsync(cancellationToken).ConfigureAwait(false);
        return new StudioBootstrapResponse
        {
            Product = "SlimVector Studio",
            Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0",
            Collections = resolved.OrderBy(static item => item.Definition.Name, StringComparer.Ordinal).ToArray(),
            Model = model,
            SupportedExtensions = [".pdf", ".docx", ".pptx", ".txt", ".md"],
            MaximumUploadBytes = _studioOptions.MaximumUploadBytes,
            Chunking = new StudioChunkingConfiguration
            {
                TargetTokens = _studioOptions.Chunking.TargetTokens,
                MaximumTokens = _studioOptions.Chunking.MaximumTokens,
                OverlapTokens = _studioOptions.Chunking.OverlapTokens,
                MaximumAllowedTokens = StudioOptions.MaximumChunkTokens,
            },
            StoragePath = Path.GetFullPath(_storageOptions.Path),
        };
    }

    public ValueTask<EmbeddingModelStatus> GetModelStatusAsync(CancellationToken cancellationToken) =>
        _embeddings.GetStatusAsync(cancellationToken);

    public async ValueTask<EmbeddingModelStatus> PrepareModelAsync(CancellationToken cancellationToken)
    {
        await _embeddings.EnsureReadyAsync(cancellationToken: cancellationToken).ConfigureAwait(false);
        return await _embeddings.GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask<CollectionDefinition> CreateCollectionAsync(CreateCollectionInput input, CancellationToken cancellationToken) =>
        _database.CreateCollectionAsync(
            input.Name,
            input.Dimension,
            input.Metric,
            ToIndexConfiguration(input.IndexKind, input.HnswM, input.HnswEfConstruction, input.HnswEfSearch),
            cancellationToken);

    public ValueTask<CollectionDefinition> UpdateCollectionAsync(
        string currentName,
        UpdateCollectionInput input,
        CancellationToken cancellationToken) =>
        _database.UpdateCollectionAsync(
            currentName,
            input.Name,
            ToIndexConfiguration(input.IndexKind, input.HnswM, input.HnswEfConstruction, input.HnswEfSearch),
            cancellationToken);

    public ValueTask DeleteCollectionAsync(string name, CancellationToken cancellationToken) =>
        _database.DeleteCollectionAsync(name, cancellationToken);

    public async ValueTask<IngestResponse> IngestAsync(IngestCommand command, CancellationToken cancellationToken)
    {
        CollectionDefinition collection = await _database.GetCollectionAsync(command.Collection, cancellationToken).ConfigureAwait(false);
        if (!command.PreviewOnly && collection.Dimension != _embeddings.Dimension)
        {
            throw new DocumentIngestionException(
                "collection_embedding_dimension_mismatch",
                $"La collection « {collection.Name} » a une dimension de {collection.Dimension} ; le modèle local « {_embeddings.ModelId} » produit {_embeddings.Dimension} dimensions.");
        }

        DocumentSource source = new(command.Content, command.FileName, command.ContentType, command.Length);
        IngestionResult result = await _pipeline.IngestAsync(
            source,
            new IngestionOptions { Chunking = command.Chunking, GenerateEmbeddings = true },
            cancellationToken: cancellationToken).ConfigureAwait(false);

        int stored = 0;
        int removed = 0;
        if (!command.PreviewOnly)
        {
            HashSet<string> currentIds = result.Chunks.Select(static chunk => chunk.Id).ToHashSet(StringComparer.Ordinal);
            string[] previousIds = command.ReplaceExisting
                ? await FindDocumentIdsBySourceFileAsync(collection.Name, command.FileName, cancellationToken).ConfigureAwait(false)
                : [];
            DocumentMutationKind kind = command.ReplaceExisting ? DocumentMutationKind.Upsert : DocumentMutationKind.Add;
            DocumentMutation[] mutations = result.Chunks.Select(chunk => new DocumentMutation
            {
                Kind = kind,
                Id = chunk.Id,
                Document = new DocumentRecord
                {
                    Id = chunk.Id,
                    Text = chunk.Chunk.Text,
                    Vector = chunk.Vector,
                    Metadata = BuildChunkMetadata(command, result, chunk),
                    UpdatedAt = DateTimeOffset.UtcNow,
                },
            }).ToArray();
            stored = await ExecuteBatchesAsync(collection, mutations, command.Atomic, cancellationToken).ConfigureAwait(false);

            string[] stale = previousIds.Where(id => !currentIds.Contains(id)).ToArray();
            if (stale.Length > 0)
            {
                DocumentMutation[] deletes = stale.Select(static id => new DocumentMutation
                {
                    Kind = DocumentMutationKind.Delete,
                    Id = id,
                }).ToArray();
                removed = await ExecuteBatchesAsync(collection, deletes, command.Atomic, cancellationToken).ConfigureAwait(false);
            }
        }

        return new IngestResponse
        {
            FileName = command.FileName,
            DocumentId = result.DocumentId,
            ContentSha256 = result.ContentSha256,
            Format = result.Document.Format,
            Title = result.Document.Title,
            SectionCount = result.Document.Sections.Count,
            CharacterCount = result.Document.CharacterCount,
            ChunkCount = result.Chunks.Count,
            StoredCount = stored,
            RemovedPreviousCount = removed,
            PreviewOnly = command.PreviewOnly,
            ExtractionMilliseconds = result.ExtractionDuration.TotalMilliseconds,
            ChunkingMilliseconds = result.ChunkingDuration.TotalMilliseconds,
            EmbeddingMilliseconds = result.EmbeddingDuration.TotalMilliseconds,
            Chunks = result.Chunks.Select(static chunk => new IngestedChunkResponse
            {
                Id = chunk.Id,
                Sequence = chunk.Chunk.Sequence,
                EstimatedTokens = chunk.Chunk.EstimatedTokens,
                Locations = chunk.Chunk.Locations,
                Heading = chunk.Chunk.Heading,
                Text = chunk.Chunk.Text,
                VectorPreview = chunk.Vector.Take(8).ToArray(),
            }).ToArray(),
        };
    }

    public async ValueTask<StudioSearchResponse> SearchAsync(
        string collectionName,
        SearchInput input,
        CancellationToken cancellationToken)
    {
        CollectionDefinition collection = await _database.GetCollectionAsync(collectionName, cancellationToken).ConfigureAwait(false);
        float[]? vector = input.Vector;
        bool vectorized = false;
        if (input.Mode is SearchMode.Vector or SearchMode.Hybrid && vector is null)
        {
            if (collection.Dimension != _embeddings.Dimension)
            {
                throw new DocumentIngestionException(
                    "collection_embedding_dimension_mismatch",
                    $"La vectorisation automatique des requêtes nécessite une collection de dimension {_embeddings.Dimension}.");
            }

            IReadOnlyList<float[]> generated = await _embeddings.GenerateAsync([input.Query], cancellationToken: cancellationToken).ConfigureAwait(false);
            vector = generated[0];
            vectorized = true;
        }

        IncludeFields include = IncludeFields.None;
        include |= input.IncludeText ? IncludeFields.Text : IncludeFields.None;
        include |= input.IncludeVector ? IncludeFields.Vector : IncludeFields.None;
        include |= input.IncludeMetadata ? IncludeFields.Metadata : IncludeFields.None;
        include |= input.IncludeScores ? IncludeFields.Scores : IncludeFields.None;
        SearchResponse result = await _database.SearchAsync(collectionName, new SearchRequest
        {
            Text = input.Mode is SearchMode.Text or SearchMode.Hybrid ? input.Query : null,
            Vector = vector,
            Mode = input.Mode,
            Limit = input.Limit,
            Filter = input.Filter is null ? null : MetadataJson.ToFilter(input.Filter),
            Include = include,
            Consistency = input.Consistency,
            VectorWeight = input.VectorWeight,
            TextWeight = input.TextWeight,
        }, cancellationToken).ConfigureAwait(false);

        return new StudioSearchResponse
        {
            Hits = result.Hits.Select(static hit => new SearchHitResponse
            {
                Id = hit.Id,
                Text = hit.Text,
                Vector = hit.Vector,
                Metadata = hit.Metadata is null ? null : MetadataJson.ToJson(hit.Metadata),
                Score = hit.Score,
                VectorRank = hit.VectorRank,
                TextRank = hit.TextRank,
            }).ToArray(),
            TookMicroseconds = result.TookMicroseconds,
            QueryWasVectorized = vectorized,
        };
    }

    public async ValueTask<DocumentPageResponse> GetDocumentsAsync(
        string collectionName,
        int offset,
        int limit,
        bool includeVectors,
        CancellationToken cancellationToken)
    {
        long total = await _database.CountDocumentsAsync(collectionName, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<DocumentRecord> documents = await _database
            .GetDocumentsAsync(collectionName, offset: offset, limit: limit, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return new DocumentPageResponse
        {
            Total = total,
            Offset = offset,
            Limit = limit,
            Documents = documents.Select(document => ToDocumentResponse(document, includeVectors)).ToArray(),
        };
    }

    public async ValueTask<BatchMutationResult> MutateDocumentsAsync(
        string collectionName,
        ManualMutationInput input,
        CancellationToken cancellationToken)
    {
        if (input.Documents.Length == 0)
        {
            throw new ArgumentException("Au moins un document est requis.", nameof(input));
        }

        CollectionDefinition collection = await _database.GetCollectionAsync(collectionName, cancellationToken).ConfigureAwait(false);
        Dictionary<int, float[]> generatedVectors = [];
        List<int> generatedIndexes = [];
        List<string> generatedTexts = [];
        for (int index = 0; index < input.Documents.Length; index++)
        {
            ManualDocumentInput document = input.Documents[index];
            bool canGenerate = input.Kind is not DocumentMutationKind.Delete && !string.IsNullOrWhiteSpace(document.Text);
            if (canGenerate && (document.AutoVectorize || document.Vector is null))
            {
                if (collection.Dimension != _embeddings.Dimension)
                {
                    throw new DocumentIngestionException(
                        "collection_embedding_dimension_mismatch",
                        $"La vectorisation automatique nécessite une collection de dimension {_embeddings.Dimension}.");
                }

                generatedIndexes.Add(index);
                generatedTexts.Add(document.Text!);
            }
        }

        if (generatedTexts.Count > 0)
        {
            IReadOnlyList<float[]> generated = await _embeddings.GenerateAsync(generatedTexts, cancellationToken: cancellationToken).ConfigureAwait(false);
            for (int index = 0; index < generated.Count; index++)
            {
                generatedVectors[generatedIndexes[index]] = generated[index];
            }
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        DocumentMutation[] mutations = input.Documents.Select((document, index) => CreateManualMutation(
            input.Kind,
            document,
            generatedVectors.GetValueOrDefault(index),
            now)).ToArray();
        return await _database.MutateAsync(collectionName, mutations, input.Atomic, "slimvector-studio", cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<BatchMutationResult> DeleteDocumentsAsync(
        string collectionName,
        IReadOnlyList<string> ids,
        bool atomic,
        CancellationToken cancellationToken)
    {
        DocumentMutation[] deletes = ids.Select(static id => new DocumentMutation
        {
            Kind = DocumentMutationKind.Delete,
            Id = id,
        }).ToArray();
        return await _database.MutateAsync(collectionName, deletes, atomic, "slimvector-studio", cancellationToken).ConfigureAwait(false);
    }

    public RuntimeResponse GetRuntime() => new()
    {
        Ready = _consensus.IsReady,
        Mode = _consensus.Mode,
        OpenCollections = _database.OpenCollectionCount,
        ManagedMemoryBytes = GC.GetTotalMemory(forceFullCollection: false),
        RaftGroups = _consensus.GetStatuses(),
        Writes = _writes.GetSnapshot(),
        Operations = _operations.GetSnapshot(),
        Backups = _backups.GetMetrics(),
    };

    public ValueTask<BackupDescriptor> CreateBackupAsync(CancellationToken cancellationToken) =>
        _backups.CreateBackupAsync(cancellationToken);

    public ValueTask<IReadOnlyList<BackupDescriptor>> ListBackupsAsync(CancellationToken cancellationToken) =>
        _backups.ListBackupsAsync(cancellationToken);

    public ValueTask VerifyBackupAsync(string backupId, CancellationToken cancellationToken) =>
        _backups.VerifyBackupAsync(backupId, cancellationToken);

    public ValueTask RestoreCollectionAsync(string backupId, RestoreCollectionInput input, CancellationToken cancellationToken) =>
        _backups.RestoreCollectionAsync(backupId, input.CollectionName, input.RestoredName, input.Overwrite, cancellationToken);

    public ValueTask RestoreFullAsync(string backupId, ConfirmedRestoreInput input, CancellationToken cancellationToken)
    {
        if (!string.Equals(input.Confirm, "RESTORE", StringComparison.Ordinal))
        {
            throw new ArgumentException("Saisissez RESTORE pour confirmer une restauration complète.", nameof(input));
        }

        return _backups.RestoreFullAsync(backupId, cancellationToken);
    }

    private async ValueTask<string[]> FindDocumentIdsBySourceFileAsync(
        string collectionName,
        string fileName,
        CancellationToken cancellationToken)
    {
        const int pageSize = 1_000;
        List<string> ids = [];
        for (int offset = 0; ; offset += pageSize)
        {
            IReadOnlyList<DocumentRecord> documents = await _database.GetDocumentsAsync(
                collectionName,
                offset: offset,
                limit: pageSize,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (DocumentRecord document in documents)
            {
                if (document.Metadata.GetValueOrDefault("source_file") is
                    { Kind: MetadataValueKind.Text, StringValue: { } sourceFile } &&
                    string.Equals(sourceFile, fileName, StringComparison.Ordinal))
                {
                    ids.Add(document.Id);
                }
            }

            if (documents.Count < pageSize)
            {
                return ids.ToArray();
            }
        }
    }

    private async ValueTask<int> ExecuteBatchesAsync(
        CollectionDefinition collection,
        DocumentMutation[] mutations,
        bool atomic,
        CancellationToken cancellationToken)
    {
        int succeeded = 0;
        const int batchSize = 500;
        for (int offset = 0; offset < mutations.Length; offset += batchSize)
        {
            DocumentMutation[] batch = mutations.Skip(offset).Take(batchSize).ToArray();
            BatchMutationResult result = await _database
                .MutateAsync(collection.Name, batch, atomic, "slimvector-studio", cancellationToken)
                .ConfigureAwait(false);
            succeeded += result.Succeeded;
            if (result.Failed > 0)
            {
                DocumentMutationResult? failure = result.Results.FirstOrDefault(static item => !item.Succeeded);
                throw new DocumentIngestionException(
                    failure?.ErrorCode ?? "document_store_failed",
                    failure?.ErrorMessage ?? "SlimVector a refusé un ou plusieurs fragments.");
            }
        }

        return succeeded;
    }

    private static Dictionary<string, MetadataValue> BuildChunkMetadata(
        IngestCommand command,
        IngestionResult result,
        EmbeddedChunk chunk)
    {
        Dictionary<string, MetadataValue> metadata = MetadataJson.FromJson(command.Metadata);
        metadata["source_file"] = MetadataValue.From(command.FileName);
        metadata["content_sha256"] = MetadataValue.From(result.ContentSha256);
        metadata["document_id"] = MetadataValue.From(result.DocumentId);
        metadata["document_format"] = MetadataValue.From(result.Document.Format.ToString());
        metadata["chunk_sequence"] = MetadataValue.From((long)chunk.Chunk.Sequence);
        metadata["chunk_tokens"] = MetadataValue.From((long)chunk.Chunk.EstimatedTokens);
        metadata["locations"] = MetadataValue.From(chunk.Chunk.Locations.ToArray());
        metadata["ingested_at"] = MetadataValue.From(DateTimeOffset.UtcNow);
        if (!string.IsNullOrWhiteSpace(chunk.Chunk.Heading))
        {
            metadata["heading"] = MetadataValue.From(chunk.Chunk.Heading);
        }

        if (!string.IsNullOrWhiteSpace(result.Document.Title))
        {
            metadata["title"] = MetadataValue.From(result.Document.Title);
        }

        return metadata;
    }

    private static DocumentMutation CreateManualMutation(
        DocumentMutationKind kind,
        ManualDocumentInput input,
        float[]? generatedVector,
        DateTimeOffset now)
    {
        float[]? vector = generatedVector ?? input.Vector;
        if (kind is DocumentMutationKind.Add or DocumentMutationKind.Upsert)
        {
            if (input.Text is null || vector is null)
            {
                throw new ArgumentException("L’ajout et la création ou le remplacement nécessitent un texte ainsi qu’un vecteur ou une vectorisation automatique.");
            }

            return new DocumentMutation
            {
                Kind = kind,
                Id = input.Id,
                Document = new DocumentRecord
                {
                    Id = input.Id,
                    Text = input.Text,
                    Vector = vector,
                    Metadata = MetadataJson.FromJson(input.Metadata),
                    UpdatedAt = now,
                },
            };
        }

        if (kind == DocumentMutationKind.Update)
        {
            return new DocumentMutation
            {
                Kind = kind,
                Id = input.Id,
                Patch = new DocumentPatch
                {
                    Text = input.Text,
                    Vector = vector,
                    Metadata = input.Metadata is null ? null : MetadataJson.FromJson(input.Metadata),
                },
            };
        }

        return new DocumentMutation { Kind = DocumentMutationKind.Delete, Id = input.Id };
    }

    private static DocumentResponse ToDocumentResponse(DocumentRecord document, bool includeVector) => new()
    {
        Id = document.Id,
        Text = document.Text,
        Vector = includeVector ? document.Vector : null,
        VectorDimension = document.Vector.Length,
        Metadata = MetadataJson.ToJson(document.Metadata),
        Version = document.Version,
        UpdatedAt = document.UpdatedAt,
    };

    private static VectorIndexConfiguration ToIndexConfiguration(
        VectorIndexKind kind,
        int hnswM,
        int hnswEfConstruction,
        int hnswEfSearch) => new()
        {
            Kind = kind,
            HnswM = hnswM,
            HnswEfConstruction = hnswEfConstruction,
            HnswEfSearch = hnswEfSearch,
        };
}
