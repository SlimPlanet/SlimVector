using System.Buffers;
using SlimVector.Application.Routing;

namespace SlimVector.Api;

internal static class DataNodeRpcEndpoint
{
    private const int MaximumPayloadBytes = 64 * 1024 * 1024;

    public static IEndpointRouteBuilder MapDataNodeRpcEndpoint(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/internal/data/replicate", ReceiveAsync).ExcludeFromDescription();
        return endpoints;
    }

    private static async Task<IResult> ReceiveAsync(
        HttpRequest request,
        IDataNodeRpcReceiver receiver,
        CancellationToken cancellationToken)
    {
        if (request.ContentLength is > MaximumPayloadBytes)
        {
            return TypedResults.StatusCode(StatusCodes.Status413PayloadTooLarge);
        }

        byte[] payload;
        try
        {
            payload = await ReadBoundedAsync(request.Body, cancellationToken).ConfigureAwait(false);
            await receiver.ReceiveAsync(
                payload,
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

                if (output.Length + read > MaximumPayloadBytes)
                {
                    throw new InvalidDataException("The internal RPC payload is too large.");
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
