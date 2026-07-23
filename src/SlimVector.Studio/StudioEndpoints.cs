using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SlimVector.DocIngestor.Models;
using SlimVector.Studio.Contracts;
using SlimVector.Studio.Services;

namespace SlimVector.Studio;

public static class StudioEndpoints
{
    public static IEndpointRouteBuilder MapStudioEndpoints(this IEndpointRouteBuilder endpoints)
    {
        RouteGroupBuilder api = endpoints.MapGroup("/studio/api");
        api.MapGet("/bootstrap", static (SlimVectorStudioService studio, CancellationToken cancellationToken) =>
            studio.GetBootstrapAsync(cancellationToken));
        api.MapGet("/model", static (SlimVectorStudioService studio, CancellationToken cancellationToken) =>
            studio.GetModelStatusAsync(cancellationToken));
        api.MapPost("/model/prepare", static (SlimVectorStudioService studio, CancellationToken cancellationToken) =>
            studio.PrepareModelAsync(cancellationToken));

        api.MapPost("/collections", static (CreateCollectionInput input, SlimVectorStudioService studio, CancellationToken cancellationToken) =>
            studio.CreateCollectionAsync(input, cancellationToken));
        api.MapPatch("/collections/{name}", static (string name, UpdateCollectionInput input, SlimVectorStudioService studio, CancellationToken cancellationToken) =>
            studio.UpdateCollectionAsync(name, input, cancellationToken));
        api.MapDelete("/collections/{name}", static (string name, SlimVectorStudioService studio, CancellationToken cancellationToken) =>
            studio.DeleteCollectionAsync(name, cancellationToken));

        api.MapPost("/ingest", IngestAsync).DisableAntiforgery();
        api.MapPost("/collections/{name}/search", SearchAsync)
            .Accepts<SearchInput>("application/json", StudioSerialization.MessagePackMediaType)
            .Produces<StudioSearchResponse>(
                StatusCodes.Status200OK,
                "application/json",
                StudioSerialization.MessagePackMediaType);
        api.MapGet("/collections/{name}/documents", static (
            string name,
            int? offset,
            int? limit,
            bool? includeVectors,
            SlimVectorStudioService studio,
            CancellationToken cancellationToken) => studio.GetDocumentsAsync(
                name,
                Math.Max(0, offset ?? 0),
                Math.Clamp(limit ?? 25, 1, 250),
                includeVectors ?? false,
                cancellationToken));
        api.MapPost("/collections/{name}/documents/mutate", static (
            string name,
            ManualMutationInput input,
            SlimVectorStudioService studio,
            CancellationToken cancellationToken) => studio.MutateDocumentsAsync(name, input, cancellationToken));
        api.MapPost("/collections/{name}/documents/delete", static (
            string name,
            DeleteDocumentsInput input,
            SlimVectorStudioService studio,
            CancellationToken cancellationToken) => studio.DeleteDocumentsAsync(name, input.Ids, input.Atomic, cancellationToken));

        api.MapGet("/runtime", static (SlimVectorStudioService studio) => studio.GetRuntime());
        api.MapGet("/backups", static (SlimVectorStudioService studio, CancellationToken cancellationToken) =>
            studio.ListBackupsAsync(cancellationToken));
        api.MapPost("/backups", static (SlimVectorStudioService studio, CancellationToken cancellationToken) =>
            studio.CreateBackupAsync(cancellationToken));
        api.MapPost("/backups/{backupId}/verify", static (string backupId, SlimVectorStudioService studio, CancellationToken cancellationToken) =>
            studio.VerifyBackupAsync(backupId, cancellationToken));
        api.MapPost("/backups/{backupId}/restore-collection", static (
            string backupId,
            RestoreCollectionInput input,
            SlimVectorStudioService studio,
            CancellationToken cancellationToken) => studio.RestoreCollectionAsync(backupId, input, cancellationToken));
        api.MapPost("/backups/{backupId}/restore", static (
            string backupId,
            ConfirmedRestoreInput input,
            SlimVectorStudioService studio,
            CancellationToken cancellationToken) => studio.RestoreFullAsync(backupId, input, cancellationToken));
        return endpoints;
    }

    private static async Task<IResult> SearchAsync(
        string name,
        HttpRequest request,
        SlimVectorStudioService studio,
        CancellationToken cancellationToken)
    {
        SearchInput input = await StudioSerialization.ReadSearchAsync(request, cancellationToken)
            .ConfigureAwait(false);
        StudioSearchResponse result = await studio.SearchAsync(name, input, cancellationToken).ConfigureAwait(false);
        return StudioSerialization.Ok(result);
    }

    private static async Task<IResult> IngestAsync(
        HttpRequest request,
        SlimVectorStudioService studio,
        IOptions<StudioOptions> studioOptions,
        CancellationToken cancellationToken)
    {
        if (!request.HasFormContentType)
        {
            throw new BadHttpRequestException("Un contenu multipart/form-data est requis.");
        }

        IFormCollection form = await request.ReadFormAsync(cancellationToken).ConfigureAwait(false);
        if (form.Files.Count == 0)
        {
            throw new BadHttpRequestException("Sélectionnez au moins un document.");
        }

        string collection = form["collection"].ToString();
        if (string.IsNullOrWhiteSpace(collection))
        {
            throw new BadHttpRequestException("Une collection cible est requise.");
        }

        ChunkingStrategy strategy = Enum.TryParse(form["strategy"], ignoreCase: true, out ChunkingStrategy parsed)
            ? parsed
            : ChunkingStrategy.Recursive;
        ChunkingOptions chunking = new()
        {
            Strategy = strategy,
            TargetTokens = ParseInt(form["targetTokens"], studioOptions.Value.Chunking.TargetTokens),
            MaximumTokens = ParseInt(form["maximumTokens"], studioOptions.Value.Chunking.MaximumTokens),
            OverlapTokens = ParseInt(form["overlapTokens"], studioOptions.Value.Chunking.OverlapTokens),
            MinimumChunkTokens = ParseInt(form["minimumChunkTokens"], 8),
        };
        chunking.Validate();
        ValidateMaximumChunkTokens(chunking.MaximumTokens);
        Dictionary<string, JsonElement> metadata = ParseMetadata(form["metadata"]);
        List<IngestResponse> results = new(form.Files.Count);
        foreach (IFormFile file in form.Files)
        {
            if (file.Length > studioOptions.Value.MaximumUploadBytes)
            {
                throw new BadHttpRequestException(
                    $"Le fichier « {file.FileName} » dépasse la limite d’envoi de {studioOptions.Value.MaximumUploadBytes} octets.",
                    StatusCodes.Status413PayloadTooLarge);
            }

            await using Stream stream = file.OpenReadStream();
            results.Add(await studio.IngestAsync(new IngestCommand
            {
                Collection = collection,
                Content = stream,
                FileName = file.FileName,
                ContentType = file.ContentType,
                Length = file.Length,
                Chunking = chunking,
                PreviewOnly = ParseBoolean(form["previewOnly"], defaultValue: false),
                ReplaceExisting = ParseBoolean(form["replaceExisting"], defaultValue: true),
                Atomic = ParseBoolean(form["atomic"], defaultValue: true),
                Metadata = metadata,
            }, cancellationToken).ConfigureAwait(false));
        }

        return Results.Ok(results);
    }

    private static void ValidateMaximumChunkTokens(int maximumTokens)
    {
        if (maximumTokens > StudioOptions.MaximumChunkTokens)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maximumTokens),
                $"La taille maximale d’un fragment ne peut pas dépasser {StudioOptions.MaximumChunkTokens} jetons.");
        }
    }

    private static int ParseInt(string? value, int fallback) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) ? parsed : fallback;

    private static bool ParseBoolean(string? value, bool defaultValue) =>
        bool.TryParse(value, out bool parsed) ? parsed : defaultValue;

    private static Dictionary<string, JsonElement> ParseMetadata(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        }

        using JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new JsonException("Les métadonnées doivent être un objet JSON.");
        }

        return document.RootElement.EnumerateObject().ToDictionary(
            static property => property.Name,
            static property => property.Value.Clone(),
            StringComparer.Ordinal);
    }
}
