using System.Text.Json;
using SlimVector.Domain;

namespace SlimVector.Storage;

public sealed class FileSystemClusterTopologyStore : IClusterTopologyStore, IDisposable
{
    private const string FileName = "cluster-topology-v2.json";
    private readonly string _path;
    private readonly bool _flushToDisk;
    private readonly StorageMetrics _metrics;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ClusterTopology _topology = new();
    private volatile bool _initialized;

    public FileSystemClusterTopologyStore(StorageSettings settings, StorageMetrics? metrics = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(settings.Path);
        _path = Path.Combine(Path.GetFullPath(settings.Path), FileName);
        _flushToDisk = settings.FlushToDisk;
        _metrics = metrics ?? new StorageMetrics();
    }

    public async ValueTask InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            if (File.Exists(_path))
            {
                await using FileStream stream = new(
                    _path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    16 * 1_024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                _topology = await JsonSerializer.DeserializeAsync(
                        stream,
                        StorageJsonContext.Default.ClusterTopology,
                        cancellationToken)
                    .ConfigureAwait(false) ?? throw Corruption("The persisted cluster topology is empty.");
                _metrics.RecordRead(stream.Length);
                _topology.Validate();
            }
            else
            {
                await WriteCoreAsync(_topology, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async ValueTask<ClusterTopology> GetAsync(CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return _topology with
            {
                Nodes = (ClusterNodeDescriptor[])_topology.Nodes.Clone(),
                DataGroups = _topology.DataGroups.Select(static group => group with
                {
                    Replicas = (DataGroupReplica[])group.Replicas.Clone(),
                }).ToArray(),
                ReplicaMoves = (ReplicaMoveDescriptor[])_topology.ReplicaMoves.Clone(),
            };
        }
        finally
        {
            _gate.Release();
        }
    }

    public ClusterTopology GetSnapshot()
    {
        EnsureInitialized();
        ClusterTopology topology = _topology;
        return topology with
        {
            Nodes = (ClusterNodeDescriptor[])topology.Nodes.Clone(),
            DataGroups = topology.DataGroups.Select(static group => group with
            {
                Replicas = (DataGroupReplica[])group.Replicas.Clone(),
            }).ToArray(),
            ReplicaMoves = (ReplicaMoveDescriptor[])topology.ReplicaMoves.Clone(),
        };
    }

    public async ValueTask ReplaceAsync(ClusterTopology topology, CancellationToken cancellationToken = default)
    {
        EnsureInitialized();
        ArgumentNullException.ThrowIfNull(topology);
        topology.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (topology.Epoch < _topology.Epoch)
            {
                throw new DomainException(
                    ErrorCodes.RoutingEpochMismatch,
                    $"Topology epoch {topology.Epoch} is older than local epoch {_topology.Epoch}.");
            }

            await WriteCoreAsync(topology, cancellationToken).ConfigureAwait(false);
            _topology = topology;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private async ValueTask WriteCoreAsync(ClusterTopology topology, CancellationToken cancellationToken)
    {
        string temporary = $"{_path}.{Guid.NewGuid():N}.tmp";
        try
        {
            long length;
            await using (FileStream stream = new(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                16 * 1_024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    topology,
                    StorageJsonContext.Default.ClusterTopology,
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                if (_flushToDisk)
                {
                    stream.Flush(flushToDisk: true);
                    _metrics.RecordDurableFlush();
                }

                length = stream.Position;
            }

            _metrics.RecordWrite(length);
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("The cluster topology store is not initialized.");
        }
    }

    private static DomainException Corruption(string message) => new(ErrorCodes.StorageCorrupted, message);
}
