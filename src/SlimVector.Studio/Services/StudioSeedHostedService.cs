using Microsoft.Extensions.Options;
using SlimVector.Application;
using SlimVector.DocIngestor.Abstractions;
using SlimVector.Domain;

namespace SlimVector.Studio.Services;

public sealed class StudioSeedHostedService : IHostedService
{
    private readonly ISlimVectorDatabase _database;
    private readonly IEmbeddingGenerator _embeddings;
    private readonly StudioOptions _options;

    public StudioSeedHostedService(
        ISlimVectorDatabase database,
        IEmbeddingGenerator embeddings,
        IOptions<StudioOptions> options)
    {
        _database = database;
        _embeddings = embeddings;
        _options = options.Value;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _database.GetOrCreateCollectionAsync(
            _options.DefaultCollection,
            _embeddings.Dimension,
            DistanceMetric.Cosine,
            new VectorIndexConfiguration { Kind = VectorIndexKind.Auto },
            cancellationToken).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
