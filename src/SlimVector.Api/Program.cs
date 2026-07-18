using System.Text.Json;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using SlimVector.Api;
using SlimVector.Api.Contracts;
using SlimVector.Application;
using SlimVector.Application.Backups;
using SlimVector.Application.Configuration;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Replication;

WebApplicationBuilder builder = WebApplication.CreateSlimBuilder(args);
long configuredMaximumBodyBytes = builder.Configuration.GetValue(
    $"{ApiOptions.SectionName}:MaximumRequestBodyBytes",
    16L * 1024 * 1024);
bool configuredStructuredLogging = builder.Configuration.GetValue(
    $"{ObservabilityOptions.SectionName}:StructuredConsoleLogging",
    true);
if (configuredStructuredLogging)
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(options => options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ");
}

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = configuredMaximumBodyBytes);
builder.Services.AddSlimVector(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddOpenApi();
builder.Services.AddRequestTimeouts();
builder.Services.AddOptions<RequestTimeoutOptions>()
    .Configure<Microsoft.Extensions.Options.IOptions<ApiOptions>>((options, apiOptions) =>
    {
        RequestTimeoutPolicy policy = new()
        {
            Timeout = apiOptions.Value.RequestTimeout,
            TimeoutStatusCode = StatusCodes.Status504GatewayTimeout,
            WriteTimeoutResponse = WriteTimeoutProblemAsync,
        };
        options.DefaultPolicy = policy;
        options.AddPolicy(ApiEndpoints.RequestTimeoutPolicyName, policy);
    });
builder.Services.ConfigureHttpJsonOptions(static options =>
{
    options.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, ApiJsonContext.Default);
});
WebApplication app = builder.Build();
app.UseExceptionHandler();
GeoReplicationOptions geoOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<GeoReplicationOptions>>().Value;
ApiOptions apiOptions = app.Services.GetRequiredService<Microsoft.Extensions.Options.IOptions<ApiOptions>>().Value;
app.Use(async (context, next) =>
{
    if (context.Request.ContentLength > apiOptions.MaximumRequestBodyBytes)
    {
        throw new DomainException(
            ErrorCodes.RequestTooLarge,
            $"Request body exceeds the configured limit of {apiOptions.MaximumRequestBodyBytes} bytes.");
    }

    await next(context).ConfigureAwait(false);
});
app.UseRequestTimeouts();
app.Use(async (context, next) =>
{
    bool writesPublicApi = context.Request.Path.StartsWithSegments(apiOptions.RoutePrefix, StringComparison.Ordinal) &&
        !HttpMethods.IsGet(context.Request.Method) &&
        !HttpMethods.IsHead(context.Request.Method) &&
        !HttpMethods.IsOptions(context.Request.Method);
    if (writesPublicApi && geoOptions.AcceptIncoming && geoOptions.ReadOnlySecondary)
    {
        throw new DomainException(
            ErrorCodes.ReadOnlySecondary,
            "This disaster-recovery secondary is read-only until it is explicitly promoted.");
    }

    await next(context).ConfigureAwait(false);
});
app.MapOpenApi("/openapi/{documentName}.json");
app.MapGet("/health/live", static () => TypedResults.Ok(new HealthResponse { Status = "live" }));
app.MapGet("/health/ready", static (IConsensusCoordinator consensus) => consensus.IsReady
    ? Results.Ok(new HealthResponse { Status = "ready" })
    : TypedResults.Json(
        new HealthResponse { Status = "not-ready" },
        ApiJsonContext.Default.HealthResponse,
        statusCode: StatusCodes.Status503ServiceUnavailable));
app.MapGet("/metrics", static (
    ISlimVectorDatabase database,
    OperationalMetrics operationalMetrics,
    IConsensusCoordinator consensus,
    IWriteScheduler writeScheduler,
    IBackupService backups,
    IGeoReplicationService geoReplication,
    IGeoReplicationReceiver geoReceiver,
    Microsoft.Extensions.Options.IOptions<ObservabilityOptions> observability) =>
{
    if (!observability.Value.MetricsEnabled)
    {
        return Results.NotFound();
    }

    long memory = GC.GetTotalMemory(forceFullCollection: false);
    OperationalMetricsSnapshot operations = operationalMetrics.GetSnapshot();
    string body = $"slimvector_build_info{{service=\"{observability.Value.ServiceName}\"}} 1\n" +
        $"# TYPE slimvector_open_collections gauge\nslimvector_open_collections {database.OpenCollectionCount}\n" +
        $"# TYPE slimvector_managed_memory_bytes gauge\nslimvector_managed_memory_bytes {memory}\n";
    body += $"slimvector_search_requests_total {operations.Searches}\n" +
        $"slimvector_search_failures_total {operations.SearchFailures}\n" +
        $"slimvector_search_slow_total {operations.SlowSearches}\n" +
        $"slimvector_search_duration_microseconds_total {operations.SearchMicroseconds}\n" +
        $"slimvector_search_mode_requests_total{{mode=\"vector\"}} {operations.VectorSearches}\n" +
        $"slimvector_search_mode_requests_total{{mode=\"text\"}} {operations.TextSearches}\n" +
        $"slimvector_search_mode_requests_total{{mode=\"hybrid\"}} {operations.HybridSearches}\n" +
        $"slimvector_search_mode_requests_total{{mode=\"metadata\"}} {operations.MetadataSearches}\n" +
        $"slimvector_index_loads_total {operations.IndexLoads}\n" +
        $"slimvector_index_load_failures_total {operations.IndexLoadFailures}\n" +
        $"slimvector_index_load_duration_microseconds_total {operations.IndexLoadMicroseconds}\n" +
        $"slimvector_index_documents_loaded_total {operations.IndexedDocumentsLoaded}\n" +
        $"slimvector_index_kind_loads_total{{kind=\"flat\"}} {operations.FlatIndexLoads}\n" +
        $"slimvector_index_kind_loads_total{{kind=\"hnsw\"}} {operations.HnswIndexLoads}\n" +
        $"slimvector_hnsw_cache_hits_total {operations.HnswCacheHits}\n" +
        $"slimvector_hnsw_cache_misses_total {operations.HnswCacheMisses}\n";
    foreach (RaftGroupStatus status in consensus.GetStatuses())
    {
        string labels = $"group=\"{status.GroupId}\",local=\"{status.LocalEndpoint}\"";
        body += $"slimvector_raft_is_leader{{{labels}}} {(status.IsLeader ? 1 : 0)}\n" +
            $"slimvector_raft_term{{{labels}}} {status.Term}\n" +
            $"slimvector_raft_last_applied_index{{{labels}}} {status.LastAppliedIndex}\n" +
            $"slimvector_raft_last_committed_index{{{labels}}} {status.LastCommittedIndex}\n" +
            $"slimvector_raft_applied_commands_total{{{labels}}} {status.AppliedCommandCount}\n";
    }

    WriteSchedulerSnapshot writes = writeScheduler.GetSnapshot();
    body += $"slimvector_write_queue_depth {writes.QueueDepth}\n" +
        $"slimvector_write_requests_total {writes.TotalWrites}\n" +
        $"slimvector_write_completed_total {writes.CompletedWrites}\n" +
        $"slimvector_write_rejected_total {writes.RejectedWrites}\n" +
        $"slimvector_write_batches_total {writes.TotalBatches}\n" +
        $"slimvector_write_batch_items_total {writes.TotalBatchItems}\n";
    foreach (WriteShardSnapshot shard in writes.Shards)
    {
        string labels = $"group=\"{shard.GroupId}\"";
        body += $"slimvector_write_shard_queue_depth{{{labels}}} {shard.QueueDepth}\n" +
            $"slimvector_write_target_batch_size{{{labels}}} {shard.TargetBatchSize}\n" +
            $"slimvector_write_batch_window_milliseconds{{{labels}}} {shard.CurrentWindow.TotalMilliseconds}\n" +
            $"slimvector_write_last_batch_size{{{labels}}} {shard.LastBatchSize}\n" +
            $"slimvector_write_last_batch_bytes{{{labels}}} {shard.LastBatchBytes}\n" +
            $"slimvector_write_last_replication_milliseconds{{{labels}}} {shard.LastReplicationMilliseconds}\n";
    }

    BackupMetricsSnapshot backupMetrics = backups.GetMetrics();
    body += $"slimvector_backups_successful_total {backupMetrics.SuccessfulBackups}\n" +
        $"slimvector_backups_failed_total {backupMetrics.FailedBackups}\n" +
        $"slimvector_restores_successful_total {backupMetrics.SuccessfulRestores}\n" +
        $"slimvector_restores_failed_total {backupMetrics.FailedRestores}\n" +
        $"slimvector_backup_deduplicated_blobs_total {backupMetrics.DeduplicatedBlobs}\n" +
        $"slimvector_backup_last_success_timestamp_seconds {backupMetrics.LastSuccessfulBackupAt?.ToUnixTimeSeconds() ?? 0}\n" +
        $"slimvector_restore_last_success_timestamp_seconds {backupMetrics.LastSuccessfulRestoreAt?.ToUnixTimeSeconds() ?? 0}\n";
    GeoReplicationMetrics outgoingGeo = geoReplication.GetMetrics();
    GeoReplicationMetrics incomingGeo = geoReceiver.GetMetrics();
    body += $"slimvector_geo_pending_events {outgoingGeo.PendingEvents}\n" +
        $"slimvector_geo_sent_events_total {outgoingGeo.SentEvents}\n" +
        $"slimvector_geo_failed_attempts_total {outgoingGeo.FailedAttempts}\n" +
        $"slimvector_geo_received_events_total {incomingGeo.ReceivedEvents}\n" +
        $"slimvector_geo_duplicate_events_total {incomingGeo.DuplicateEvents}\n" +
        $"slimvector_geo_divergence_events_total {incomingGeo.DivergenceEvents}\n" +
        $"slimvector_geo_replication_lag_seconds {outgoingGeo.ReplicationLag.TotalSeconds}\n";

    return Results.Text(body, "text/plain; version=0.0.4");
});
app.MapSlimVectorApi();
app.MapBackupAdminApi();
app.MapGeoReplicationEndpoint();
app.Run();

static Task WriteTimeoutProblemAsync(HttpContext context)
{
    ProblemDetails problem = new()
    {
        Status = StatusCodes.Status504GatewayTimeout,
        Title = "Request timed out",
        Detail = "The request exceeded the configured server timeout.",
        Instance = context.Request.Path,
        Type = "https://slimvector.dev/problems/request_timeout",
    };
    problem.Extensions["code"] = "request_timeout";
    problem.Extensions["traceId"] = context.TraceIdentifier;
    return context.Response.WriteAsJsonAsync(
        problem,
        ApiJsonContext.Default.ProblemDetails,
        contentType: "application/problem+json",
        CancellationToken.None);
}

public partial class Program;
