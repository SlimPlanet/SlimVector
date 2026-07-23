using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Placement;
using SlimVector.Domain;
using SlimVector.Raft;

namespace SlimVector.Application.Tests;

public sealed class ClusterTopologyBootstrapServiceTests
{
    [Fact]
    public async Task ClusterBootstrapRetriesUntilRaftCanCommitTopology()
    {
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        FlakyTopologyService topology = new(failuresBeforeSuccess: 2);
        ClusterTopologyBootstrapService service = CreateService(topology);

        await service.StartAsync(cancellationToken);

        Assert.Equal(3, topology.ReplaceAttempts);
        ClusterTopology installed = await topology.GetAsync(cancellationToken);
        Assert.Equal(3, installed.Nodes.Length);
        Assert.Equal(3, installed.CatalogNodeIds.Length);
        Assert.All(installed.DataGroups, group => Assert.Equal(3, group.Replicas.Length));
    }

    [Fact]
    public async Task ClusterBootstrapHonorsCancellationBeforeAccessingTopology()
    {
        FlakyTopologyService topology = new(failuresBeforeSuccess: 0);
        ClusterTopologyBootstrapService service = CreateService(topology);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.StartAsync(cancellation.Token));

        Assert.Equal(0, topology.ReplaceAttempts);
    }

    private static ClusterTopologyBootstrapService CreateService(IClusterTopologyService topology) => new(
        topology,
        Options.Create(new RaftOptions
        {
            Mode = ExecutionMode.Cluster,
            NodeId = "10.0.0.1",
            PublicEndpoint = "http://10.0.0.1:3262",
            PublicApiEndpoint = "http://127.0.0.1:8090",
            InternalEndpoint = "http://10.0.0.1:8080",
            Zone = "10.0.0.1",
            Members =
            [
                "http://10.0.0.1:3262",
                "http://10.0.0.2:3262",
                "http://10.0.0.3:3262",
            ],
            MemberApiEndpoints =
            [
                "http://127.0.0.1:8090",
                "http://127.0.0.1:8092",
                "http://127.0.0.1:8093",
            ],
            MemberNodeIds = ["10.0.0.1", "10.0.0.2", "10.0.0.3"],
            MemberInternalEndpoints =
            [
                "http://10.0.0.1:8080",
                "http://10.0.0.2:8080",
                "http://10.0.0.3:8080",
            ],
            MemberZones = ["10.0.0.1", "10.0.0.2", "10.0.0.3"],
            MemberCapacityBytes = [5_368_709_120, 5_368_709_120, 5_368_709_120],
            DataGroupCount = 2,
            DataPortRangeStart = 3_263,
            DataPortRangeCount = 16,
            CapacityBytes = 5_368_709_120,
            HeartbeatInterval = TimeSpan.FromMilliseconds(1),
        }),
        Options.Create(new ClusterMembershipOptions
        {
            OperationTimeout = TimeSpan.FromSeconds(1),
            CatchUpTimeout = TimeSpan.FromSeconds(2),
        }),
        Options.Create(new DataPlacementOptions { ReplicationFactor = 3 }),
        Options.Create(new StorageOptions { Path = "/tmp/slimvector-bootstrap-unused" }),
        TimeProvider.System);

    private sealed class FlakyTopologyService(int failuresBeforeSuccess) : IClusterTopologyService
    {
        private ClusterTopology _topology = new();

        public int ReplaceAttempts { get; private set; }

        public ValueTask<ClusterTopology> GetAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(_topology);

        public ValueTask<ClusterTopology> JoinAsync(
            ClusterNodeRegistration registration,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ClusterTopology> DrainAsync(
            string nodeId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ClusterTopology> RemoveAsync(
            string nodeId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ClusterTopology> MarkUnavailableAsync(
            string nodeId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ClusterTopology> UpdateCapacityAsync(
            string nodeId,
            long usedBytes,
            long assignedBytes,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ClusterTopology> ReportStorageUsageAsync(
            string nodeId,
            IReadOnlyDictionary<string, long> dataGroupBytes,
            IReadOnlyDictionary<string, ReplicaHealthReport> replicaHealth,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<ClusterTopology> ReplaceAsync(
            ClusterTopology topology,
            CancellationToken cancellationToken = default)
        {
            ReplaceAttempts++;
            if (ReplaceAttempts <= failuresBeforeSuccess)
            {
                throw new ConsensusUnavailableException(
                    MultiRaftNode.CatalogGroupId,
                    ConsensusFailureKind.QuorumUnavailable,
                    leaderEndpoint: null,
                    new InvalidOperationException("The test quorum is not ready."));
            }

            _topology = topology;
            return ValueTask.FromResult(topology);
        }
    }
}
