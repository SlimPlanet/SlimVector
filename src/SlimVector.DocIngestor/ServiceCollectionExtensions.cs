using Microsoft.Extensions.DependencyInjection;
using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Chunking;
using SlimVector.DocIngestor.Embeddings;
using SlimVector.DocIngestor.Extractors;
using SlimVector.DocIngestor.Pipeline;

namespace SlimVector.DocIngestor;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSlimVectorDocIngestor(
        this IServiceCollection services,
        Action<HuggingFaceEmbeddingOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        HuggingFaceEmbeddingOptions options = new();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.AddHttpClient("SlimVector.DocIngestor.HuggingFace", static client =>
        {
            client.Timeout = Timeout.InfiniteTimeSpan;
            client.DefaultRequestHeaders.UserAgent.ParseAdd("SlimVector-DocIngestor/1.0");
        });
        services.AddSingleton<ITokenCounter, ApproximateTokenCounter>();
        services.AddSingleton<ITextChunker, RecursiveTextChunker>();
        services.AddSingleton<IDocumentTextExtractor, PdfTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, DocxTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, PptxTextExtractor>();
        services.AddSingleton<IDocumentTextExtractor, PlainTextExtractor>();
        services.AddSingleton<DocumentExtractorRouter>();
        services.AddSingleton<IEmbeddingGenerator>(provider =>
        {
            HttpClient client = provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient("SlimVector.DocIngestor.HuggingFace");
            return new OnnxSentenceEmbeddingGenerator(client, provider.GetRequiredService<HuggingFaceEmbeddingOptions>());
        });
        services.AddSingleton<IDocumentIngestionPipeline, DocumentIngestionPipeline>();
        return services;
    }
}
