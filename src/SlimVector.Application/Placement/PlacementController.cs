using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using MemoryPack;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Routing;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Application.Placement;

public sealed record RebalanceAction(
    Guid OperationId,
    Guid CollectionId,
    string CollectionName,
    int ShardId,
    string SourceDataGroupId,
    string TargetDataGroupId,
    string Reason);

public sealed record RebalancePlan(
    Guid PlanId,
    DateTimeOffset CreatedAt,
    bool DryRun,
    IReadOnlyList<RebalanceAction> Actions);

public sealed record ShardMoveStatus(
    Guid OperationId,
    Guid CollectionId,
    string CollectionName,
    int ShardId,
    string SourceDataGroupId,
    string TargetDataGroupId,
    ShardPlacementState State,
    long RoutingEpoch,
    long SnapshotVersion,
    long ReplayedThroughVersion);

public sealed record PlacementControllerStatus(bool Paused, IReadOnlyList<ShardMoveStatus> Moves);

public interface IPlacementController
{
    ValueTask<RebalancePlan> PlanAsync(string? drainDataGroupId = null, CancellationToken cancellationToken = default);

    ValueTask<PlacementControllerStatus> ApproveAsync(Guid planId, CancellationToken cancellationToken = default);

    ValueTask<PlacementControllerStatus> GetStatusAsync(CancellationToken cancellationToken = default);

    ValueTask<PlacementControllerStatus> AdvanceAsync(CancellationToken cancellationToken = default);

    void Pause();

    void ResumeProcessing();
}

public sealed class PlacementController : IPlacementController, IDisposable
{
    private readonly IStorageEngine _storage;
    private readonly IConsensusCoordinator _consensus;
    private readonly TimeProvider _timeProvider;
    private readonly RebalancingOptions _options;
    private readonly IClusterTopologyStore? _topologyStore;
    private readonly IDataNodeQueryClient? _queryClient;
    private readonly ILocalDataQueryService? _localQueries;
    private readonly ILocalRaftGroupManager? _localGroups;
    private readonly IDataNodeRpcClient? _dataRpc;
    private readonly ILocalRaftCommandReplicator? _localReplicator;
    private readonly long _maximumTransferBytesPerSecond;
    private readonly ConcurrentDictionary<Guid, RebalancePlan> _plans = new();
    private readonly ConcurrentDictionary<(Guid CollectionId, int ShardId), DateTimeOffset> _lastMoveCompletion = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _paused;

    public PlacementController(
        IStorageEngine storage,
        IConsensusCoordinator consensus,
        TimeProvider timeProvider,
        IOptions<RebalancingOptions> options,
        IClusterTopologyStore? topologyStore = null,
        IDataNodeQueryClient? queryClient = null,
        ILocalDataQueryService? localQueries = null,
        ILocalRaftGroupManager? localGroups = null,
        IDataNodeRpcClient? dataRpc = null,
        ILocalRaftCommandReplicator? localReplicator = null,
        IOptions<DataPlacementOptions>? placementOptions = null)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(consensus);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(options);
        _storage = storage;
        _consensus = consensus;
        _timeProvider = timeProvider;
        _options = options.Value;
        _topologyStore = topologyStore;
        _queryClient = queryClient;
        _localQueries = localQueries;
        _localGroups = localGroups;
        _dataRpc = dataRpc;
        _localReplicator = localReplicator;
        _maximumTransferBytesPerSecond = placementOptions?.Value.MaximumTransferBytesPerSecond ?? long.MaxValue;
    }

    public async ValueTask<RebalancePlan> PlanAsync(
        string? drainDataGroupId = null,
        CancellationToken cancellationToken = default)
    {
        EnsureLeader();
        IReadOnlyList<CollectionDefinition> collections = await _storage.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
        string[] groups = _topologyStore is null
            ? _consensus.GetStatuses()
                .Select(static status => status.GroupId)
                .Where(static group => !string.Equals(group, MultiRaftNode.CatalogGroupId, StringComparison.Ordinal))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray()
            : (await _topologyStore.GetAsync(cancellationToken).ConfigureAwait(false)).DataGroups
                .Where(static group => group.State != DataGroupState.Removed)
                .Select(static group => group.GroupId)
                .Order(StringComparer.Ordinal)
                .ToArray();
        if (groups.Length == 0)
        {
            throw new InvalidOperationException("No data group is available for placement.");
        }

        if (drainDataGroupId is not null && !groups.Contains(drainDataGroupId, StringComparer.Ordinal))
        {
            throw new DomainException(ErrorCodes.InvalidPlacement, $"Data group '{drainDataGroupId}' does not exist.");
        }

        Dictionary<string, int> counts = groups.ToDictionary(static group => group, static _ => 0, StringComparer.Ordinal);
        foreach (CollectionDefinition collection in collections)
        {
            if (collection.Placement is null)
            {
                continue;
            }

            foreach (ShardPlacement shard in collection.Placement.Shards.Where(static shard => shard.State == ShardPlacementState.Active))
            {
                counts[shard.DataGroupId] = counts.GetValueOrDefault(shard.DataGroupId) + 1;
            }
        }

        List<RebalanceAction> actions = [];
        HashSet<string> busySources = new(StringComparer.Ordinal);
        HashSet<string> busyTargets = new(StringComparer.Ordinal);
        foreach (CollectionDefinition collection in collections.OrderBy(static item => item.Id))
        {
            if (collection.Placement is null)
            {
                continue;
            }

            foreach (ShardPlacement shard in collection.Placement.Shards
                         .Where(shard => shard.State == ShardPlacementState.Active &&
                             (drainDataGroupId is null || string.Equals(shard.DataGroupId, drainDataGroupId, StringComparison.Ordinal)))
                         .OrderBy(static shard => shard.ShardId))
            {
                if (_options.Cooldown > TimeSpan.Zero &&
                    _lastMoveCompletion.TryGetValue((collection.Id, shard.ShardId), out DateTimeOffset completedAt) &&
                    _timeProvider.GetUtcNow() - completedAt < _options.Cooldown)
                {
                    continue;
                }

                string target = counts
                    .Where(pair => !string.Equals(pair.Key, shard.DataGroupId, StringComparison.Ordinal))
                    .OrderBy(static pair => pair.Value)
                    .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => pair.Key)
                    .FirstOrDefault() ?? shard.DataGroupId;
                int gap = counts[shard.DataGroupId] - counts[target];
                double improvement = gap / (double)Math.Max(1, counts[shard.DataGroupId]);
                bool improvesBalance = drainDataGroupId is not null ||
                    gap > 1 && improvement >= _options.MinimumImprovementRatio;
                if (!improvesBalance || busySources.Contains(shard.DataGroupId) || busyTargets.Contains(target))
                {
                    continue;
                }

                actions.Add(new RebalanceAction(
                    Guid.NewGuid(),
                    collection.Id,
                    collection.Name,
                    shard.ShardId,
                    shard.DataGroupId,
                    target,
                    drainDataGroupId is null ? "balance virtual shards" : $"drain {drainDataGroupId}"));
                busySources.Add(shard.DataGroupId);
                busyTargets.Add(target);
                counts[shard.DataGroupId]--;
                counts[target]++;
                if (actions.Count >= _options.MaximumConcurrentMoves)
                {
                    break;
                }
            }

            if (actions.Count >= _options.MaximumConcurrentMoves)
            {
                break;
            }
        }

        RebalancePlan plan = new(Guid.NewGuid(), _timeProvider.GetUtcNow(), DryRun: true, actions);
        _plans[plan.PlanId] = plan;
        return plan;
    }

    public async ValueTask<PlacementControllerStatus> ApproveAsync(
        Guid planId,
        CancellationToken cancellationToken = default)
    {
        EnsureLeader();
        if (!_plans.TryRemove(planId, out RebalancePlan? plan))
        {
            throw new DomainException(ErrorCodes.InvalidPlacement, $"Rebalance plan '{planId}' was not found or expired.");
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach (RebalanceAction action in plan.Actions)
            {
                await BeginMoveAsync(action, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<PlacementControllerStatus> GetStatusAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CollectionDefinition> collections = await _storage.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
        return new PlacementControllerStatus(
            Volatile.Read(ref _paused) != 0,
            collections.SelectMany(collection => collection.Placement?.Shards
                    .Where(static shard => shard.State != ShardPlacementState.Active)
                    .Select(shard => new ShardMoveStatus(
                        shard.OperationId ?? Guid.Empty,
                        collection.Id,
                        collection.Name,
                        shard.ShardId,
                        shard.SourceDataGroupId ?? shard.DataGroupId,
                        shard.TargetDataGroupId ?? shard.DataGroupId,
                        shard.State,
                        collection.Placement.Epoch,
                        shard.SnapshotVersion,
                        shard.ReplayedThroughVersion)) ?? [])
                .OrderBy(static move => move.CollectionId)
                .ThenBy(static move => move.ShardId)
                .ToArray());
    }

    public async ValueTask<PlacementControllerStatus> AdvanceAsync(CancellationToken cancellationToken = default)
    {
        if (Volatile.Read(ref _paused) != 0)
        {
            return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
        }

        EnsureLeader();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            IReadOnlyList<CollectionDefinition> collections = await _storage.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
            foreach (CollectionDefinition collection in collections)
            {
                ShardPlacement? moving = collection.Placement?.Shards.FirstOrDefault(static shard =>
                    shard.State != ShardPlacementState.Active);
                if (moving is not null)
                {
                    await AdvanceMoveAsync(collection, moving, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }

        return await GetStatusAsync(cancellationToken).ConfigureAwait(false);
    }

    public void Pause() => Interlocked.Exchange(ref _paused, 1);

    public void ResumeProcessing() => Interlocked.Exchange(ref _paused, 0);

    public void Dispose() => _gate.Dispose();

    private async ValueTask BeginMoveAsync(RebalanceAction action, CancellationToken cancellationToken)
    {
        CollectionDefinition collection = await GetCollectionAsync(action.CollectionId, cancellationToken).ConfigureAwait(false);
        CollectionPlacement placement = collection.Placement ??
            throw new DomainException(ErrorCodes.InvalidPlacement, $"Collection '{collection.Name}' has no placement.");
        ShardPlacement current = placement.Shards[action.ShardId];
        if (current.State != ShardPlacementState.Active ||
            !string.Equals(current.DataGroupId, action.SourceDataGroupId, StringComparison.Ordinal))
        {
            throw new DomainException(ErrorCodes.MembershipConflict, "The shard placement changed after the plan was generated.");
        }

        ShardPlacement moving = current with
        {
            State = ShardPlacementState.Copying,
            SourceDataGroupId = current.DataGroupId,
            TargetDataGroupId = action.TargetDataGroupId,
            OperationId = action.OperationId,
            SnapshotVersion = 0,
            ReplayedThroughVersion = 0,
        };
        await PersistShardAsync(collection, moving, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask AdvanceMoveAsync(
        CollectionDefinition collection,
        ShardPlacement moving,
        CancellationToken cancellationToken)
    {
        DocumentRecord[] shardDocuments = (await LoadGroupDocumentsAsync(
                collection,
                moving.SourceDataGroupId ?? moving.DataGroupId,
                cancellationToken).ConfigureAwait(false))
            .Where(document => collection.Placement!.Resolve(collection.Id, document.Id).ShardId == moving.ShardId)
            .OrderBy(static document => document.Id, StringComparer.Ordinal)
            .ToArray();
        ShardPlacement next;
        switch (moving.State)
        {
            case ShardPlacementState.Copying:
                {
                    long snapshotVersion = MaximumVersion(shardDocuments);
                    ShardMoveCheckpoint checkpoint = new()
                    {
                        OperationId = moving.OperationId!.Value,
                        CollectionId = collection.Id,
                        ShardId = moving.ShardId,
                        SnapshotVersion = snapshotVersion,
                        ReplayedThroughVersion = snapshotVersion,
                        SnapshotDocuments = shardDocuments.Select(RaftCommandCodec.FromDomain).ToArray(),
                        SnapshotChecksum = ComputeDocumentChecksum(shardDocuments),
                        CurrentChecksum = ComputeDocumentChecksum(shardDocuments),
                    };
                    await WriteCheckpointAsync(collection.Id, checkpoint, cancellationToken).ConfigureAwait(false);
                    await ReplicateShardTransferAsync(
                        moving.TargetDataGroupId!,
                        collection,
                        shardDocuments.Select(StorageOperation.Upsert).ToArray(),
                        moving.ShardId,
                        cancellationToken).ConfigureAwait(false);
                    next = moving with
                    {
                        State = ShardPlacementState.CatchingUp,
                        SnapshotVersion = snapshotVersion,
                        ReplayedThroughVersion = snapshotVersion,
                    };
                    break;
                }
            case ShardPlacementState.CatchingUp:
                {
                    ShardMoveCheckpoint checkpoint = await ReadCheckpointAsync(collection.Id, moving, cancellationToken)
                        .ConfigureAwait(false);
                    checkpoint.Delta = BuildDelta(checkpoint.SnapshotDocuments, shardDocuments);
                    checkpoint.ReplayedThroughVersion = MaximumVersion(shardDocuments);
                    checkpoint.CurrentChecksum = ComputeDocumentChecksum(shardDocuments);
                    ValidateCheckpoint(checkpoint, shardDocuments);
                    await WriteCheckpointAsync(collection.Id, checkpoint, cancellationToken).ConfigureAwait(false);
                    await ReplicateShardTransferAsync(
                        moving.TargetDataGroupId!,
                        collection,
                        checkpoint.Delta.Select(RaftCommandCodec.ToStorage).ToArray(),
                        moving.ShardId,
                        cancellationToken).ConfigureAwait(false);
                    DocumentRecord[] targetDocuments = (await LoadGroupDocumentsAsync(
                            collection,
                            moving.TargetDataGroupId!,
                            cancellationToken).ConfigureAwait(false))
                        .Where(document => collection.Placement!.Resolve(collection.Id, document.Id).ShardId == moving.ShardId)
                        .OrderBy(static document => document.Id, StringComparer.Ordinal)
                        .ToArray();
                    if (!string.Equals(
                            ComputeDocumentChecksum(targetDocuments),
                            checkpoint.CurrentChecksum,
                            StringComparison.Ordinal))
                    {
                        StorageOperation[] repair = BuildDelta(
                                targetDocuments.Select(RaftCommandCodec.FromDomain).ToArray(),
                                shardDocuments)
                            .Select(RaftCommandCodec.ToStorage)
                            .ToArray();
                        await ReplicateShardTransferAsync(
                            moving.TargetDataGroupId!,
                            collection,
                            repair,
                            moving.ShardId,
                            cancellationToken).ConfigureAwait(false);
                        next = moving with
                        {
                            State = ShardPlacementState.CatchingUp,
                            ReplayedThroughVersion = checkpoint.ReplayedThroughVersion,
                        };
                        break;
                    }

                    next = moving with
                    {
                        State = ShardPlacementState.Switching,
                        ReplayedThroughVersion = checkpoint.ReplayedThroughVersion,
                    };
                    break;
                }
            case ShardPlacementState.Switching:
                {
                    ShardMoveCheckpoint checkpoint = await ReadCheckpointAsync(collection.Id, moving, cancellationToken)
                        .ConfigureAwait(false);
                    if (!string.Equals(checkpoint.CurrentChecksum, ComputeDocumentChecksum(shardDocuments), StringComparison.Ordinal))
                    {
                        next = moving with { State = ShardPlacementState.CatchingUp };
                        break;
                    }

                    ValidateCheckpoint(checkpoint, shardDocuments);
                    next = moving with { State = ShardPlacementState.Draining };
                    break;
                }
            case ShardPlacementState.Draining:
                {
                    ShardMoveCheckpoint checkpoint = await ReadCheckpointAsync(collection.Id, moving, cancellationToken)
                        .ConfigureAwait(false);
                    HashSet<string> transferredIds = checkpoint.SnapshotDocuments.Select(static document => document.Id)
                        .ToHashSet(StringComparer.Ordinal);
                    foreach (RaftStorageOperation operation in checkpoint.Delta)
                    {
                        if (operation.Kind == DocumentMutationKind.Delete)
                        {
                            transferredIds.Remove(operation.Id);
                        }
                        else
                        {
                            transferredIds.Add(operation.Id);
                        }
                    }

                    await ReplicateShardTransferAsync(
                        moving.SourceDataGroupId!,
                        collection,
                        transferredIds.Select(static id => StorageOperation.Delete(id, long.MaxValue)).ToArray(),
                        moving.ShardId,
                        cancellationToken).ConfigureAwait(false);
                    next = moving with
                    {
                        State = ShardPlacementState.Active,
                        DataGroupId = moving.TargetDataGroupId!,
                        SourceDataGroupId = null,
                        TargetDataGroupId = null,
                        OperationId = null,
                    };
                    break;
                }
            default:
                next = moving;
                break;
        }

        if (next.State == ShardPlacementState.Switching && next.ReplayedThroughVersion < next.SnapshotVersion)
        {
            throw new InvalidOperationException("The shard delta has not caught up to its snapshot version.");
        }

        await PersistShardAsync(collection, next, cancellationToken).ConfigureAwait(false);
        if (moving.State == ShardPlacementState.Draining && next.State == ShardPlacementState.Active)
        {
            _lastMoveCompletion[(collection.Id, moving.ShardId)] = _timeProvider.GetUtcNow();
        }
    }

    private async ValueTask<ShardMoveCheckpoint> ReadCheckpointAsync(
        Guid collectionId,
        ShardPlacement moving,
        CancellationToken cancellationToken)
    {
        byte[] data = await _storage.ReadDerivedDataAsync(
                collectionId,
                GetCheckpointName(moving.OperationId!.Value),
                cancellationToken)
            .ConfigureAwait(false) ?? throw new InvalidOperationException("The shard-move checkpoint is missing.");
        ShardMoveCheckpoint checkpoint = MemoryPackSerializer.Deserialize<ShardMoveCheckpoint>(data) ??
            throw new InvalidOperationException("The shard-move checkpoint is invalid.");
        if (checkpoint.OperationId != moving.OperationId || checkpoint.CollectionId != collectionId ||
            checkpoint.ShardId != moving.ShardId)
        {
            throw new InvalidOperationException("The shard-move checkpoint does not match the active placement operation.");
        }

        return checkpoint;
    }

    private ValueTask WriteCheckpointAsync(
        Guid collectionId,
        ShardMoveCheckpoint checkpoint,
        CancellationToken cancellationToken) =>
        _storage.WriteDerivedDataAsync(
            collectionId,
            GetCheckpointName(checkpoint.OperationId),
            MemoryPackSerializer.Serialize(checkpoint),
            cancellationToken);

    private static RaftStorageOperation[] BuildDelta(
        IReadOnlyList<RaftDocument> snapshotDocuments,
        IReadOnlyList<DocumentRecord> currentDocuments)
    {
        Dictionary<string, RaftDocument> snapshot = snapshotDocuments.ToDictionary(
            static document => document.Id,
            StringComparer.Ordinal);
        Dictionary<string, DocumentRecord> current = currentDocuments.ToDictionary(
            static document => document.Id,
            StringComparer.Ordinal);
        List<RaftStorageOperation> delta = [];
        foreach (RaftDocument document in snapshotDocuments.OrderBy(static document => document.Id, StringComparer.Ordinal))
        {
            if (!current.ContainsKey(document.Id))
            {
                delta.Add(RaftCommandCodec.FromStorage(StorageOperation.Delete(
                    document.Id,
                    checked(document.Version + 1))));
            }
        }

        foreach (DocumentRecord document in currentDocuments.OrderBy(static document => document.Id, StringComparer.Ordinal))
        {
            if (!snapshot.TryGetValue(document.Id, out RaftDocument? prior) ||
                !string.Equals(
                    ComputeDocumentChecksum([RaftCommandCodec.ToDomain(prior)]),
                    ComputeDocumentChecksum([document]),
                    StringComparison.Ordinal))
            {
                delta.Add(RaftCommandCodec.FromStorage(StorageOperation.Upsert(document)));
            }
        }

        return delta.ToArray();
    }

    private static void ValidateCheckpoint(
        ShardMoveCheckpoint checkpoint,
        IReadOnlyList<DocumentRecord> currentDocuments)
    {
        Dictionary<string, DocumentRecord> reconstructed = checkpoint.SnapshotDocuments
            .Select(RaftCommandCodec.ToDomain)
            .ToDictionary(static document => document.Id, StringComparer.Ordinal);
        foreach (RaftStorageOperation operation in checkpoint.Delta)
        {
            if (operation.Kind == DocumentMutationKind.Delete)
            {
                reconstructed.Remove(operation.Id);
            }
            else if (operation.Document is not null)
            {
                reconstructed[operation.Id] = RaftCommandCodec.ToDomain(operation.Document);
            }
        }

        string reconstructedChecksum = ComputeDocumentChecksum(reconstructed.Values);
        string currentChecksum = ComputeDocumentChecksum(currentDocuments);
        if (!string.Equals(reconstructedChecksum, currentChecksum, StringComparison.Ordinal) ||
            !string.Equals(checkpoint.CurrentChecksum, currentChecksum, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The shard snapshot and ordered delta do not reconstruct the source state.");
        }
    }

    private async ValueTask<IReadOnlyList<DocumentRecord>> LoadGroupDocumentsAsync(
        CollectionDefinition collection,
        string groupId,
        CancellationToken cancellationToken)
    {
        if (_queryClient is null || _localQueries is null || _localGroups is null)
        {
            return (await _storage.LoadDocumentsAsync(collection.Id, cancellationToken).ConfigureAwait(false)).Values.ToArray();
        }

        HashSet<string> hosted = _localGroups.GetHostedDataGroupIds().ToHashSet(StringComparer.Ordinal);
        return hosted.Contains(groupId)
            ? await _localQueries.GetRawDocumentsLocalAsync(
                collection.Name,
                groupId,
                ReadConsistency.Linearizable,
                cancellationToken).ConfigureAwait(false)
            : await _queryClient.GetRawDocumentsAsync(
                collection.Name,
                groupId,
                ReadConsistency.Linearizable,
                cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask ReplicateShardTransferAsync(
        string groupId,
        CollectionDefinition collection,
        StorageOperation[] operations,
        int shardId,
        CancellationToken cancellationToken)
    {
        IDataNodeRpcClient? dataRpc = _dataRpc;
        ILocalRaftCommandReplicator? localReplicator = _localReplicator;
        ILocalRaftGroupManager? localGroups = _localGroups;
        if (operations.Length == 0 || dataRpc is null || localReplicator is null || localGroups is null)
        {
            return;
        }

        bool local = localGroups.GetHostedDataGroupIds().Contains(groupId, StringComparer.Ordinal);
        foreach (StorageOperation[] batch in ChunkTransferOperations(operations))
        {
            RaftCommandEnvelope command = RaftCommandCodec.ShardTransfer(
                Guid.NewGuid(),
                groupId,
                collection,
                batch,
                shardId);
            int payloadBytes = RaftCommandCodec.Serialize(command).Length;
            long started = Stopwatch.GetTimestamp();
            if (local)
            {
                await localReplicator.ReplicateLocalAsync(command, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await dataRpc.ReplicateAsync(command, cancellationToken).ConfigureAwait(false);
            }

            if (_maximumTransferBytesPerSecond < long.MaxValue)
            {
                TimeSpan minimum = TimeSpan.FromSeconds(payloadBytes / (double)_maximumTransferBytesPerSecond);
                TimeSpan remaining = minimum - Stopwatch.GetElapsedTime(started);
                if (remaining > TimeSpan.Zero)
                {
                    await Task.Delay(remaining, cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private static List<StorageOperation[]> ChunkTransferOperations(StorageOperation[] operations)
    {
        const long targetBytes = 4L * 1_024 * 1_024;
        List<StorageOperation[]> batches = [];
        List<StorageOperation> current = [];
        long currentBytes = 0;
        foreach (StorageOperation operation in operations)
        {
            long estimatedBytes = EstimateTransferBytes(operation);
            if (current.Count > 0 && currentBytes + estimatedBytes > targetBytes)
            {
                batches.Add(current.ToArray());
                current.Clear();
                currentBytes = 0;
            }

            current.Add(operation);
            currentBytes = checked(currentBytes + estimatedBytes);
        }

        if (current.Count > 0)
        {
            batches.Add(current.ToArray());
        }

        return batches;
    }

    private static long EstimateTransferBytes(StorageOperation operation) => operation.Document is null
        ? 128 + operation.Id.Length * sizeof(char)
        : 256L + operation.Id.Length * sizeof(char) + operation.Document.Text.Length * sizeof(char) +
          operation.Document.Vector.LongLength * sizeof(float) + operation.Document.Metadata.Count * 128L;

    private static string ComputeDocumentChecksum(IEnumerable<DocumentRecord> documents)
    {
        RaftDocument[] serializable = documents
            .OrderBy(static document => document.Id, StringComparer.Ordinal)
            .Select(RaftCommandCodec.FromDomain)
            .ToArray();
        return Convert.ToHexStringLower(SHA256.HashData(MemoryPackSerializer.Serialize(serializable)));
    }

    private static long MaximumVersion(IEnumerable<DocumentRecord> documents) =>
        documents.Select(static document => document.Version).DefaultIfEmpty().Max();

    private static string GetCheckpointName(Guid operationId) => $"shard-move-{operationId:N}";

    private async ValueTask PersistShardAsync(
        CollectionDefinition collection,
        ShardPlacement replacement,
        CancellationToken cancellationToken)
    {
        CollectionPlacement placement = collection.Placement!;
        ShardPlacement[] shards = (ShardPlacement[])placement.Shards.Clone();
        shards[replacement.ShardId] = replacement;
        CollectionDefinition updated = collection with
        {
            Placement = placement with { Epoch = checked(placement.Epoch + 1), Shards = shards },
            UpdatedAt = _timeProvider.GetUtcNow(),
        };
        await _consensus.UpsertCollectionAsync(updated, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<CollectionDefinition> GetCollectionAsync(Guid collectionId, CancellationToken cancellationToken)
    {
        IReadOnlyList<CollectionDefinition> collections = await _storage.ListCollectionsAsync(cancellationToken).ConfigureAwait(false);
        return collections.FirstOrDefault(collection => collection.Id == collectionId) ??
            throw new DomainException(ErrorCodes.CollectionNotFound, $"Collection '{collectionId}' was not found.");
    }

    private void EnsureLeader()
    {
        if (_consensus.Mode == ExecutionMode.SingleNode)
        {
            return;
        }

        RaftGroupStatus catalog = _consensus.GetStatuses().Single(static status =>
            string.Equals(status.GroupId, MultiRaftNode.CatalogGroupId, StringComparison.Ordinal));
        if (!catalog.IsLeader)
        {
            throw new DomainException(ErrorCodes.MembershipConflict, "Only the catalog leader may run the placement controller.");
        }
    }
}

[MemoryPackable]
internal sealed partial class ShardMoveCheckpoint
{
    public int FormatVersion { get; set; } = 1;

    public Guid OperationId { get; set; }

    public Guid CollectionId { get; set; }

    public int ShardId { get; set; }

    public long SnapshotVersion { get; set; }

    public long ReplayedThroughVersion { get; set; }

    public RaftDocument[] SnapshotDocuments { get; set; } = [];

    public RaftStorageOperation[] Delta { get; set; } = [];

    public string SnapshotChecksum { get; set; } = string.Empty;

    public string CurrentChecksum { get; set; } = string.Empty;
}

public sealed class PlacementControllerHostedService(
    IPlacementController controller,
    IOptions<RebalancingOptions> options) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using PeriodicTimer timer = new(options.Value.ReconcileInterval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                PlacementControllerStatus status = await controller.GetStatusAsync(stoppingToken).ConfigureAwait(false);
                if (status.Moves.Count > 0)
                {
                    _ = await controller.AdvanceAsync(stoppingToken).ConfigureAwait(false);
                }
                else if (!options.Value.ManualApproval && !status.Paused)
                {
                    RebalancePlan plan = await controller.PlanAsync(cancellationToken: stoppingToken).ConfigureAwait(false);
                    if (plan.Actions.Count > 0)
                    {
                        _ = await controller.ApproveAsync(plan.PlanId, stoppingToken).ConfigureAwait(false);
                    }
                }
            }
            catch (DomainException exception) when (exception.Code == ErrorCodes.MembershipConflict)
            {
                // A follower remains passive until it becomes catalog leader.
            }
        }
    }
}
