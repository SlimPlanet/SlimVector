namespace SlimVector.Replication;

public interface IGeoReplicationService : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken = default);

    ValueTask StopAsync(CancellationToken cancellationToken = default);

    ValueTask PublishAsync(GeoReplicationEvent replicationEvent, CancellationToken cancellationToken = default);

    GeoReplicationMetrics GetMetrics();
}

public interface IGeoReplicationReceiver
{
    bool AcceptIncoming { get; }

    ValueTask<GeoReplicationReceiveResult> ReceiveAsync(
        ReadOnlyMemory<byte> payload,
        string signature,
        CancellationToken cancellationToken = default);

    GeoReplicationMetrics GetMetrics();
}

public enum GeoReplicationReceiveResult
{
    Applied,
    Duplicate,
}

public sealed class GeoReplicationDivergenceException : Exception
{
    public GeoReplicationDivergenceException(string message)
        : base(message)
    {
    }
}
