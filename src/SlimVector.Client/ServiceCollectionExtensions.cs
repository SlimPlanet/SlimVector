using Microsoft.Extensions.DependencyInjection;

namespace SlimVector.Client;

public static class ServiceCollectionExtensions
{
    public static IHttpClientBuilder AddSlimVectorClient(this IServiceCollection services, Uri baseAddress)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(baseAddress);
        if (!baseAddress.IsAbsoluteUri)
        {
            throw new ArgumentException("SlimVector base address must be absolute.", nameof(baseAddress));
        }

        return services.AddHttpClient<SlimVectorClient>(client => client.BaseAddress = baseAddress);
    }
}
