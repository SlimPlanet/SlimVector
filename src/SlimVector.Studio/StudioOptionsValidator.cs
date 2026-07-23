using Microsoft.Extensions.Options;

namespace SlimVector.Studio;

public sealed class StudioOptionsValidator : IValidateOptions<StudioOptions>
{
    public ValidateOptionsResult Validate(string? name, StudioOptions options)
    {
        try
        {
            options.Validate();
            return ValidateOptionsResult.Success;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            return ValidateOptionsResult.Fail(exception.Message);
        }
    }
}
