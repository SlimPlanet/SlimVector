namespace SlimVector.Domain;

public enum ClusterNodeState
{
    Joining,
    Active,
    Draining,
    Unavailable,
    Removed,
}

public enum DataGroupState
{
    Creating,
    Active,
    Relocating,
    Draining,
    Removed,
}

public enum ReplicaMoveState
{
    Planned,
    PreparingTarget,
    CatchingUp,
    Promoting,
    RemovingSource,
    Completed,
    Failed,
}

public sealed record ClusterNodeDescriptor
{
    public required string NodeId { get; init; }

    public required string ApiEndpoint { get; init; }

    public required string InternalEndpoint { get; init; }

    public required string RaftHost { get; init; }

    public required string Zone { get; init; }

    public long CapacityBytes { get; init; }

    public long UsedBytes { get; init; }

    public long AssignedBytes { get; init; }

    public int RaftPortStart { get; init; }

    public int RaftPortCount { get; init; }

    public ClusterNodeState State { get; init; } = ClusterNodeState.Joining;

    public DateTimeOffset LastSeenAt { get; init; }

    public string[] Roles { get; init; } = ["api", "data"];
}

public sealed record DataGroupReplica
{
    public required string NodeId { get; init; }

    public required string RaftEndpoint { get; init; }

    public long? ObservedReplicationLag { get; init; }

    public bool Healthy { get; init; } = true;
}

public sealed record DataGroupDescriptor
{
    public required string GroupId { get; init; }

    public long Generation { get; init; } = 1;

    public int ReplicationFactor { get; init; } = 3;

    public long EstimatedBytes { get; init; }

    public DataGroupState State { get; init; } = DataGroupState.Creating;

    public required DataGroupReplica[] Replicas { get; init; }
}

public sealed record ReplicaMoveDescriptor
{
    public required Guid OperationId { get; init; }

    public required Guid PlanId { get; init; }

    public required string GroupId { get; init; }

    public required string SourceNodeId { get; init; }

    public required string TargetNodeId { get; init; }

    public required string TargetRaftEndpoint { get; init; }

    public long EstimatedBytes { get; init; }

    public ReplicaMoveState State { get; init; } = ReplicaMoveState.Planned;

    public string? LastError { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }
}

public sealed record ClusterTopology
{
    public const int CurrentFormatVersion = 2;

    public int FormatVersion { get; init; } = CurrentFormatVersion;

    public long Epoch { get; init; } = 1;

    public ClusterNodeDescriptor[] Nodes { get; init; } = [];

    public string[] CatalogNodeIds { get; init; } = [];

    public DataGroupDescriptor[] DataGroups { get; init; } = [];

    public ReplicaMoveDescriptor[] ReplicaMoves { get; init; } = [];

    public void Validate()
    {
        if (FormatVersion != CurrentFormatVersion || Epoch < 1)
        {
            throw new DomainException(ErrorCodes.InvalidPlacement, "The cluster topology version or epoch is invalid.");
        }

        if (Nodes.Select(static node => node.NodeId).Distinct(StringComparer.Ordinal).Count() != Nodes.Length ||
            Nodes.Any(static node => string.IsNullOrWhiteSpace(node.NodeId) ||
                string.IsNullOrWhiteSpace(node.ApiEndpoint) ||
                string.IsNullOrWhiteSpace(node.InternalEndpoint) ||
                string.IsNullOrWhiteSpace(node.RaftHost) ||
                string.IsNullOrWhiteSpace(node.Zone) ||
                node.CapacityBytes < 0 || node.UsedBytes < 0 || node.AssignedBytes < 0 ||
                node.RaftPortStart is < 1 or > 65_535 || node.RaftPortCount < 1 ||
                node.RaftPortStart + node.RaftPortCount - 1 > 65_535))
        {
            throw new DomainException(ErrorCodes.InvalidPlacement, "The cluster topology contains an invalid node.");
        }

        HashSet<string> nodeIds = Nodes
            .Where(static node => node.State != ClusterNodeState.Removed)
            .Select(static node => node.NodeId)
            .ToHashSet(StringComparer.Ordinal);
        if (CatalogNodeIds.Length > 0 &&
            (CatalogNodeIds.Length != Math.Min(3, nodeIds.Count) ||
             CatalogNodeIds.Distinct(StringComparer.Ordinal).Count() != CatalogNodeIds.Length ||
             CatalogNodeIds.Any(nodeId => !nodeIds.Contains(nodeId))))
        {
            throw new DomainException(
                ErrorCodes.InvalidPlacement,
                "The catalog replica set must contain exactly three active nodes (or one in single-node mode).");
        }

        if (DataGroups.Select(static group => group.GroupId).Distinct(StringComparer.Ordinal).Count() != DataGroups.Length ||
            DataGroups.Any(group => string.IsNullOrWhiteSpace(group.GroupId) || group.Generation < 1 ||
                group.ReplicationFactor < 1 || group.EstimatedBytes < 0 ||
                group.Replicas.Length != group.ReplicationFactor ||
                group.Replicas.Select(static replica => replica.NodeId).Distinct(StringComparer.Ordinal).Count() != group.Replicas.Length ||
                group.Replicas.Any(replica => !nodeIds.Contains(replica.NodeId) ||
                    string.IsNullOrWhiteSpace(replica.RaftEndpoint) || replica.ObservedReplicationLag < 0)))
        {
            throw new DomainException(ErrorCodes.InvalidPlacement, "The cluster topology contains an invalid data group.");
        }


        HashSet<string> groupIds = DataGroups.Select(static group => group.GroupId).ToHashSet(StringComparer.Ordinal);
        if (ReplicaMoves.Select(static move => move.OperationId).Distinct().Count() != ReplicaMoves.Length ||
            ReplicaMoves.Any(move => move.OperationId == Guid.Empty || move.PlanId == Guid.Empty ||
                !groupIds.Contains(move.GroupId) ||
                move.State != ReplicaMoveState.Completed &&
                (!nodeIds.Contains(move.SourceNodeId) || !nodeIds.Contains(move.TargetNodeId)) ||
                string.Equals(move.SourceNodeId, move.TargetNodeId, StringComparison.Ordinal) ||
                string.IsNullOrWhiteSpace(move.TargetRaftEndpoint) || move.EstimatedBytes < 0))
        {
            throw new DomainException(ErrorCodes.InvalidPlacement, "The cluster topology contains an invalid replica move.");
        }
    }
}
