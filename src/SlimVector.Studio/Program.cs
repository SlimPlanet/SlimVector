using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Http.Features;
using SlimVector.Application;
using SlimVector.DocIngestor;
using SlimVector.DocIngestor.Models;
using SlimVector.Domain;
using SlimVector.Studio;
using SlimVector.Studio.Services;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
StudioOptions studioOptions = builder.Configuration.GetSection(StudioOptions.SectionName).Get<StudioOptions>() ?? new StudioOptions();
if (studioOptions.MaximumUploadBytes < 1024 * 1024)
{
    throw new InvalidOperationException("Studio:MaximumUploadBytes must be at least 1 MB.");
}

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = studioOptions.MaximumUploadBytes + 1024 * 1024);
builder.Services.Configure<FormOptions>(options => options.MultipartBodyLengthLimit = studioOptions.MaximumUploadBytes + 1024 * 1024);
builder.Services.ConfigureHttpJsonOptions(static options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
});
builder.Services.AddSingleton(studioOptions);
builder.Services.AddSlimVector(builder.Configuration);
builder.Services.AddSlimVectorDocIngestor(options =>
{
    options.AutoDownload = studioOptions.AutoDownloadModel;
    if (!string.IsNullOrWhiteSpace(studioOptions.ModelDirectory))
    {
        options.ModelDirectory = studioOptions.ModelDirectory;
    }
});
builder.Services.AddSingleton<SlimVectorStudioService>();
builder.Services.AddHostedService<StudioSeedHostedService>();

WebApplication app = builder.Build();
app.Use(async (context, next) =>
{
    try
    {
        await next(context).ConfigureAwait(false);
    }
    catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
    {
        // The browser closed the request; there is no response left to write.
    }
    catch (Exception exception)
    {
        (int status, string code, string title) = exception switch
        {
            DomainException domain when domain.Code is ErrorCodes.CollectionNotFound or ErrorCodes.DocumentNotFound =>
                (StatusCodes.Status404NotFound, domain.Code, "Resource not found"),
            DomainException domain when domain.Code is ErrorCodes.CollectionAlreadyExists or ErrorCodes.DocumentAlreadyExists =>
                (StatusCodes.Status409Conflict, domain.Code, "Resource already exists"),
            DomainException domain => (StatusCodes.Status400BadRequest, domain.Code, "SlimVector rejected the request"),
            DocumentIngestionException ingestion => (StatusCodes.Status422UnprocessableEntity, ingestion.Code, "Document ingestion failed"),
            BadHttpRequestException bad => (bad.StatusCode, "invalid_request", "Invalid request"),
            JsonException => (StatusCodes.Status400BadRequest, "invalid_json", "Invalid JSON"),
            ArgumentException => (StatusCodes.Status400BadRequest, "invalid_argument", "Invalid argument"),
            _ => (StatusCodes.Status500InternalServerError, "studio_error", "SlimVector Studio failed"),
        };
        context.Response.StatusCode = status;
        await Results.Problem(
            detail: exception.Message,
            instance: context.Request.Path,
            statusCode: status,
            title: title,
            type: $"https://slimvector.dev/problems/{code}",
            extensions: new Dictionary<string, object?>
            {
                ["code"] = code,
                ["traceId"] = context.TraceIdentifier,
            }).ExecuteAsync(context).ConfigureAwait(false);
    }
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.MapGet("/health/live", static () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", static (SlimVector.Raft.IConsensusCoordinator consensus) =>
    consensus.IsReady ? Results.Ok(new { status = "ready" }) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));
app.MapStudioEndpoints();
app.MapFallbackToFile("index.html");
app.Run();

public partial class Program;
