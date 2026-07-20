using System.Collections.Concurrent;
using SlimVector.Domain;
using SlimVector.Raft.Commands;

namespace SlimVector.Raft;

public sealed class MultiRaftNode : IAsyncDisposable
{
    public const string CatalogGroupId = "catalog";
    private readonly ConcurrentDictionary<string, RaftGroupNode> _groups = new(StringComparer.Ordinal);
    private readonly Func<string, IRaftCommandApplier> _applierFactory;
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private volatile bool _started;

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

        _applierFactory = applierFactory;
        foreach (RaftGroupNodeOptions option in options)
        {
            if (!_groups.TryAdd(option.GroupId, new RaftGroupNode(option, applierFactory(option.GroupId))))
            {
                throw new ArgumentException($"Raft group id '{option.GroupId}' is duplicated.", nameof(groupOptions));
            }
        }

    }

    public IReadOnlyCollection<string> GroupIds => _groups.Keys.ToArray();

    public IReadOnlyList<string> DataGroupIds => _groups.Keys
        .Where(static groupId => groupId != CatalogGroupId)
        .Order(StringComparer.Ordinal)
        .ToArray();

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            await Task.WhenAll(_groups.Values.Select(group => group.StartAsync(cancellationToken).AsTask())).ConfigureAwait(false);
            _started = true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            await Task.WhenAll(_groups.Values.Select(group => group.StopAsync(cancellationToken).AsTask())).ConfigureAwait(false);
            _started = false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask AddGroupAsync(
        RaftGroupNodeOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (string.Equals(options.GroupId, CatalogGroupId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The catalog group cannot be added dynamically.", nameof(options));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_groups.ContainsKey(options.GroupId))
            {
                return;
            }

            RaftGroupNode group = new(options, _applierFactory(options.GroupId));
            if (!_groups.TryAdd(options.GroupId, group))
            {
                await group.DisposeAsync().ConfigureAwait(false);
                return;
            }

            try
            {
                if (_started)
                {
                    await group.StartAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch
            {
                _groups.TryRemove(options.GroupId, out _);
                await group.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask RemoveGroupAsync(string groupId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        if (string.Equals(groupId, CatalogGroupId, StringComparison.Ordinal))
        {
            throw new ArgumentException("The catalog group cannot be removed dynamically.", nameof(groupId));
        }

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_groups.TryRemove(groupId, out RaftGroupNode? group))
            {
                return;
            }

            if (_started)
            {
                await group.StopAsync(cancellationToken).ConfigureAwait(false);
            }

            await group.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public string GetDataGroupId(Guid collectionId)
        => RaftGroupAssignment.GetDataGroupId(collectionId, DataGroupIds);

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

    public ValueTask ReplicateDataGroupAsync(
        string groupId,
        RaftCommandEnvelope command,
        CancellationToken cancellationToken = default)
    {
        if (!_groups.TryGetValue(groupId, out RaftGroupNode? group) ||
            string.Equals(groupId, CatalogGroupId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Data group '{groupId}' is not hosted by this node.", nameof(groupId));
        }

        if (!string.Equals(command.GroupId, groupId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Command targets '{command.GroupId}', not '{groupId}'.", nameof(command));
        }

        return group.ReplicateAsync(command, cancellationToken);
    }

    public ValueTask ApplyReadBarrierAsync(
        Guid? collectionId,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        string groupId = collectionId.HasValue ? GetDataGroupId(collectionId.Value) : CatalogGroupId;
        return _groups[groupId].ApplyReadBarrierAsync(consistency, cancellationToken);
    }

    public async ValueTask ApplyReadBarriersAsync(
        IEnumerable<string> groupIds,
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        string[] groups = groupIds.Distinct(StringComparer.Ordinal).ToArray();
        await Task.WhenAll(groups.Select(groupId => _groups[groupId]
            .ApplyReadBarrierAsync(consistency, cancellationToken).AsTask())).ConfigureAwait(false);
    }

    public RaftGroupNode GetGroup(string groupId) => _groups.TryGetValue(groupId, out RaftGroupNode? group)
        ? group
        : throw new KeyNotFoundException($"Raft group '{groupId}' is not hosted by this node.");

    public IReadOnlyList<ClusterMembershipStatus> GetMembershipStatuses() => _groups.Values
        .OrderBy(static group => group.GroupId, StringComparer.Ordinal)
        .Select(static group => new ClusterMembershipStatus(group.GroupId, group.GetMemberStatuses(), null, null))
        .ToArray();

    public async ValueTask DisposeAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (RaftGroupNode group in _groups.Values)
            {
                await group.DisposeAsync().ConfigureAwait(false);
            }

            _groups.Clear();
        }
        finally
        {
            _lifecycleGate.Release();
            _lifecycleGate.Dispose();
        }
    }
}
