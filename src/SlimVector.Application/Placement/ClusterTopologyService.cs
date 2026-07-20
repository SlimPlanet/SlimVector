using System.Net;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Placement;

public sealed record ClusterNodeRegistration(
    string NodeId,
    string ApiEndpoint,
    string InternalEndpoint,
    string RaftHost,
    string Zone,
    long CapacityBytes,
    int RaftPortStart,
    int RaftPortCount,
    string[]? Roles = null);

public sealed record ReplicaHealthReport(long? ReplicationLag, bool Healthy);

public interface IClusterTopologyService
{
    ValueTask<ClusterTopology> GetAsync(CancellationToken cancellationToken = default);

    ValueTask<ClusterTopology> JoinAsync(
        ClusterNodeRegistration registration,
        CancellationToken cancellationToken = default);

    ValueTask<ClusterTopology> DrainAsync(string nodeId, CancellationToken cancellationToken = default);

    ValueTask<ClusterTopology> RemoveAsync(string nodeId, CancellationToken cancellationToken = default);

    ValueTask<ClusterTopology> MarkUnavailableAsync(string nodeId, CancellationToken cancellationToken = default);

    ValueTask<ClusterTopology> UpdateCapacityAsync(
        string nodeId,
        long usedBytes,
        long assignedBytes,
        CancellationToken cancellationToken = default);

    ValueTask<ClusterTopology> ReportStorageUsageAsync(
        string nodeId,
        IReadOnlyDictionary<string, long> dataGroupBytes,
        IReadOnlyDictionary<string, ReplicaHealthReport> replicaHealth,
        CancellationToken cancellationToken = default);

    ValueTask<ClusterTopology> ReplaceAsync(
        ClusterTopology topology,
        CancellationToken cancellationToken = default);
}

public sealed class ClusterTopologyService : IClusterTopologyService, IDisposable
{
    private readonly IClusterTopologyStore _store;
    private readonly IConsensusCoordinator _consensus;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ClusterTopologyService(
        IClusterTopologyStore store,
        IConsensusCoordinator consensus,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(consensus);
        ArgumentNullException.ThrowIfNull(timeProvider);
        _store = store;
        _consensus = consensus;
        _timeProvider = timeProvider;
    }

    public ValueTask<ClusterTopology> GetAsync(CancellationToken cancellationToken = default) =>
        _store.GetAsync(cancellationToken);

    public async ValueTask<ClusterTopology> JoinAsync(
        ClusterNodeRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ValidateRegistration(registration);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClusterTopology current = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
            ClusterNodeDescriptor incoming = new()
            {
                NodeId = registration.NodeId,
                ApiEndpoint = NormalizeEndpoint(registration.ApiEndpoint),
                InternalEndpoint = NormalizeEndpoint(registration.InternalEndpoint),
                RaftHost = registration.RaftHost,
                Zone = registration.Zone,
                CapacityBytes = registration.CapacityBytes,
                RaftPortStart = registration.RaftPortStart,
                RaftPortCount = registration.RaftPortCount,
                State = ClusterNodeState.Active,
                LastSeenAt = _timeProvider.GetUtcNow(),
                Roles = registration.Roles is { Length: > 0 }
                    ? registration.Roles.Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
                    : ["api", "data"],
            };
            ClusterNodeDescriptor? existing = current.Nodes.FirstOrDefault(node =>
                string.Equals(node.NodeId, incoming.NodeId, StringComparison.Ordinal));
            if (existing is not null && existing.State != ClusterNodeState.Removed &&
                (!string.Equals(existing.ApiEndpoint, incoming.ApiEndpoint, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(existing.InternalEndpoint, incoming.InternalEndpoint, StringComparison.OrdinalIgnoreCase) ||
                 !string.Equals(existing.RaftHost, incoming.RaftHost, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DomainException(
                    ErrorCodes.MembershipConflict,
                    $"Node '{incoming.NodeId}' is already registered with different endpoints.");
            }

            incoming = incoming with
            {
                UsedBytes = existing?.UsedBytes ?? 0,
                AssignedBytes = existing?.AssignedBytes ?? 0,
            };
            ClusterTopology next = current with
            {
                Epoch = checked(current.Epoch + 1),
                Nodes = [
                    .. current.Nodes.Where(node => !string.Equals(node.NodeId, incoming.NodeId, StringComparison.Ordinal)),
                    incoming,
                ],
            };
            return await ReplaceCoreAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public ValueTask<ClusterTopology> DrainAsync(string nodeId, CancellationToken cancellationToken = default) =>
        ChangeNodeStateAsync(nodeId, ClusterNodeState.Draining, requireNoReplicas: false, cancellationToken);

    public ValueTask<ClusterTopology> RemoveAsync(string nodeId, CancellationToken cancellationToken = default) =>
        ChangeNodeStateAsync(nodeId, ClusterNodeState.Removed, requireNoReplicas: true, cancellationToken);

    public ValueTask<ClusterTopology> MarkUnavailableAsync(
        string nodeId,
        CancellationToken cancellationToken = default) =>
        ChangeNodeStateAsync(nodeId, ClusterNodeState.Unavailable, requireNoReplicas: false, cancellationToken);

    public async ValueTask<ClusterTopology> UpdateCapacityAsync(
        string nodeId,
        long usedBytes,
        long assignedBytes,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentOutOfRangeException.ThrowIfNegative(usedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(assignedBytes);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClusterTopology current = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
            ClusterNodeDescriptor existing = GetNode(current, nodeId);
            ClusterTopology next = current with
            {
                Epoch = checked(current.Epoch + 1),
                Nodes = current.Nodes.Select(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal)
                    ? existing with
                    {
                        UsedBytes = usedBytes,
                        AssignedBytes = assignedBytes,
                        LastSeenAt = _timeProvider.GetUtcNow(),
                    }
                    : node).ToArray(),
            };
            return await ReplaceCoreAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ClusterTopology> ReportStorageUsageAsync(
        string nodeId,
        IReadOnlyDictionary<string, long> dataGroupBytes,
        IReadOnlyDictionary<string, ReplicaHealthReport> replicaHealth,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        ArgumentNullException.ThrowIfNull(dataGroupBytes);
        ArgumentNullException.ThrowIfNull(replicaHealth);
        if (dataGroupBytes.Values.Any(static bytes => bytes < 0))
        {
            throw new ArgumentOutOfRangeException(nameof(dataGroupBytes));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClusterTopology current = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
            ClusterNodeDescriptor existing = GetNode(current, nodeId);
            DataGroupDescriptor[] groups = current.DataGroups.Select(group =>
            {
                DataGroupDescriptor updated = dataGroupBytes.TryGetValue(group.GroupId, out long bytes)
                    ? group with { EstimatedBytes = bytes }
                    : group;
                if (replicaHealth.TryGetValue(group.GroupId, out ReplicaHealthReport? health))
                {
                    updated = updated with
                    {
                        Replicas = updated.Replicas.Select(replica => string.Equals(
                                replica.NodeId,
                                nodeId,
                                StringComparison.Ordinal)
                            ? replica with
                            {
                                ObservedReplicationLag = health.ReplicationLag,
                                Healthy = health.Healthy,
                            }
                            : replica).ToArray(),
                    };
                }

                return updated;
            }).ToArray();
            long usedBytes = dataGroupBytes.Values.Sum();
            long assignedBytes = groups.Where(group => group.Replicas.Any(replica =>
                    string.Equals(replica.NodeId, nodeId, StringComparison.Ordinal)))
                .Sum(static group => group.EstimatedBytes);
            if (existing.UsedBytes == usedBytes && existing.AssignedBytes == assignedBytes &&
                groups.SequenceEqual(current.DataGroups))
            {
                return current;
            }

            ClusterTopology next = current with
            {
                Epoch = checked(current.Epoch + 1),
                Nodes = current.Nodes.Select(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal)
                    ? existing with
                    {
                        UsedBytes = usedBytes,
                        AssignedBytes = assignedBytes,
                        LastSeenAt = _timeProvider.GetUtcNow(),
                    }
                    : node).ToArray(),
                DataGroups = groups,
            };
            return await ReplaceCoreAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ClusterTopology> ReplaceAsync(
        ClusterTopology topology,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReplaceCoreAsync(topology, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ClusterTopology> ChangeNodeStateAsync(
        string nodeId,
        ClusterNodeState state,
        bool requireNoReplicas,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nodeId);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClusterTopology current = await _store.GetAsync(cancellationToken).ConfigureAwait(false);
            ClusterNodeDescriptor existing = GetNode(current, nodeId);
            if (requireNoReplicas && current.DataGroups.Any(group => group.State != DataGroupState.Removed &&
                    group.Replicas.Any(replica => string.Equals(replica.NodeId, nodeId, StringComparison.Ordinal))))
            {
                throw new DomainException(
                    ErrorCodes.MembershipConflict,
                    $"Node '{nodeId}' still hosts data-group replicas and must be drained first.");
            }

            ClusterTopology next = current with
            {
                Epoch = checked(current.Epoch + 1),
                Nodes = current.Nodes.Select(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal)
                    ? existing with { State = state, LastSeenAt = _timeProvider.GetUtcNow() }
                    : node).ToArray(),
            };
            return await ReplaceCoreAsync(next, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async ValueTask<ClusterTopology> ReplaceCoreAsync(
        ClusterTopology topology,
        CancellationToken cancellationToken)
    {
        topology.Validate();
        try
        {
            await _consensus.ReplaceTopologyAsync(topology, cancellationToken).ConfigureAwait(false);
        }
        catch (NotSupportedException)
        {
            await _store.ReplaceAsync(topology, cancellationToken).ConfigureAwait(false);
        }

        return topology;
    }

    private static ClusterNodeDescriptor GetNode(ClusterTopology topology, string nodeId) =>
        topology.Nodes.FirstOrDefault(node => string.Equals(node.NodeId, nodeId, StringComparison.Ordinal)) ??
        throw new DomainException(ErrorCodes.MembershipMemberNotFound, $"Node '{nodeId}' is not registered.");

    private static void ValidateRegistration(ClusterNodeRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.NodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(registration.Zone);
        if (!Uri.TryCreate(registration.ApiEndpoint, UriKind.Absolute, out Uri? api) ||
            api.Scheme is not "http" and not "https" ||
            !Uri.TryCreate(registration.InternalEndpoint, UriKind.Absolute, out Uri? internalEndpoint) ||
            internalEndpoint.Scheme is not "http" and not "https" ||
            !IPAddress.TryParse(registration.RaftHost, out _) || registration.CapacityBytes < 1 ||
            registration.RaftPortStart is < 1 or > 65_535 || registration.RaftPortCount < 1 ||
            registration.RaftPortStart + registration.RaftPortCount - 1 > 65_535)
        {
            throw new DomainException(ErrorCodes.InvalidPlacement, "The cluster node registration is invalid.");
        }
    }

    private static string NormalizeEndpoint(string endpoint) =>
        new Uri(endpoint, UriKind.Absolute).AbsoluteUri.TrimEnd('/');

    public void Dispose() => _gate.Dispose();
}

public sealed class ClusterTopologyBootstrapService(
    IClusterTopologyService topology,
    IOptions<RaftOptions> raftOptions,
    IOptions<DataPlacementOptions> placementOptions,
    IOptions<StorageOptions> storageOptions) : Microsoft.Extensions.Hosting.IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        RaftOptions raft = raftOptions.Value;
        ClusterTopology current = await topology.GetAsync(cancellationToken).ConfigureAwait(false);
        if (current.Nodes.Length > 0)
        {
            return;
        }

        long localCapacity = raft.CapacityBytes > 0 ? raft.CapacityBytes : GetDiskCapacity(storageOptions.Value.Path);
        if (raft.Mode == ExecutionMode.SingleNode)
        {
            ClusterNodeRegistration local = Registration(
                raft.NodeId,
                raft.PublicApiEndpoint,
                raft.InternalEndpoint,
                RaftOptionsValidator.TryParseEndpoint(raft.PublicEndpoint, out IPEndPoint? endpoint)
                    ? endpoint!.Address.ToString()
                    : "127.0.0.1",
                raft.Zone,
                localCapacity,
                raft.DataPortRangeStart,
                raft.DataPortRangeCount);
            current = await topology.JoinAsync(local, cancellationToken).ConfigureAwait(false);
            ClusterTopology single = current with
            {
                Epoch = checked(current.Epoch + 1),
                CatalogNodeIds = [raft.NodeId],
                DataGroups =
                [
                    new DataGroupDescriptor
                    {
                        GroupId = "data-0",
                        ReplicationFactor = 1,
                        State = DataGroupState.Active,
                        Replicas =
                        [
                            new DataGroupReplica
                            {
                                NodeId = raft.NodeId,
                                RaftEndpoint = raft.PublicEndpoint,
                            },
                        ],
                    },
                ],
            };
            _ = await topology.ReplaceAsync(single, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (raft.JoinExistingCluster)
        {
            return;
        }

        List<ClusterNodeDescriptor> nodes = [];
        for (int index = 0; index < raft.Members.Length; index++)
        {
            _ = RaftOptionsValidator.TryParseEndpoint(raft.Members[index], out IPEndPoint? endpoint);
            nodes.Add(new ClusterNodeDescriptor
            {
                NodeId = raft.MemberNodeIds.Length == raft.Members.Length
                    ? raft.MemberNodeIds[index]
                    : $"node-{index + 1}",
                ApiEndpoint = raft.MemberApiEndpoints[index].TrimEnd('/'),
                InternalEndpoint = raft.MemberInternalEndpoints.Length == raft.Members.Length
                    ? raft.MemberInternalEndpoints[index].TrimEnd('/')
                    : raft.MemberApiEndpoints[index].TrimEnd('/'),
                RaftHost = endpoint!.Address.ToString(),
                Zone = raft.MemberZones.Length == raft.Members.Length ? raft.MemberZones[index] : "default",
                CapacityBytes = raft.MemberCapacityBytes.Length == raft.Members.Length
                    ? raft.MemberCapacityBytes[index]
                    : localCapacity,
                RaftPortStart = endpoint.Port + 1,
                RaftPortCount = raft.DataPortRangeCount,
                State = ClusterNodeState.Active,
                LastSeenAt = DateTimeOffset.UtcNow,
            });
        }

        int replicationFactor = Math.Min(placementOptions.Value.ReplicationFactor, nodes.Count);
        DataGroupDescriptor[] groups = Enumerable.Range(0, raft.DataGroupCount).Select(groupIndex =>
            new DataGroupDescriptor
            {
                GroupId = $"data-{groupIndex}",
                ReplicationFactor = replicationFactor,
                State = DataGroupState.Active,
                Replicas = Enumerable.Range(0, replicationFactor).Select(offset =>
                {
                    int nodeIndex = ((groupIndex * replicationFactor) + offset) % nodes.Count;
                    return new DataGroupReplica
                    {
                        NodeId = nodes[nodeIndex].NodeId,
                        RaftEndpoint = OffsetPort(raft.Members[nodeIndex], groupIndex + 1),
                    };
                }).ToArray(),
            }).ToArray();
        ClusterTopology bootstrap = new()
        {
            Epoch = 2,
            Nodes = nodes.ToArray(),
            CatalogNodeIds = nodes.Take(3).Select(static node => node.NodeId).ToArray(),
            DataGroups = groups,
        };
        _ = await topology.ReplaceAsync(bootstrap, cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static ClusterNodeRegistration Registration(
        string nodeId,
        string apiEndpoint,
        string internalEndpoint,
        string raftHost,
        string zone,
        long capacity,
        int portStart,
        int portCount) =>
        new(nodeId, apiEndpoint, internalEndpoint, raftHost, zone, capacity, portStart, portCount);

    private static long GetDiskCapacity(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? fullPath;
        return new DriveInfo(root).TotalSize;
    }

    private static string OffsetPort(string endpoint, int offset)
    {
        _ = RaftOptionsValidator.TryParseEndpoint(endpoint, out IPEndPoint? parsed);
        return new UriBuilder("http", parsed!.Address.ToString(), checked(parsed.Port + offset)).Uri.AbsoluteUri.TrimEnd('/');
    }
}
