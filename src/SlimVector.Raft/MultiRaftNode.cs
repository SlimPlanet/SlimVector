using System.Collections.Frozen;
using SlimVector.Domain;
using SlimVector.Raft.Commands;

namespace SlimVector.Raft;

public sealed class MultiRaftNode : IAsyncDisposable
{
    public const string CatalogGroupId = "catalog";
    private readonly FrozenDictionary<string, RaftGroupNode> _groups;
    private readonly string[] _dataGroupIds;

    public MultiRaftNode(
        IEnumerable<RaftGroupNodeOptions> groupOptions,
        Func<string, IRaftCommandApplier> applierFactory)
    {
        ArgumentNullException.ThrowIfNull(groupOptions);
        ArgumentNullException.ThrowIfNull(applierFactory);
        RaftGroupNodeOptions[] options = groupOptions.ToArray();
        if (options.Length == 0 || !options.Any(static option => option.GroupId == CatalogGroupId))
        {
            throw new ArgumentException("A multi-Raft node requires a catalog group.", nameof(groupOptions));
        }

        if (options.Select(static option => option.GroupId).Distinct(StringComparer.Ordinal).Count() != options.Length)
        {
            throw new ArgumentException("Raft group ids must be unique.", nameof(groupOptions));
        }

        _groups = options.ToFrozenDictionary(
            static option => option.GroupId,
            option => new RaftGroupNode(option, applierFactory(option.GroupId)),
            StringComparer.Ordinal);
        _dataGroupIds = _groups.Keys
            .Where(static groupId => groupId != CatalogGroupId)
            .Order(StringComparer.Ordinal)
            .ToArray();
        if (_dataGroupIds.Length == 0)
        {
            throw new ArgumentException("A multi-Raft node requires at least one data group.", nameof(groupOptions));
        }
    }

    public IReadOnlyCollection<string> GroupIds => _groups.Keys;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(_groups.Values.Select(group => group.StartAsync(cancellationToken).AsTask())).ConfigureAwait(false);
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await Task.WhenAll(_groups.Values.Select(group => group.StopAsync(cancellationToken).AsTask())).ConfigureAwait(false);
    }

    public string GetDataGroupId(Guid collectionId)
        => RaftGroupAssignment.GetDataGroupId(collectionId, _dataGroupIds);

    public ValueTask ReplicateCatalogAsync(RaftCommandEnvelope command, CancellationToken cancellationToken = default) =>
        _groups[CatalogGroupId].ReplicateAsync(command, cancellationToken);

    public ValueTask ReplicateDataAsync(
        Guid collectionId,
        RaftCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        string expectedGroupId = GetDataGroupId(collectionId);
        if (!string.Equals(command.GroupId, expectedGroupId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Collection '{collectionId}' is assigned to '{expectedGroupId}'.", nameof(command));
        }

        return _groups[expectedGroupId].ReplicateAsync(command, cancellationToken);
    }

    public ValueTask ApplyReadBarrierAsync(
        Guid? collectionId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        string groupId = collectionId.HasValue ? GetDataGroupId(collectionId.Value) : CatalogGroupId;
        return _groups[groupId].ApplyReadBarrierAsync(consistency, cancellationToken);
    }

    public RaftGroupNode GetGroup(string groupId) => _groups.TryGetValue(groupId, out RaftGroupNode? group)
        ? group
        : throw new KeyNotFoundException($"Raft group '{groupId}' is not hosted by this node.");

    public async ValueTask DisposeAsync()
    {
        foreach (RaftGroupNode group in _groups.Values)
        {
            await group.DisposeAsync().ConfigureAwait(false);
        }
    }
}
