using System.Net;
using System.Security.Cryptography;
using System.Text;
using DotNext.Net.Cluster;
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
    private readonly SemaphoreSlim _membershipLock = new(1, 1);
    private readonly string _membershipPath;
    private bool _started;
    private bool _disposed;

    public RaftGroupNode(RaftGroupNodeOptions options, IRaftCommandApplier applier)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(applier);
        options.Validate();
        string storagePath = Path.GetFullPath(options.StoragePath);
        string preliminaryGroupPath = Path.Combine(storagePath, SanitizeGroupId(options.GroupId));
        Directory.CreateDirectory(preliminaryGroupPath);
        _membershipPath = Path.Combine(preliminaryGroupPath, "membership-v1");
        IReadOnlyList<IPEndPoint> persistedMembers = LoadMembership(_membershipPath) ?? options.Members;
        _options = options with
        {
            StoragePath = storagePath,
            Members = persistedMembers,
            StartAsJoiningMember = persistedMembers.Count == 0 && options.StartAsJoiningMember,
        };
        _options.Validate();

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
        _cluster.MemberAdded += OnMembershipChanged;
        _cluster.MemberRemoved += OnMembershipChanged;
        if (!File.Exists(_membershipPath) && _options.Members.Count > 0)
        {
            PersistMembership(_options.Members);
        }
    }

    public string GroupId => _options.GroupId;

    public IPEndPoint LocalEndpoint => _options.LocalEndpoint;

    public bool IsLeader => _started && !_cluster.LeadershipToken.IsCancellationRequested;

    public long Term => _cluster.Term;

    public long LastAppliedIndex => _writeAheadLog.LastAppliedIndex;

    public long LastCommittedIndex => _writeAheadLog.LastCommittedEntryIndex;

    public long AppliedCommandCount => _stateMachine.AppliedCommandCount;

    public EndPoint? LeaderEndpoint => _cluster.Leader?.EndPoint;

    public IReadOnlyList<RaftMemberStatus> GetMemberStatuses() => _cluster.Members
        .OrderBy(static member => member.EndPoint.ToString(), StringComparer.Ordinal)
        .Select(ToMemberStatus)
        .ToArray();

    public async ValueTask<bool> AddMemberAsync(IPEndPoint endpoint, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(endpoint);
        await _membershipLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _cluster.AddMemberAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (NotLeaderException exception)
        {
            throw Unavailable(ConsensusFailureKind.NotLeader, exception);
        }
        catch (QuorumUnreachableException exception)
        {
            throw Unavailable(ConsensusFailureKind.QuorumUnavailable, exception);
        }
        finally
        {
            PersistCurrentMembership();
            _membershipLock.Release();
        }
    }

    public async ValueTask<bool> RemoveMemberAsync(IPEndPoint endpoint, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(endpoint);
        await _membershipLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await _cluster.RemoveMemberAsync(endpoint, cancellationToken).ConfigureAwait(false);
        }
        catch (NotLeaderException exception)
        {
            throw Unavailable(ConsensusFailureKind.NotLeader, exception);
        }
        catch (QuorumUnreachableException exception)
        {
            throw Unavailable(ConsensusFailureKind.QuorumUnavailable, exception);
        }
        finally
        {
            PersistCurrentMembership();
            _membershipLock.Release();
        }
    }

    public async ValueTask<bool> TransferLeadershipAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _membershipLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ((ICluster)_cluster).ResignAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _membershipLock.Release();
        }
    }

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
        _membershipLock.Dispose();
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
            ColdStart = !options.StartAsJoiningMember && options.Members.Count == 1,
            ConfigurationStorage = null,
            WarmupRounds = options.WarmupRounds,
            MaxReplicationLag = options.MaximumReplicationLag,
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

    private RaftMemberStatus ToMemberStatus(RaftClusterMember member)
    {
        long? matchIndex = null;
        long? lag = null;
        if (!member.IsRemote)
        {
            matchIndex = LastAppliedIndex;
            lag = Math.Max(0, LastCommittedIndex - LastAppliedIndex);
        }

        return new RaftMemberStatus(
            GroupId,
            member.EndPoint.ToString() ?? string.Empty,
            member.IsLeader,
            member.IsRemote,
            member.Status.ToString(),
            matchIndex,
            lag);
    }

    private void PersistCurrentMembership()
    {
        if (_started)
        {
            PersistMembership(_cluster.Members.Select(static member => member.EndPoint).OfType<IPEndPoint>());
        }
    }

    private void OnMembershipChanged(object? sender, RaftClusterMemberEventArgs<RaftClusterMember> eventArgs) =>
        PersistCurrentMembership();

    private void PersistMembership(IEnumerable<IPEndPoint> members)
    {
        string body = string.Join(
            '\n',
            members.OrderBy(static endpoint => endpoint.Address.ToString(), StringComparer.Ordinal)
                .ThenBy(static endpoint => endpoint.Port)
                .Select(static endpoint => endpoint.ToString()));
        string checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        string temporary = _membershipPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using FileStream stream = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            byte[] contents = Encoding.UTF8.GetBytes("1\n" + checksum + "\n" + body + "\n");
            stream.Write(contents);
            stream.Flush(flushToDisk: true);
            File.Move(temporary, _membershipPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static IPEndPoint[]? LoadMembership(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        string[] lines = File.ReadAllLines(path, Encoding.UTF8);
        if (lines.Length < 3 || !string.Equals(lines[0], "1", StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Raft membership file '{path}' has an unsupported format.");
        }

        string body = string.Join('\n', lines.Skip(2).Where(static line => line.Length > 0));
        string checksum = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(body)));
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(lines[1]),
                Encoding.ASCII.GetBytes(checksum)))
        {
            throw new InvalidDataException($"Raft membership file '{path}' failed checksum validation.");
        }

        IPEndPoint[] members = lines.Skip(2)
            .Where(static line => line.Length > 0)
            .Select(line => IPEndPoint.TryParse(line, out IPEndPoint? endpoint)
                ? endpoint
                : throw new InvalidDataException($"Raft membership endpoint '{line}' is invalid."))
            .ToArray();
        if (members.Length == 0 || members.Distinct().Count() != members.Length)
        {
            throw new InvalidDataException($"Raft membership file '{path}' is empty or contains duplicates.");
        }

        return members;
    }

    private ConsensusUnavailableException Unavailable(ConsensusFailureKind kind, Exception exception) =>
        new(GroupId, kind, LeaderEndpoint?.ToString(), exception);
}
