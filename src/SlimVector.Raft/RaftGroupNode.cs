using System.Net;
using DotNext.Net.Cluster.Consensus.Raft;
using DotNext.Net.Cluster.Consensus.Raft.Membership;
using DotNext.Net.Cluster.Consensus.Raft.StateMachine;
using SlimVector.Domain;
using SlimVector.Raft.Commands;
using SlimVector.Raft.StateMachine;

namespace SlimVector.Raft;

public sealed class RaftGroupNode : IAsyncDisposable
{
    private readonly RaftGroupNodeOptions _options;
    private readonly SlimVectorRaftStateMachine _stateMachine;
    private readonly WriteAheadLog _writeAheadLog;
    private readonly RaftCluster _cluster;
    private readonly SemaphoreSlim _lifecycleLock = new(1, 1);
    private bool _started;
    private bool _disposed;

    public RaftGroupNode(RaftGroupNodeOptions options, IRaftCommandApplier applier)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(applier);
        options.Validate();
        _options = options with { StoragePath = Path.GetFullPath(options.StoragePath) };

        string groupPath = Path.Combine(_options.StoragePath, SanitizeGroupId(_options.GroupId));
        DirectoryInfo snapshotPath = Directory.CreateDirectory(Path.Combine(groupPath, "snapshots"));
        DirectoryInfo writeAheadLogPath = Directory.CreateDirectory(Path.Combine(groupPath, "wal"));
        _stateMachine = new SlimVectorRaftStateMachine(
            _options.GroupId,
            snapshotPath,
            applier,
            _options.SnapshotEveryEntries);
        _writeAheadLog = new WriteAheadLog(
            new WriteAheadLog.Options
            {
                Location = writeAheadLogPath.FullName,
                HashAlgorithm = WriteAheadLog.IntegrityHashAlgorithm.XxHash3,
            },
            _stateMachine);

        RaftCluster.TcpConfiguration configuration = CreateConfiguration(_options);
        _cluster = new RaftCluster(configuration) { AuditTrail = _writeAheadLog };
    }

    public string GroupId => _options.GroupId;

    public IPEndPoint LocalEndpoint => _options.LocalEndpoint;

    public bool IsLeader => _started && !_cluster.LeadershipToken.IsCancellationRequested;

    public long Term => _cluster.Term;

    public long LastAppliedIndex => _writeAheadLog.LastAppliedIndex;

    public long LastCommittedIndex => _writeAheadLog.LastCommittedEntryIndex;

    public long AppliedCommandCount => _stateMachine.AppliedCommandCount;

    public EndPoint? LeaderEndpoint => _cluster.Leader?.EndPoint;

    public async ValueTask StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started)
            {
                return;
            }

            await _stateMachine.RestoreAsync(cancellationToken).ConfigureAwait(false);
            await _cluster.StartAsync(cancellationToken).ConfigureAwait(false);
            _started = true;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask StopAsync(CancellationToken cancellationToken = default)
    {
        await _lifecycleLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                return;
            }

            await _cluster.StopAsync(cancellationToken).ConfigureAwait(false);
            _started = false;
        }
        finally
        {
            _lifecycleLock.Release();
        }
    }

    public async ValueTask ReplicateAsync(RaftCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(command);
        if (!string.Equals(command.GroupId, GroupId, StringComparison.Ordinal))
        {
            throw new ArgumentException($"Command targets group '{command.GroupId}' instead of '{GroupId}'.", nameof(command));
        }

        byte[] payload = RaftCommandCodec.Serialize(command);
        try
        {
            await _cluster.ReplicateAsync(payload, token: cancellationToken).ConfigureAwait(false);
        }
        catch (NotLeaderException exception)
        {
            throw Unavailable(ConsensusFailureKind.NotLeader, exception);
        }
        catch (QuorumUnreachableException exception)
        {
            throw Unavailable(ConsensusFailureKind.QuorumUnavailable, exception);
        }
    }

    public async ValueTask ApplyReadBarrierAsync(
        ReadConsistency consistency,
        CancellationToken cancellationToken = default)
    {
        try
        {
            switch (consistency)
            {
                case ReadConsistency.Stale:
                    return;
                case ReadConsistency.Leader:
                    await _cluster.ApplyReadBarrierAsync(ReadBarrierType.Weak, cancellationToken).ConfigureAwait(false);
                    break;
                case ReadConsistency.Linearizable:
                    await _cluster.ApplyReadBarrierAsync(ReadBarrierType.Strong, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(consistency), consistency, "Unknown read consistency.");
            }
        }
        catch (QuorumUnreachableException exception)
        {
            throw Unavailable(ConsensusFailureKind.QuorumUnavailable, exception);
        }
    }

    public async ValueTask<EndPoint> WaitForLeaderAsync(
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        RaftClusterMember leader = await _cluster.WaitForLeaderAsync(timeout, cancellationToken).ConfigureAwait(false);
        return leader.EndPoint;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_started)
        {
            await _cluster.StopAsync(CancellationToken.None).ConfigureAwait(false);
            _started = false;
        }

        await _cluster.DisposeAsync().ConfigureAwait(false);
        await _writeAheadLog.DisposeAsync().ConfigureAwait(false);
        await _stateMachine.DisposeAsync().ConfigureAwait(false);
        _lifecycleLock.Dispose();
    }

    private static RaftCluster.TcpConfiguration CreateConfiguration(RaftGroupNodeOptions options)
    {
        RaftCluster.TcpConfiguration configuration = new(options.LocalEndpoint)
        {
            RequestTimeout = options.RequestTimeout,
            LowerElectionTimeout = options.LowerElectionTimeoutMilliseconds,
            UpperElectionTimeout = options.UpperElectionTimeoutMilliseconds,
            HeartbeatThreshold = options.HeartbeatThreshold,
            TransmissionBlockSize = options.TransmissionBlockSize,
            ColdStart = options.Members.Count == 1,
            ConfigurationStorage = null,
        };

        if (options.Members.Count > 1)
        {
            if (configuration.ConfigurationStorage is not InMemoryClusterConfigurationStorage<EndPoint> storage)
            {
                throw new InvalidOperationException("DotNext did not create in-memory static membership storage.");
            }

            InMemoryClusterConfigurationStorage<EndPoint>.Builder builder = storage.CreateInitialConfigurationBuilder();
            foreach (IPEndPoint member in options.Members.OrderBy(static endpoint => endpoint.Port))
            {
                builder.Add(member);
            }

            builder.Build();
        }

        return configuration;
    }

    private static string SanitizeGroupId(string groupId)
    {
        if (groupId.Length > 128 || groupId.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) && character is not '.' and not '_' and not '-'))
        {
            throw new ArgumentException("Raft group ids may contain only ASCII letters, digits, '.', '_' and '-'.", nameof(groupId));
        }

        return groupId;
    }

    private ConsensusUnavailableException Unavailable(ConsensusFailureKind kind, Exception exception) =>
        new(GroupId, kind, LeaderEndpoint?.ToString(), exception);
}
