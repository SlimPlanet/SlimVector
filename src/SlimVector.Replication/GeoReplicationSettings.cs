using SlimVector.Domain;

namespace SlimVector.Replication;

public sealed record GeoReplicationSettings
{
    public bool Enabled { get; init; }

    public bool AcceptIncoming { get; init; }

    public GeoReplicationMode Mode { get; init; } = GeoReplicationMode.Asynchronous;

    public required string OriginId { get; init; }

    public Uri? SecondaryEndpoint { get; init; }

    public required string SharedSecret { get; init; }

    public required string StatePath { get; init; }

    public int QueueCapacity { get; init; } = 10_000;

    public TimeSpan RetryMinimumDelay { get; init; } = TimeSpan.FromSeconds(1);

    public TimeSpan RetryMaximumDelay { get; init; } = TimeSpan.FromMinutes(1);

    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
