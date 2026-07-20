using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Placement;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Tests;

public sealed class PlacementControllerTests
{
    [Fact]
    public async Task ApprovedShardMovePersistsAndResumesAfterControllerRestart()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        string path = Path.Combine(Path.GetTempPath(), "SlimVector.Placement.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        try
        {
            using FileSystemStorageEngine storage = new(new StorageSettings { Path = path, FlushToDisk = false });
            await storage.InitializeAsync(cancellationToken);
            Guid collectionId = Guid.Parse("20000000-0000-0000-0000-000000000001");
            CollectionPlacement placement = CollectionPlacement.Create(collectionId, ["data-0"], 8);
            CollectionDefinition collection = CollectionDefinition.Create("move-test", 2, DistanceMetric.Cosine) with
            {
                Id = collectionId,
                Placement = placement,
            };
            await storage.CreateCollectionAsync(collection, cancellationToken);
            DocumentRecord[] documents = Enumerable.Range(0, 16).Select(index => new DocumentRecord
            {
                Id = $"doc-{index}",
                Text = "move verification",
                Vector = [1, index],
                Metadata = [],
                Version = index + 1,
                UpdatedAt = DateTimeOffset.UtcNow,
            }).ToArray();
            await storage.AppendAsync(collection.Id, documents.Select(StorageOperation.Upsert).ToArray(), cancellationToken);
            RecordingPlacementConsensus consensus = new(storage);
            IOptions<RebalancingOptions> options = Options.Create(new RebalancingOptions
            {
                MaximumConcurrentMoves = 1,
                ReconcileInterval = TimeSpan.FromMilliseconds(10),
            });
            Guid operationId;
            int movingShard;
            using (PlacementController first = new(storage, consensus, TimeProvider.System, options))
            {
                RebalancePlan plan = await first.PlanAsync("data-0", cancellationToken);
                RebalanceAction action = Assert.Single(plan.Actions);
                operationId = action.OperationId;
                movingShard = action.ShardId;
                PlacementControllerStatus approved = await first.ApproveAsync(plan.PlanId, cancellationToken);
                Assert.Equal(ShardPlacementState.Copying, Assert.Single(approved.Moves).State);
            }

            using PlacementController resumed = new(storage, consensus, TimeProvider.System, options);
            Assert.Equal(operationId, Assert.Single((await resumed.GetStatusAsync(cancellationToken)).Moves).OperationId);
            string movedDocumentId = Enumerable.Range(0, 10_000)
                .Select(static index => $"moving-{index}")
                .First(id => placement.Resolve(collection.Id, id).ShardId == movingShard);
            DocumentRecord movedDocument = new()
            {
                Id = movedDocumentId,
                Text = "snapshot value",
                Vector = [1, 1],
                Metadata = [],
                Version = 50,
                UpdatedAt = DateTimeOffset.UtcNow,
            };
            await storage.AppendAsync(collection.Id, [StorageOperation.Upsert(movedDocument)], cancellationToken);

            PlacementControllerStatus copying = await resumed.AdvanceAsync(cancellationToken);
            ShardMoveStatus catchingUp = Assert.Single(copying.Moves);
            Assert.Equal(ShardPlacementState.CatchingUp, catchingUp.State);
            Assert.Equal(50, catchingUp.SnapshotVersion);
            Assert.NotNull(await storage.ReadDerivedDataAsync(
                collection.Id,
                $"shard-move-{operationId:N}",
                cancellationToken));

            await storage.AppendAsync(
                collection.Id,
                [StorageOperation.Upsert(movedDocument with { Text = "delta value", Version = 100 })],
                cancellationToken);
            ShardMoveStatus switching = Assert.Single((await resumed.AdvanceAsync(cancellationToken)).Moves);
            Assert.Equal(ShardPlacementState.Switching, switching.State);
            Assert.Equal(100, switching.ReplayedThroughVersion);
            Assert.Equal(ShardPlacementState.Draining, Assert.Single((await resumed.AdvanceAsync(cancellationToken)).Moves).State);
            _ = await resumed.AdvanceAsync(cancellationToken);

            Assert.Empty((await resumed.GetStatusAsync(cancellationToken)).Moves);
            CollectionDefinition persisted = Assert.Single(await storage.ListCollectionsAsync(cancellationToken));
            Assert.Equal("data-1", persisted.Placement!.Shards[movingShard].DataGroupId);
            Assert.True(persisted.Placement.Epoch >= 6);
            IReadOnlyDictionary<string, DocumentRecord> restored = await storage.LoadDocumentsAsync(collection.Id, cancellationToken);
            Assert.Equal(documents.Length + 1, restored.Count);
            Assert.Equal("delta value", restored[movedDocumentId].Text);
            Assert.Equal(100, restored[movedDocumentId].Version);
        }
        finally
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private sealed class RecordingPlacementConsensus(IStorageEngine storage) : IConsensusCoordinator
    {
        public event Action<Guid?>? StateChanged;

        public ExecutionMode Mode => ExecutionMode.Cluster;

        public bool IsReady => true;

        public IReadOnlyList<RaftGroupStatus> GetStatuses() =>
        [
            Status(MultiRaftNode.CatalogGroupId, isLeader: true),
            Status("data-0", isLeader: true),
            Status("data-1", isLeader: true),
        ];

        public ValueTask StartAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask StopAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public string GetDataGroupId(Guid collectionId) => "data-0";

        public async ValueTask UpsertCollectionAsync(
            CollectionDefinition collection,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<CollectionDefinition> current = await storage.ListCollectionsAsync(cancellationToken);
            if (current.Any(item => item.Id == collection.Id))
            {
                await storage.UpdateCollectionAsync(collection, cancellationToken);
            }
            else
            {
                await storage.CreateCollectionAsync(collection, cancellationToken);
            }

            StateChanged?.Invoke(collection.Id);
        }

        public ValueTask DeleteCollectionAsync(
            CollectionDefinition collection,
            CancellationToken cancellationToken = default) =>
            storage.DeleteCollectionAsync(collection.Name, cancellationToken);

        public ValueTask AppendAsync(
            CollectionDefinition collection,
            IReadOnlyList<StorageOperation> operations,
            CancellationToken cancellationToken = default) =>
            storage.AppendAsync(collection.Id, operations, cancellationToken);

        public async ValueTask AppendBatchAsync(
            IReadOnlyList<CollectionWrite> writes,
            CancellationToken cancellationToken = default)
        {
            foreach (CollectionWrite write in writes)
            {
                await storage.AppendAsync(write.Collection.Id, write.Operations, cancellationToken);
            }
        }

        public ValueTask ApplyReadBarrierAsync(
            Guid? collectionId,
            ReadConsistency consistency,
            CancellationToken cancellationToken = default) => ValueTask.CompletedTask;

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static RaftGroupStatus Status(string groupId, bool isLeader) => new()
        {
            GroupId = groupId,
            LocalEndpoint = "local",
            LeaderEndpoint = "local",
            IsLeader = isLeader,
            Term = 1,
            LastAppliedIndex = 1,
            LastCommittedIndex = 1,
            AppliedCommandCount = 1,
        };
    }
}
