using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SlimVector.Domain;
using SlimVector.Raft.Commands;
using SlimVector.Storage;

namespace SlimVector.Raft.Tests;

[Collection(RaftNetworkTestGroup.Name)]
public sealed class RaftClusterTests
{
    private static readonly TimeSpan ElectionTimeout = TimeSpan.FromSeconds(15);

    [Fact(Timeout = 60_000)]
    public async Task ThreeNodesElectReplicateFailOverAndCatchUp()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        IPEndPoint[] endpoints = AllocateLoopbackEndpoints(3);
        RecordingCommandApplier[] appliers = [new(), new(), new()];
        RaftGroupNode?[] nodes = new RaftGroupNode?[3];
        for (int index = 0; index < nodes.Length; index++)
        {
            nodes[index] = CreateNode("catalog", endpoints[index], endpoints, directory.Path, index, appliers[index]);
        }

        try
        {
            await Task.WhenAll(nodes.Select(node => node!.StartAsync(cancellationToken).AsTask()));
            EndPoint[] elected = await Task.WhenAll(nodes.Select(node => node!.WaitForLeaderAsync(ElectionTimeout, cancellationToken).AsTask()));
            Assert.All(elected, endpoint => Assert.Equal(elected[0], endpoint));
            int firstLeaderIndex = Array.FindIndex(nodes, node => Equals(node!.LocalEndpoint, elected[0]));
            Assert.InRange(firstLeaderIndex, 0, 2);
            RaftGroupNode firstLeader = nodes[firstLeaderIndex]!;
            await WaitUntilAsync(() => firstLeader.IsLeader, ElectionTimeout, cancellationToken);

            RaftCommandEnvelope firstCommand = RaftCommandCodec.CatalogDelete(
                Guid.Parse("10000000-0000-0000-0000-000000000001"),
                "catalog",
                Guid.Parse("20000000-0000-0000-0000-000000000001"),
                "obsolete");
            await firstLeader.ReplicateAsync(firstCommand, cancellationToken);
            await WaitUntilAsync(() => appliers.All(static applier => applier.Commands.Count == 1), ElectionTimeout, cancellationToken);

            await firstLeader.StopAsync(cancellationToken);
            await WaitUntilAsync(
                () => nodes.Where((_, index) => index != firstLeaderIndex).Any(static node => node!.IsLeader),
                ElectionTimeout,
                cancellationToken);
            RaftGroupNode secondLeader = nodes
                .Where((_, index) => index != firstLeaderIndex)
                .Single(static node => node!.IsLeader)!;
            Assert.NotEqual(firstLeader.LocalEndpoint, secondLeader.LocalEndpoint);

            RaftCommandEnvelope secondCommand = RaftCommandCodec.CatalogDelete(
                Guid.Parse("10000000-0000-0000-0000-000000000002"),
                "catalog",
                Guid.Parse("20000000-0000-0000-0000-000000000002"),
                "expired");
            await secondLeader.ReplicateAsync(secondCommand, cancellationToken);
            await WaitUntilAsync(
                () => appliers.Where((_, index) => index != firstLeaderIndex).All(static applier => applier.Commands.Count == 2),
                ElectionTimeout,
                cancellationToken);

            await firstLeader.DisposeAsync();
            nodes[firstLeaderIndex] = CreateNode(
                "catalog",
                endpoints[firstLeaderIndex],
                endpoints,
                directory.Path,
                firstLeaderIndex,
                appliers[firstLeaderIndex]);
            await nodes[firstLeaderIndex]!.StartAsync(cancellationToken);
            await WaitUntilAsync(() => appliers[firstLeaderIndex].Commands.Count == 2, ElectionTimeout, cancellationToken);

            RaftGroupNode currentLeader = nodes.Single(static node => node!.IsLeader)!;
            await currentLeader.ReplicateAsync(firstCommand, cancellationToken);
            await Task.Delay(250, cancellationToken);
            Assert.All(appliers, static applier => Assert.Equal(2, applier.Commands.Count));
            Assert.All(nodes, static node => Assert.Equal(2, node!.AppliedCommandCount));
        }
        finally
        {
            foreach (RaftGroupNode? node in nodes)
            {
                if (node is not null)
                {
                    await node.DisposeAsync();
                }
            }
        }
    }

    [Fact(Timeout = 45_000)]
    public async Task MultiRaftRunsCatalogAndDataGroupsIndependently()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        IPEndPoint[] endpoints = AllocateLoopbackEndpoints(3);
        string[] groupIds = [MultiRaftNode.CatalogGroupId, "data-0", "data-1"];
        Dictionary<string, RecordingCommandApplier> appliers = groupIds.ToDictionary(
            static groupId => groupId,
            static _ => new RecordingCommandApplier(),
            StringComparer.Ordinal);
        RaftGroupNodeOptions[] options = groupIds.Select((groupId, index) => Options(
            groupId,
            endpoints[index],
            [endpoints[index]],
            directory.Path,
            index)).ToArray();

        await using MultiRaftNode node = new(options, groupId => appliers[groupId]);
        await node.StartAsync(cancellationToken);
        foreach (string groupId in groupIds)
        {
            await node.GetGroup(groupId).WaitForLeaderAsync(ElectionTimeout, cancellationToken);
        }

        CollectionDefinition collection = CollectionDefinition.Create("articles", 2, DistanceMetric.Cosine);
        string dataGroupId = node.GetDataGroupId(collection.Id);
        await node.ReplicateCatalogAsync(
            RaftCommandCodec.CatalogUpsert(Guid.NewGuid(), MultiRaftNode.CatalogGroupId, collection, dataGroupId),
            cancellationToken);
        await node.ReplicateDataAsync(
            collection.Id,
            RaftCommandCodec.DataBatch(Guid.NewGuid(), dataGroupId, collection, []),
            cancellationToken);

        Assert.Single(appliers[MultiRaftNode.CatalogGroupId].Commands);
        Assert.Single(appliers[dataGroupId].Commands);
        string idleDataGroup = groupIds.Single(groupId => groupId.StartsWith("data-", StringComparison.Ordinal) && groupId != dataGroupId);
        Assert.Empty(appliers[idleDataGroup].Commands);
        Assert.All(groupIds, groupId => Assert.True(node.GetGroup(groupId).IsLeader));
    }

    [Fact(Timeout = 60_000)]
    public async Task LateFollowerCatchesUpFromPersistentApplicationSnapshot()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        IPEndPoint[] endpoints = AllocateLoopbackEndpoints(3);
        FileSystemStorageEngine[] storages = Enumerable.Range(0, 3)
            .Select(index => new FileSystemStorageEngine(new StorageSettings
            {
                Path = Path.Combine(directory.Path, $"application-{index}"),
                FlushToDisk = false,
            }))
            .ToArray();
        StorageRaftCommandApplier[] appliers = storages.Select(static storage => new StorageRaftCommandApplier(storage)).ToArray();
        RaftGroupNode[] nodes = Enumerable.Range(0, 3)
            .Select(index => new RaftGroupNode(
                Options("data-0", endpoints[index], endpoints, directory.Path, index) with { SnapshotEveryEntries = 1 },
                appliers[index]))
            .ToArray();
        DateTimeOffset timestamp = new(2026, 7, 18, 13, 0, 0, TimeSpan.Zero);
        CollectionDefinition collection = new()
        {
            Id = Guid.Parse("40000000-0000-0000-0000-000000000001"),
            Name = "snapshot-catchup",
            Dimension = 2,
            Metric = DistanceMetric.Cosine,
            CreatedAt = timestamp,
            UpdatedAt = timestamp,
        };

        try
        {
            await Task.WhenAll(storages.Select(storage => storage.InitializeAsync(cancellationToken).AsTask()));
            await Task.WhenAll(nodes.Take(2).Select(node => node.StartAsync(cancellationToken).AsTask()));
            EndPoint elected = await nodes[0].WaitForLeaderAsync(ElectionTimeout, cancellationToken);
            RaftGroupNode leader = nodes.Take(2).Single(node => Equals(node.LocalEndpoint, elected));
            await WaitUntilAsync(() => leader.IsLeader, ElectionTimeout, cancellationToken);
            for (int version = 1; version <= 5; version++)
            {
                DocumentRecord document = new()
                {
                    Id = "document",
                    Text = $"version-{version}",
                    Vector = [version, 0],
                    Metadata = [],
                    Version = version,
                    UpdatedAt = timestamp.AddSeconds(version),
                };
                await leader.ReplicateAsync(
                    RaftCommandCodec.DataBatch(
                        Guid.Parse($"50000000-0000-0000-0000-{version:D12}"),
                        "data-0",
                        collection,
                        [StorageOperation.Upsert(document)]),
                    cancellationToken);
            }

            await nodes[2].StartAsync(cancellationToken);
            await WaitUntilAsync(
                () => TryReadFinalDocumentAsync(storages[2], collection.Id, cancellationToken),
                ElectionTimeout,
                cancellationToken);
            IReadOnlyDictionary<string, DocumentRecord> restored = await storages[2]
                .LoadDocumentsAsync(collection.Id, cancellationToken);
            Assert.Equal("version-5", restored["document"].Text);
            Assert.Equal(5, restored["document"].Version);
        }
        finally
        {
            foreach (RaftGroupNode node in nodes)
            {
                await node.DisposeAsync();
            }

            foreach (StorageRaftCommandApplier applier in appliers)
            {
                applier.Dispose();
            }

            foreach (FileSystemStorageEngine storage in storages)
            {
                storage.Dispose();
            }
        }
    }

    [Fact(Timeout = 60_000)]
    public async Task QuorumPartitionBlocksCommitAndRecoveryResumesReplication()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        using TemporaryDirectory directory = new();
        IPEndPoint[] endpoints = AllocateLoopbackEndpoints(3);
        RecordingCommandApplier[] appliers = [new(), new(), new()];
        RaftGroupNode?[] nodes = Enumerable.Range(0, 3)
            .Select(index => CreateNode("catalog", endpoints[index], endpoints, directory.Path, index, appliers[index]))
            .ToArray();
        Guid recoveryCommandId = Guid.Parse("70000000-0000-0000-0000-000000000002");
        try
        {
            await Task.WhenAll(nodes.Select(node => node!.StartAsync(cancellationToken).AsTask()));
            EndPoint elected = await nodes[0]!.WaitForLeaderAsync(ElectionTimeout, cancellationToken);
            int leaderIndex = Array.FindIndex(nodes, node => Equals(node!.LocalEndpoint, elected));
            RaftGroupNode leader = nodes[leaderIndex]!;
            await WaitUntilAsync(() => leader.IsLeader, ElectionTimeout, cancellationToken);
            int[] followerIndexes = Enumerable.Range(0, 3).Where(index => index != leaderIndex).ToArray();
            await Task.WhenAll(followerIndexes.Select(index => nodes[index]!.StopAsync(cancellationToken).AsTask()));

            using (CancellationTokenSource partitionTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                partitionTimeout.CancelAfter(TimeSpan.FromSeconds(2));
                ConsensusUnavailableException failure = await Assert.ThrowsAsync<ConsensusUnavailableException>(() => leader.ReplicateAsync(
                    RaftCommandCodec.CatalogDelete(
                        Guid.Parse("70000000-0000-0000-0000-000000000001"),
                        "catalog",
                        Guid.NewGuid(),
                        "partitioned"),
                    partitionTimeout.Token).AsTask());
                Assert.Equal(ConsensusFailureKind.NotLeader, failure.Kind);
            }

            int returningIndex = followerIndexes[0];
            await nodes[returningIndex]!.DisposeAsync();
            nodes[returningIndex] = CreateNode(
                "catalog",
                endpoints[returningIndex],
                endpoints,
                directory.Path,
                returningIndex,
                appliers[returningIndex]);
            await nodes[returningIndex]!.StartAsync(cancellationToken);
            EndPoint recoveredLeaderEndpoint = await nodes[returningIndex]!
                .WaitForLeaderAsync(ElectionTimeout, cancellationToken);
            RaftGroupNode recoveredLeader = nodes
                .Where((node, index) => index == leaderIndex || index == returningIndex)
                .Single(node => Equals(node!.LocalEndpoint, recoveredLeaderEndpoint))!;
            await WaitUntilAsync(() => recoveredLeader.IsLeader, ElectionTimeout, cancellationToken);
            await recoveredLeader.ReplicateAsync(
                RaftCommandCodec.CatalogDelete(
                    recoveryCommandId,
                    "catalog",
                    Guid.NewGuid(),
                    "recovered"),
                cancellationToken);
            await WaitUntilAsync(
                () => new[] { leaderIndex, returningIndex }.All(index =>
                    appliers[index].Commands.Any(command => command.CommandId == recoveryCommandId)),
                ElectionTimeout,
                cancellationToken);
        }
        finally
        {
            foreach (RaftGroupNode? node in nodes)
            {
                if (node is not null)
                {
                    await node.DisposeAsync();
                }
            }
        }
    }

    private static RaftGroupNode CreateNode(
        string groupId,
        IPEndPoint endpoint,
        IReadOnlyList<IPEndPoint> members,
        string rootPath,
        int nodeIndex,
        RecordingCommandApplier applier) => new(
            Options(groupId, endpoint, members, rootPath, nodeIndex),
            applier);

    private static RaftGroupNodeOptions Options(
        string groupId,
        IPEndPoint endpoint,
        IReadOnlyList<IPEndPoint> members,
        string rootPath,
        int nodeIndex) => new()
        {
            GroupId = groupId,
            LocalEndpoint = endpoint,
            Members = members,
            StoragePath = Path.Combine(rootPath, $"node-{nodeIndex}"),
            LowerElectionTimeoutMilliseconds = 150,
            UpperElectionTimeoutMilliseconds = 350,
            RequestTimeout = TimeSpan.FromSeconds(2),
            SnapshotEveryEntries = 1_000,
            TransmissionBlockSize = 4 * 1024,
        };

    private static IPEndPoint[] AllocateLoopbackEndpoints(int count)
    {
        IPEndPoint[] result = new IPEndPoint[count];
        for (int index = 0; index < count; index++)
        {
            using TcpListener listener = new(IPAddress.Loopback, 0);
            listener.Start();
            result[index] = (IPEndPoint)listener.LocalEndpoint;
        }

        return result;
    }

    private static async Task WaitUntilAsync(
        Func<bool> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!predicate())
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"The Raft condition was not met within {timeout}.");
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task WaitUntilAsync(
        Func<Task<bool>> predicate,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Stopwatch stopwatch = Stopwatch.StartNew();
        while (!await predicate().ConfigureAwait(false))
        {
            if (stopwatch.Elapsed >= timeout)
            {
                throw new TimeoutException($"The Raft condition was not met within {timeout}.");
            }

            await Task.Delay(25, cancellationToken);
        }
    }

    private static async Task<bool> TryReadFinalDocumentAsync(
        FileSystemStorageEngine storage,
        Guid collectionId,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyDictionary<string, DocumentRecord> documents = await storage
                .LoadDocumentsAsync(collectionId, cancellationToken);
            return documents.TryGetValue("document", out DocumentRecord? document) && document.Version == 5;
        }
        catch (DomainException exception) when (exception.Code == ErrorCodes.CollectionNotFound)
        {
            return false;
        }
    }
}
