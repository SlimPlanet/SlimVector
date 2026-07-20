using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Application.Placement;
using SlimVector.Domain;

namespace SlimVector.Application.Tests;

public sealed class SharedNothingPlacementPlannerTests
{
    [Fact]
    public void UnavailableReplicaIsReplacedOnAnActiveNode()
    {
        DataPlacementOptions options = new()
        {
            ReplicationFactor = 3,
            MinimumGroupReplicasPerNode = 1,
            TargetDataGroupBytes = 1L * 1_024 * 1_024 * 1_024,
        };
        SharedNothingPlacementPlanner planner = new(Options.Create(options), TimeProvider.System);
        ClusterNodeDescriptor[] nodes = Enumerable.Range(0, 6).Select(Node).ToArray();
        nodes[0] = nodes[0] with { State = ClusterNodeState.Unavailable };
        ClusterTopology topology = new()
        {
            Epoch = 4,
            Nodes = nodes,
            CatalogNodeIds = ["node-0", "node-1", "node-2"],
            DataGroups =
            [
                new DataGroupDescriptor
                {
                    GroupId = "data-0",
                    ReplicationFactor = 3,
                    EstimatedBytes = 256 * 1_024 * 1_024,
                    State = DataGroupState.Active,
                    Replicas = Enumerable.Range(0, 3).Select(index => new DataGroupReplica
                    {
                        NodeId = $"node-{index}",
                        RaftEndpoint = $"http://127.0.0.1:{10_000 + (index * 100)}",
                    }).ToArray(),
                },
            ],
        };

        SharedNothingRebalancePlan plan = planner.Plan(topology, "node-0");

        ReplicaRelocationPlan move = Assert.Single(plan.ReplicaMoves);
        Assert.Equal("node-0", move.SourceNodeId);
        Assert.NotEqual("node-0", move.TargetNodeId);
        Assert.Equal(ClusterNodeState.Active, nodes.Single(node => node.NodeId == move.TargetNodeId).State);
    }

    [Fact]
    public void NewNodesReceiveRf3GroupsWithZoneAntiAffinityAndBoundedCapacity()
    {
        DataPlacementOptions options = new()
        {
            ReplicationFactor = 3,
            MinimumGroupReplicasPerNode = 4,
            TargetDataGroupBytes = 32L * 1_024 * 1_024 * 1_024,
            HighWatermarkRatio = 0.80,
            ReserveRatio = 0.15,
        };
        SharedNothingPlacementPlanner planner = new(Options.Create(options), TimeProvider.System);
        ClusterTopology topology = new()
        {
            Epoch = 7,
            Nodes = Enumerable.Range(0, 6).Select(index => Node(index)).ToArray(),
        };

        SharedNothingRebalancePlan plan = planner.Plan(topology);

        Assert.Equal(8, plan.GroupsToCreate.Count);
        Assert.All(plan.GroupsToCreate, group =>
        {
            Assert.Equal(3, group.Replicas.Length);
            Assert.Equal(3, group.Replicas.Select(replica => topology.Nodes.Single(node =>
                node.NodeId == replica.NodeId).Zone).Distinct(StringComparer.Ordinal).Count());
        });
        Assert.All(plan.AssignedBytesAfter, pair =>
            Assert.True(pair.Value <= topology.Nodes.Single(node => node.NodeId == pair.Key).CapacityBytes * 0.80));

        long usableThreeNodes = UsableCapacity(topology.Nodes.Take(3), options);
        long usableSixNodes = UsableCapacity(topology.Nodes, options);
        long usableNineNodes = UsableCapacity(Enumerable.Range(0, 9).Select(Node), options);
        Assert.InRange(Math.Abs(usableSixNodes - (usableThreeNodes * 2)), 0, 1);
        Assert.InRange(Math.Abs(usableNineNodes - (usableThreeNodes * 3)), 0, 1);
        long minimumAssigned = plan.AssignedBytesAfter.Values.Min();
        long maximumAssigned = plan.AssignedBytesAfter.Values.Max();
        Assert.True((maximumAssigned - minimumAssigned) / (double)Math.Max(1, maximumAssigned) < 0.15);
    }

    private static long UsableCapacity(IEnumerable<ClusterNodeDescriptor> nodes, DataPlacementOptions options) =>
        (long)(nodes.Sum(static node => node.CapacityBytes) * (1 - options.ReserveRatio)) / options.ReplicationFactor;

    private static ClusterNodeDescriptor Node(int index) => new()
    {
        NodeId = $"node-{index}",
        ApiEndpoint = $"http://127.0.0.1:{8_000 + index}",
        InternalEndpoint = $"http://127.0.0.1:{8_000 + index}",
        RaftHost = "127.0.0.1",
        Zone = $"zone-{index % 3}",
        CapacityBytes = 1L * 1_024 * 1_024 * 1_024 * 1_024,
        RaftPortStart = 10_000 + (index * 100),
        RaftPortCount = 64,
        State = ClusterNodeState.Active,
        LastSeenAt = DateTimeOffset.UtcNow,
    };
}
