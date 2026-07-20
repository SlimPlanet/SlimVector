using SlimVector.Domain;

namespace SlimVector.Storage;

public interface IClusterTopologyStore
{
    ValueTask InitializeAsync(CancellationToken cancellationToken = default);

    ValueTask<ClusterTopology> GetAsync(CancellationToken cancellationToken = default);

    ClusterTopology GetSnapshot() =>
        throw new NotSupportedException("Synchronous topology snapshots are not supported by this store.");

    ValueTask ReplaceAsync(ClusterTopology topology, CancellationToken cancellationToken = default);
}
