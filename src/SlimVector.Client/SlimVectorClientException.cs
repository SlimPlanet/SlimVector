using System.Net;

namespace SlimVector.Client;

public sealed class SlimVectorClientException : Exception
{
    public SlimVectorClientException(HttpStatusCode statusCode, string? errorCode, string message)
        : base(message)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
    }

    public HttpStatusCode StatusCode { get; }

    public string? ErrorCode { get; }
}
