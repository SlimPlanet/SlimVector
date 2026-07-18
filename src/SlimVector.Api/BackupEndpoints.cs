using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using SlimVector.Api.Contracts;
using SlimVector.Application.Backups;
using SlimVector.Application.Configuration;

namespace SlimVector.Api;

internal static class BackupEndpoints
{
    private const string AdminKeyHeader = "X-SlimVector-Admin-Key";

    public static IEndpointRouteBuilder MapBackupAdminApi(this IEndpointRouteBuilder endpoints)
    {
        ApiOptions options = endpoints.ServiceProvider.GetRequiredService<IOptions<ApiOptions>>().Value;
        if (!options.AdminEndpointsEnabled)
        {
            return endpoints;
        }

        RouteGroupBuilder backups = endpoints.MapGroup($"{options.RoutePrefix}/admin/backups");
        backups.WithRequestTimeout(ApiEndpoints.RequestTimeoutPolicyName);
        backups.WithTags("Backups");
        backups.MapPost("/", CreateAsync).WithName("CreateBackup").Produces<BackupResponse>(StatusCodes.Status201Created);
        backups.MapGet("/", ListAsync).WithName("ListBackups").Produces<BackupListResponse>();
        backups.MapPost("/{backupId}/verify", VerifyAsync).WithName("VerifyBackup").Produces<BackupOperationResponse>();
        backups.MapPost("/{backupId}/restore", RestoreFullAsync).WithName("RestoreFullBackup").Produces<BackupOperationResponse>();
        backups.MapPost("/{backupId}/restore-collection", RestoreCollectionAsync)
            .WithName("RestoreBackupCollection")
            .Produces<BackupOperationResponse>();
        return endpoints;
    }

    private static async Task<IResult> CreateAsync(
        HttpContext context,
        IBackupService service,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        BackupDescriptor backup = await service.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Created(
            $"{options.Value.RoutePrefix}/admin/backups/{Uri.EscapeDataString(backup.BackupId)}",
            ToResponse(backup));
    }

    private static async Task<IResult> ListAsync(
        HttpContext context,
        IBackupService service,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        IReadOnlyList<BackupDescriptor> backups = await service.ListBackupsAsync(cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new BackupListResponse { Backups = backups.Select(ToResponse).ToArray() });
    }

    private static async Task<IResult> VerifyAsync(
        string backupId,
        HttpContext context,
        IBackupService service,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        await service.VerifyBackupAsync(backupId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new BackupOperationResponse { Status = "verified" });
    }

    private static async Task<IResult> RestoreFullAsync(
        string backupId,
        HttpContext context,
        IBackupService service,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        await service.RestoreFullAsync(backupId, cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new BackupOperationResponse { Status = "restored" });
    }

    private static async Task<IResult> RestoreCollectionAsync(
        string backupId,
        RestoreCollectionRequest request,
        HttpContext context,
        IBackupService service,
        IOptions<ApiOptions> options,
        CancellationToken cancellationToken)
    {
        IResult? unauthorized = Authorize(context, options.Value);
        if (unauthorized is not null)
        {
            return unauthorized;
        }

        await service.RestoreCollectionAsync(
            backupId,
            request.CollectionName,
            request.RestoredName,
            request.Overwrite,
            cancellationToken).ConfigureAwait(false);
        return TypedResults.Ok(new BackupOperationResponse { Status = "restored" });
    }

    private static Microsoft.AspNetCore.Http.HttpResults.ProblemHttpResult? Authorize(HttpContext context, ApiOptions options)
    {
        string supplied = context.Request.Headers[AdminKeyHeader].ToString();
        byte[] expectedBytes = Encoding.UTF8.GetBytes(options.AdminApiKey);
        byte[] suppliedBytes = Encoding.UTF8.GetBytes(supplied);
        bool authorized = suppliedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(suppliedBytes, expectedBytes);
        CryptographicOperations.ZeroMemory(suppliedBytes);
        CryptographicOperations.ZeroMemory(expectedBytes);
        return authorized
            ? null
            : TypedResults.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "Administrator authentication required",
                type: "https://slimvector.dev/problems/admin_authentication_required",
                extensions: new Dictionary<string, object?> { ["code"] = "admin_authentication_required" });
    }

    private static BackupResponse ToResponse(BackupDescriptor backup) => new()
    {
        BackupId = backup.BackupId,
        CreatedAt = backup.CreatedAt,
        ParentBackupId = backup.ParentBackupId,
        CollectionCount = backup.CollectionCount,
        DocumentCount = backup.DocumentCount,
    };
}
