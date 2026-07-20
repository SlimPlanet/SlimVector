using System.Net;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Domain;

namespace SlimVector.Application.Placement;

public sealed record ReplicaRelocationPlan(
    string GroupId,
    string SourceNodeId,
    string TargetNodeId,
    string TargetRaftEndpoint,
    long EstimatedBytes,
    string Reason);

public sealed record SharedNothingRebalancePlan(
    Guid PlanId,
    long TopologyEpoch,
    DateTimeOffset CreatedAt,
    string? DrainNodeId,
    IReadOnlyList<DataGroupDescriptor> GroupsToCreate,
    IReadOnlyList<ReplicaRelocationPlan> ReplicaMoves,
    IReadOnlyDictionary<string, long> AssignedBytesBefore,
    IReadOnlyDictionary<string, long> AssignedBytesAfter);

public interface ISharedNothingPlacementPlanner
{
    SharedNothingRebalancePlan Plan(ClusterTopology topology, string? drainNodeId = null);

    ClusterTopology Apply(ClusterTopology topology, SharedNothingRebalancePlan plan);
}

public sealed class SharedNothingPlacementPlanner : ISharedNothingPlacementPlanner
{
    private readonly DataPlacementOptions _options;
    private readonly TimeProvider _timeProvider;

    public SharedNothingPlacementPlanner(IOptions<DataPlacementOptions> options, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _options = options.Value;
        _timeProvider = timeProvider;
    }

    public SharedNothingRebalancePlan Plan(ClusterTopology topology, string? drainNodeId = null)
    {
        ArgumentNullException.ThrowIfNull(topology);
        topology.Validate();
        ClusterNodeDescriptor[] dataNodes = topology.Nodes
            .Where(static node => node.State == ClusterNodeState.Active &&
                node.Roles.Contains("data", StringComparer.OrdinalIgnoreCase))
            .OrderBy(static node => node.NodeId, StringComparer.Ordinal)
            .ToArray();
        if (dataNodes.Length < _options.ReplicationFactor)
        {
            throw new DomainException(
                ErrorCodes.InvalidPlacement,
                $"At least {_options.ReplicationFactor} active data nodes are required for RF{_options.ReplicationFactor}.");
        }

        if (drainNodeId is not null && !topology.Nodes.Any(node =>
                string.Equals(node.NodeId, drainNodeId, StringComparison.Ordinal) &&
                node.State is ClusterNodeState.Active or ClusterNodeState.Draining or ClusterNodeState.Unavailable))
        {
            throw new DomainException(ErrorCodes.MembershipMemberNotFound, $"Node '{drainNodeId}' cannot be drained.");
        }

        Dictionary<string, long> assigned = dataNodes.ToDictionary(
            static node => node.NodeId,
            static _ => 0L,
            StringComparer.Ordinal);
        foreach (DataGroupDescriptor group in topology.DataGroups.Where(static group => group.State != DataGroupState.Removed))
        {
            long reservation = ReservedBytes(group);
            foreach (DataGroupReplica replica in group.Replicas)
            {
                if (assigned.TryGetValue(replica.NodeId, out long currentAssigned))
                {
                    assigned[replica.NodeId] = checked(currentAssigned + reservation);
                }
            }
        }

        Dictionary<string, long> before = new(assigned, StringComparer.Ordinal);
        Dictionary<string, HashSet<int>> usedPorts = BuildUsedPorts(topology);
        List<DataGroupDescriptor> created = [];
        List<ReplicaRelocationPlan> moves = [];
        int desiredGroupCount = Math.Max(
            topology.DataGroups.Count(static group => group.State != DataGroupState.Removed),
            (int)Math.Ceiling(dataNodes.Length * _options.MinimumGroupReplicasPerNode /
                (double)_options.ReplicationFactor));
        desiredGroupCount = Math.Max(
            desiredGroupCount,
            (int)Math.Ceiling(topology.DataGroups.Sum(static group => group.EstimatedBytes) /
                (double)_options.TargetDataGroupBytes));
        int nextGroupNumber = NextGroupNumber(topology.DataGroups);
        while (topology.DataGroups.Count(static group => group.State != DataGroupState.Removed) + created.Count < desiredGroupCount)
        {
            ClusterNodeDescriptor[] replicas = SelectReplicaNodes(
                dataNodes,
                assigned,
                new HashSet<string>(StringComparer.Ordinal),
                _options.ReplicationFactor,
                _options.TargetDataGroupBytes);
            string groupId = $"data-{nextGroupNumber++}";
            DataGroupDescriptor group = new()
            {
                GroupId = groupId,
                ReplicationFactor = _options.ReplicationFactor,
                State = DataGroupState.Creating,
                Replicas = replicas.Select(node => new DataGroupReplica
                {
                    NodeId = node.NodeId,
                    RaftEndpoint = AllocateEndpoint(node, usedPorts),
                }).ToArray(),
            };
            created.Add(group);
            foreach (ClusterNodeDescriptor node in replicas)
            {
                assigned[node.NodeId] = checked(assigned[node.NodeId] + _options.TargetDataGroupBytes);
            }
        }

        IEnumerable<DataGroupDescriptor> groupsToInspect = topology.DataGroups
            .Where(static group => group.State != DataGroupState.Removed)
            .OrderByDescending(ReservedBytes)
            .ThenBy(static group => group.GroupId, StringComparer.Ordinal);
        foreach (DataGroupDescriptor group in groupsToInspect)
        {
            DataGroupReplica? source = drainNodeId is null
                ? SelectOverloadedReplica(group, dataNodes, assigned)
                : group.Replicas.FirstOrDefault(replica =>
                    string.Equals(replica.NodeId, drainNodeId, StringComparison.Ordinal));
            if (source is null)
            {
                continue;
            }

            HashSet<string> excluded = group.Replicas.Select(static replica => replica.NodeId)
                .ToHashSet(StringComparer.Ordinal);
            long bytes = ReservedBytes(group);
            ClusterNodeDescriptor? target = SelectReplicaNodes(dataNodes, assigned, excluded, 1, bytes).FirstOrDefault();
            if (target is null)
            {
                continue;
            }

            if (drainNodeId is null && !ImprovesBalance(source.NodeId, target.NodeId, bytes, dataNodes, assigned))
            {
                continue;
            }

            moves.Add(new ReplicaRelocationPlan(
                group.GroupId,
                source.NodeId,
                target.NodeId,
                AllocateEndpoint(target, usedPorts),
                group.EstimatedBytes,
                drainNodeId is null ? "balance assigned bytes" : $"drain {drainNodeId}"));
            if (assigned.TryGetValue(source.NodeId, out long sourceAssigned))
            {
                assigned[source.NodeId] = Math.Max(0, sourceAssigned - bytes);
            }

            assigned[target.NodeId] = checked(assigned[target.NodeId] + bytes);
        }

        return new SharedNothingRebalancePlan(
            Guid.NewGuid(),
            topology.Epoch,
            _timeProvider.GetUtcNow(),
            drainNodeId,
            created,
            moves,
            before,
            new Dictionary<string, long>(assigned, StringComparer.Ordinal));
    }

    public ClusterTopology Apply(ClusterTopology topology, SharedNothingRebalancePlan plan)
    {
        ArgumentNullException.ThrowIfNull(topology);
        ArgumentNullException.ThrowIfNull(plan);
        if (topology.Epoch != plan.TopologyEpoch)
        {
            throw new DomainException(
                ErrorCodes.RoutingEpochMismatch,
                $"Rebalance plan epoch {plan.TopologyEpoch} does not match topology epoch {topology.Epoch}.");
        }

        Dictionary<string, DataGroupDescriptor> groups = topology.DataGroups.ToDictionary(
            static group => group.GroupId,
            StringComparer.Ordinal);
        foreach (DataGroupDescriptor created in plan.GroupsToCreate)
        {
            groups.Add(created.GroupId, created with { State = DataGroupState.Active });
        }

        foreach (ReplicaRelocationPlan move in plan.ReplicaMoves)
        {
            DataGroupDescriptor group = groups[move.GroupId];
            DataGroupReplica[] replicas = [
                .. group.Replicas.Where(replica => !string.Equals(replica.NodeId, move.SourceNodeId, StringComparison.Ordinal)),
                new DataGroupReplica { NodeId = move.TargetNodeId, RaftEndpoint = move.TargetRaftEndpoint },
            ];
            groups[move.GroupId] = group with
            {
                Generation = checked(group.Generation + 1),
                State = DataGroupState.Active,
                Replicas = replicas,
            };
        }

        ClusterTopology next = topology with
        {
            Epoch = checked(topology.Epoch + 1),
            Nodes = topology.Nodes.Select(node => plan.AssignedBytesAfter.TryGetValue(node.NodeId, out long bytes)
                ? node with { AssignedBytes = bytes }
                : node).ToArray(),
            DataGroups = groups.Values.OrderBy(static group => group.GroupId, StringComparer.Ordinal).ToArray(),
        };
        next.Validate();
        return next;
    }

    private long ReservedBytes(DataGroupDescriptor group) =>
        Math.Max(group.EstimatedBytes, _options.TargetDataGroupBytes);

    private static Dictionary<string, HashSet<int>> BuildUsedPorts(ClusterTopology topology)
    {
        Dictionary<string, HashSet<int>> result = topology.Nodes.ToDictionary(
            static node => node.NodeId,
            static _ => new HashSet<int>(),
            StringComparer.Ordinal);
        foreach (DataGroupReplica replica in topology.DataGroups.SelectMany(static group => group.Replicas))
        {
            if (result.TryGetValue(replica.NodeId, out HashSet<int>? ports) &&
                Uri.TryCreate(replica.RaftEndpoint, UriKind.Absolute, out Uri? endpoint))
            {
                ports.Add(endpoint.Port);
            }
        }

        return result;
    }

    private static int NextGroupNumber(IEnumerable<DataGroupDescriptor> groups) => groups
        .Select(static group => group.GroupId.StartsWith("data-", StringComparison.Ordinal) &&
            int.TryParse(group.GroupId.AsSpan(5), out int number) ? number : -1)
        .DefaultIfEmpty(-1)
        .Max() + 1;

    private ClusterNodeDescriptor[] SelectReplicaNodes(
        IReadOnlyList<ClusterNodeDescriptor> nodes,
        IReadOnlyDictionary<string, long> assigned,
        HashSet<string> excluded,
        int count,
        long requiredBytes)
    {
        List<ClusterNodeDescriptor> selected = [];
        HashSet<string> selectedZones = new(StringComparer.Ordinal);
        while (selected.Count < count)
        {
            ClusterNodeDescriptor? next = nodes
                .Where(node => !excluded.Contains(node.NodeId) &&
                    selected.All(candidate => !string.Equals(candidate.NodeId, node.NodeId, StringComparison.Ordinal)) &&
                    assigned.GetValueOrDefault(node.NodeId) + requiredBytes <=
                    node.CapacityBytes * _options.HighWatermarkRatio &&
                    node.CapacityBytes - assigned.GetValueOrDefault(node.NodeId) - requiredBytes >=
                    node.CapacityBytes * _options.ReserveRatio)
                .OrderBy(node => selectedZones.Contains(node.Zone))
                .ThenBy(node => assigned.GetValueOrDefault(node.NodeId) /
                    (double)Math.Max(1, node.CapacityBytes))
                .ThenBy(static node => node.NodeId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (next is null)
            {
                break;
            }

            selected.Add(next);
            selectedZones.Add(next.Zone);
        }

        return selected.ToArray();
    }

    private static DataGroupReplica? SelectOverloadedReplica(
        DataGroupDescriptor group,
        IReadOnlyList<ClusterNodeDescriptor> nodes,
        IReadOnlyDictionary<string, long> assigned)
    {
        Dictionary<string, ClusterNodeDescriptor> byId = nodes.ToDictionary(static node => node.NodeId, StringComparer.Ordinal);
        return group.Replicas
            .Where(replica => byId.ContainsKey(replica.NodeId))
            .OrderByDescending(replica => assigned.GetValueOrDefault(replica.NodeId) /
                (double)Math.Max(1, byId[replica.NodeId].CapacityBytes))
            .FirstOrDefault();
    }

    private static bool ImprovesBalance(
        string sourceNodeId,
        string targetNodeId,
        long bytes,
        IReadOnlyList<ClusterNodeDescriptor> nodes,
        IReadOnlyDictionary<string, long> assigned)
    {
        ClusterNodeDescriptor source = nodes.Single(node => string.Equals(node.NodeId, sourceNodeId, StringComparison.Ordinal));
        ClusterNodeDescriptor target = nodes.Single(node => string.Equals(node.NodeId, targetNodeId, StringComparison.Ordinal));
        double before = Math.Abs(
            assigned.GetValueOrDefault(sourceNodeId) / (double)Math.Max(1, source.CapacityBytes) -
            assigned.GetValueOrDefault(targetNodeId) / (double)Math.Max(1, target.CapacityBytes));
        double after = Math.Abs(
            Math.Max(0, assigned.GetValueOrDefault(sourceNodeId) - bytes) / (double)Math.Max(1, source.CapacityBytes) -
            (assigned.GetValueOrDefault(targetNodeId) + bytes) / (double)Math.Max(1, target.CapacityBytes));
        return after < before;
    }

    private static string AllocateEndpoint(
        ClusterNodeDescriptor node,
        Dictionary<string, HashSet<int>> usedPorts)
    {
        HashSet<int> used = usedPorts[node.NodeId];
        int port = Enumerable.Range(node.RaftPortStart, node.RaftPortCount).FirstOrDefault(candidate => !used.Contains(candidate));
        if (port == 0)
        {
            throw new DomainException(
                ErrorCodes.InvalidPlacement,
                $"Node '{node.NodeId}' has exhausted its Raft port range.");
        }

        used.Add(port);
        IPAddress address = IPAddress.Parse(node.RaftHost);
        return new UriBuilder("http", address.ToString(), port).Uri.AbsoluteUri.TrimEnd('/');
    }
}
