using Microsoft.Extensions.Options;

namespace SlimVector.Studio.Tests;

public sealed class StudioOptionsTests
{
    private readonly StudioOptionsValidator _validator = new();

    [Fact]
    public void DefaultChunkingConfigurationIsValid()
    {
        StudioOptions options = new();

        ValidateOptionsResult result = _validator.Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
        Assert.Equal(500, options.Chunking.TargetTokens);
        Assert.Equal(600, options.Chunking.MaximumTokens);
        Assert.Equal(100, options.Chunking.OverlapTokens);
    }

    [Fact]
    public void MaximumChunkSizeAcceptsBoundary()
    {
        StudioOptions options = new()
        {
            Chunking = new StudioChunkingOptions
            {
                TargetTokens = 600,
                MaximumTokens = StudioOptions.MaximumChunkTokens,
                OverlapTokens = 100,
            },
        };

        ValidateOptionsResult result = _validator.Validate(Options.DefaultName, options);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void ChunkingConfigurationRejectsInvalidBoundsAndInteractions()
    {
        StudioOptions aboveMaximum = new()
        {
            Chunking = new StudioChunkingOptions
            {
                TargetTokens = 600,
                MaximumTokens = StudioOptions.MaximumChunkTokens + 1,
                OverlapTokens = 100,
            },
        };
        StudioOptions overlapAtTarget = new()
        {
            Chunking = new StudioChunkingOptions
            {
                TargetTokens = 500,
                MaximumTokens = 600,
                OverlapTokens = 500,
            },
        };

        ValidateOptionsResult maximumResult = _validator.Validate(Options.DefaultName, aboveMaximum);
        ValidateOptionsResult overlapResult = _validator.Validate(Options.DefaultName, overlapAtTarget);

        Assert.True(maximumResult.Failed);
        Assert.Contains("1200", maximumResult.FailureMessage, StringComparison.Ordinal);
        Assert.True(overlapResult.Failed);
        Assert.Contains("OverlapTokens", overlapResult.FailureMessage, StringComparison.Ordinal);
    }
}
