using MemoryPack;
using SlimVector.Domain;
using SlimVector.Storage;

namespace SlimVector.Raft.Commands;

public static class RaftCommandCodec
{
    public const int CurrentFormatVersion = 1;

    public static byte[] Serialize(RaftCommandEnvelope command)
    {
        Validate(command);
        return MemoryPackSerializer.Serialize(command);
    }

    public static RaftCommandEnvelope Deserialize(ReadOnlySpan<byte> payload)
    {
        RaftCommandEnvelope command;
        try
        {
            command = MemoryPackSerializer.Deserialize<RaftCommandEnvelope>(payload)
                ?? throw new InvalidDataException("The Raft command payload is empty.");
        }
        catch (MemoryPackSerializationException exception)
        {
            throw new InvalidDataException("The Raft command payload is malformed.", exception);
        }

        Validate(command);
        return command;
    }

    public static RaftCommandEnvelope CatalogUpsert(
        Guid commandId,
        string groupId,
        CollectionDefinition collection,
        string dataGroupId) => new()
        {
            CommandId = commandId,
            GroupId = groupId,
            Kind = RaftCommandKind.CatalogUpsert,
            CatalogUpsert = new CatalogUpsertCommand
            {
                Collection = FromDomain(collection),
                DataGroupId = dataGroupId,
            },
        };

    public static RaftCommandEnvelope CatalogDelete(
        Guid commandId,
        string groupId,
        Guid collectionId,
        string collectionName) => new()
        {
            CommandId = commandId,
            GroupId = groupId,
            Kind = RaftCommandKind.CatalogDelete,
            CatalogDelete = new CatalogDeleteCommand
            {
                CollectionId = collectionId,
                CollectionName = collectionName,
            },
        };

    public static RaftCommandEnvelope DataBatch(
        Guid commandId,
        string groupId,
        CollectionDefinition collection,
        IReadOnlyList<StorageOperation> operations) => new()
        {
            CommandId = commandId,
            GroupId = groupId,
            Kind = RaftCommandKind.DataBatch,
            DataBatch = FromWrite(new CollectionWrite(collection, operations)),
        };

    public static RaftCommandEnvelope ShardBatch(
        Guid commandId,
        string groupId,
        IReadOnlyList<CollectionWrite> writes) => new()
        {
            CommandId = commandId,
            GroupId = groupId,
            Kind = RaftCommandKind.ShardBatch,
            ShardBatch = new ShardBatchCommand { Batches = writes.Select(FromWrite).ToArray() },
        };

    public static CollectionDefinition ToDomain(RaftCollectionDefinition collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        CollectionDefinition result = new()
        {
            Id = collection.Id,
            Name = collection.Name,
            Dimension = collection.Dimension,
            Metric = collection.Metric,
            VectorIndex = new VectorIndexConfiguration
            {
                Kind = collection.VectorIndexKind,
                HnswM = collection.HnswM,
                HnswEfConstruction = collection.HnswEfConstruction,
                HnswEfSearch = collection.HnswEfSearch,
            },
            MetadataIndexed = collection.MetadataIndexed,
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt,
        };
        DomainValidation.ValidateCollectionName(result.Name);
        DomainValidation.ValidateDimension(result.Dimension);
        DomainValidation.ValidateVectorIndex(result.VectorIndex);
        return result;
    }

    public static StorageOperation ToStorage(RaftStorageOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        DomainValidation.ValidateDocumentId(operation.Id);
        return operation.Kind switch
        {
            DocumentMutationKind.Delete => StorageOperation.Delete(operation.Id, operation.Version),
            DocumentMutationKind.Add or DocumentMutationKind.Upsert or DocumentMutationKind.Update when operation.Document is not null =>
                StorageOperation.Upsert(ToDomain(operation.Document)),
            _ => throw new InvalidDataException($"Raft storage operation '{operation.Kind}' is invalid."),
        };
    }

    public static RaftCollectionDefinition FromDomain(CollectionDefinition collection)
    {
        ArgumentNullException.ThrowIfNull(collection);
        return new RaftCollectionDefinition
        {
            Id = collection.Id,
            Name = collection.Name,
            Dimension = collection.Dimension,
            Metric = collection.Metric,
            VectorIndexKind = collection.VectorIndex.Kind,
            HnswM = collection.VectorIndex.HnswM,
            HnswEfConstruction = collection.VectorIndex.HnswEfConstruction,
            HnswEfSearch = collection.VectorIndex.HnswEfSearch,
            MetadataIndexed = collection.MetadataIndexed,
            CreatedAt = collection.CreatedAt,
            UpdatedAt = collection.UpdatedAt,
        };
    }

    public static RaftStorageOperation FromStorage(StorageOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return new RaftStorageOperation
        {
            Kind = operation.Kind,
            Id = operation.Id,
            Document = operation.Document is null ? null : FromDomain(operation.Document),
            Version = operation.Version,
        };
    }

    public static RaftDocument FromDomain(DocumentRecord document) => new()
    {
        Id = document.Id,
        Text = document.Text,
        Vector = document.Vector,
        Metadata = document.Metadata
            .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static pair => new RaftMetadataEntry { Key = pair.Key, Value = FromDomain(pair.Value) })
            .ToArray(),
        Version = document.Version,
        UpdatedAt = document.UpdatedAt,
    };

    public static DocumentRecord ToDomain(RaftDocument document)
    {
        DocumentRecord result = new()
        {
            Id = document.Id,
            Text = document.Text,
            Vector = document.Vector,
            Metadata = document.Metadata.ToDictionary(
                static entry => entry.Key,
                static entry => ToDomain(entry.Value),
                StringComparer.Ordinal),
            Version = document.Version,
            UpdatedAt = document.UpdatedAt,
        };
        return result;
    }

    private static RaftMetadataValue FromDomain(MetadataValue value) => new()
    {
        Kind = value.Kind,
        StringValue = value.StringValue,
        BooleanValue = value.BooleanValue,
        IntegerValue = value.IntegerValue,
        NumberValue = value.NumberValue,
        DateTimeValue = value.DateTimeValue,
        GuidValue = value.GuidValue,
        StringArrayValue = value.StringArrayValue,
        BooleanArrayValue = value.BooleanArrayValue,
        IntegerArrayValue = value.IntegerArrayValue,
        NumberArrayValue = value.NumberArrayValue,
    };

    private static MetadataValue ToDomain(RaftMetadataValue value) => new()
    {
        Kind = value.Kind,
        StringValue = value.StringValue,
        BooleanValue = value.BooleanValue,
        IntegerValue = value.IntegerValue,
        NumberValue = value.NumberValue,
        DateTimeValue = value.DateTimeValue,
        GuidValue = value.GuidValue,
        StringArrayValue = value.StringArrayValue,
        BooleanArrayValue = value.BooleanArrayValue,
        IntegerArrayValue = value.IntegerArrayValue,
        NumberArrayValue = value.NumberArrayValue,
    };

    private static void Validate(RaftCommandEnvelope command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.FormatVersion != CurrentFormatVersion)
        {
            throw new InvalidDataException($"Raft command format version '{command.FormatVersion}' is unsupported.");
        }

        if (command.CommandId == Guid.Empty)
        {
            throw new InvalidDataException("Raft command id may not be empty.");
        }

        if (string.IsNullOrWhiteSpace(command.GroupId))
        {
            throw new InvalidDataException("Raft group id is required.");
        }

        int payloadCount = (command.CatalogUpsert is null ? 0 : 1) +
            (command.CatalogDelete is null ? 0 : 1) +
            (command.DataBatch is null ? 0 : 1) +
            (command.ShardBatch is null ? 0 : 1);
        bool payloadMatchesKind = command.Kind switch
        {
            RaftCommandKind.CatalogUpsert => command.CatalogUpsert is not null,
            RaftCommandKind.CatalogDelete => command.CatalogDelete is not null,
            RaftCommandKind.DataBatch => command.DataBatch is not null,
            RaftCommandKind.ShardBatch => command.ShardBatch is { Batches.Length: > 0 },
            _ => false,
        };
        if (payloadCount != 1 || !payloadMatchesKind)
        {
            throw new InvalidDataException("Raft command kind and payload are inconsistent.");
        }
    }

    private static DataBatchCommand FromWrite(CollectionWrite write)
    {
        ArgumentNullException.ThrowIfNull(write);
        return new DataBatchCommand
        {
            CollectionId = write.Collection.Id,
            Collection = FromDomain(write.Collection),
            Operations = write.Operations.Select(FromStorage).ToArray(),
        };
    }
}
