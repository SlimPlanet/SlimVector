using System.Buffers;
using SlimVector.Replication;

namespace SlimVector.Api;

internal static class GeoReplicationEndpoint
{
    private const int MaximumPayloadBytes = 64 * 1024 * 1024;

    public static IEndpointRouteBuilder MapGeoReplicationEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/geo/replicate", ReceiveAsync).ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> ReceiveAsync(
        HttpRequest request,
        IGeoReplicationReceiver receiver,
        CancellationToken cancellationToken)
    {
        if (!receiver.AcceptIncoming)
        {
            return TypedResults.NotFound();
        }

        if (request.ContentLength is > MaximumPayloadBytes)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        byte[] payload;
        try
        {
            payload = await ReadBoundedAsync(request.Body, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        string signature = request.Headers["X-SlimVector-Signature"].ToString();
        try
        {
            GeoReplicationReceiveResult result = await receiver
                .ReceiveAsync(payload, signature, cancellationToken)
                .ConfigureAwait(false);
            return result == GeoReplicationReceiveResult.Applied
                ? TypedResults.NoContent()
                : TypedResults.Ok();
        }
        catch (UnauthorizedAccessException)
        {
            return TypedResults.Unauthorized();
        }
        catch (GeoReplicationDivergenceException exception)
        {
            return TypedResults.Text(exception.Message, statusCode: StatusCodes.Status409Conflict);
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

                if (output.Length + read > MaximumPayloadBytes)
                {
                    throw new InvalidDataException("The geographic replication payload is too large.");
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
