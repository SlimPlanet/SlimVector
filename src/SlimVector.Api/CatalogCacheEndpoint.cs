using System.Buffers;
using SlimVector.Application.Routing;

namespace SlimVector.Api;

internal static class CatalogCacheEndpoint
{
    private const int MaximumSnapshotBytes = 64 * 1024 * 1024;

    public static IEndpointRouteBuilder MapCatalogCacheEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/internal/catalog/snapshot", CaptureAsync).ExcludeFromDescription();
        endpoints.MapPost("/internal/catalog/snapshot", InstallAsync).ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> CaptureAsync(
        HttpResponse response,
        ILocalCatalogSnapshotExchange exchange,
        CancellationToken cancellationToken)
    {
        byte[] snapshot = await exchange.CaptureAsync(requireLeaderBarrier: true, cancellationToken)
            .ConfigureAwait(false);
        string? signature = exchange.Sign(snapshot);
        if (signature is not null)
        {
            response.Headers["X-SlimVector-Signature"] = signature;
        }

        return Results.Bytes(snapshot, "application/x-memorypack");
    }

    private static async Task<IResult> InstallAsync(
        HttpRequest request,
        ILocalCatalogSnapshotExchange exchange,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaximumSnapshotBytes)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        try
        {
            byte[] snapshot = await ReadBoundedAsync(request.Body, cancellationToken).ConfigureAwait(false);
            await exchange.InstallAsync(
                snapshot,
                request.Headers["X-SlimVector-Signature"].ToString(),
                cancellationToken).ConfigureAwait(false);
            return TypedResults.NoContent();
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
        catch (InvalidDataException exception)
        {
            return TypedResults.Text(exception.Message, statusCode: StatusCodes.Status400BadRequest);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream input, CancellationToken cancellationToken)
    {
        byte[] rented = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using MemoryStream output = new();
            while (true)
            {
                int read = await input.ReadAsync(rented, cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    return output.ToArray();
                }

                if (output.Length + read > MaximumSnapshotBytes)
                {
                    throw new InvalidDataException("The catalog snapshot is too large.");
                }

                output.Write(rented, 0, read);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }
}
