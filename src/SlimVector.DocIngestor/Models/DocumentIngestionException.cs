namespace SlimVector.DocIngestor.Models;

public sealed class DocumentIngestionException : Exception
{
    public DocumentIngestionException(string code, string message)
        : base(message)
    {
        Code = code;
    }

    public DocumentIngestionException(string code, string message, Exception innerException)
        : base(message, innerException)
    {
        Code = code;
    }

    public string Code { get; }
}
