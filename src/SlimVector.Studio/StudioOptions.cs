namespace SlimVector.Studio;

public sealed class StudioOptions
{
    public const string SectionName = "Studio";

    public long MaximumUploadBytes { get; set; } = 128L * 1024 * 1024;

    public string DefaultCollection { get; set; } = "documents";

    public string? ModelDirectory { get; set; }

    public bool AutoDownloadModel { get; set; } = true;
}
