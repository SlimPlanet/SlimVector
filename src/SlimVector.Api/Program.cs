using System.Text.Json;
using Microsoft.AspNetCore.Http.Timeouts;
using Microsoft.AspNetCore.Mvc;
using SlimVector.Api;
using SlimVector.Api.Contracts;
using SlimVector.Application;
using SlimVector.Application.Admission;
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
app.UseMiddleware<AdmissionMiddleware>();
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
    IClusterMembershipCoordinator membership,
    IWriteScheduler writeScheduler,
    IBackupService backups,
    IGeoReplicationService geoReplication,
    IGeoReplicationReceiver geoReceiver,
    IAdmissionController admission,
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
        $"slimvector_index_kind_loads_total{{kind=\"ivf_flat\"}} {operations.IvfFlatIndexLoads}\n" +
        $"slimvector_index_kind_loads_total{{kind=\"ivf_pq\"}} {operations.IvfPqIndexLoads}\n" +
        $"slimvector_index_kind_loads_total{{kind=\"disk_ann\"}} {operations.DiskAnnIndexLoads}\n" +
        $"slimvector_hnsw_cache_hits_total {operations.HnswCacheHits}\n" +
        $"slimvector_hnsw_cache_misses_total {operations.HnswCacheMisses}\n" +
        $"slimvector_index_migrations_total {operations.IndexMigrations}\n" +
        $"slimvector_index_migration_failures_total {operations.IndexMigrationFailures}\n" +
        $"slimvector_index_migration_duration_microseconds_total {operations.IndexMigrationMicroseconds}\n" +
        $"slimvector_index_migration_last_recall {operations.LastIndexMigrationRecall}\n";
    foreach (SlimVector.Application.Indexes.IndexRuntimeMetrics index in database.GetOpenIndexMetrics())
    {
        string collection = EscapePrometheusLabel(index.Collection);
        string kind = EscapePrometheusLabel(index.ActiveKind.ToString());
        string state = EscapePrometheusLabel(index.MigrationState);
        string labels = $"collection=\"{collection}\",kind=\"{kind}\",state=\"{state}\"";
        body += $"slimvector_index_active{{{labels}}} 1\n" +
            $"slimvector_index_documents{{{labels}}} {index.DocumentCount}\n" +
            $"slimvector_index_estimated_resident_bytes{{{labels}}} {index.EstimatedResidentBytes}\n" +
            $"slimvector_index_persisted_snapshot_bytes{{{labels}}} {index.PersistedSnapshotBytes}\n";
    }

    foreach (RaftGroupStatus status in consensus.GetStatuses())
    {
        string labels = $"group=\"{status.GroupId}\",local=\"{status.LocalEndpoint}\"";
        body += $"slimvector_raft_is_leader{{{labels}}} {(status.IsLeader ? 1 : 0)}\n" +
            $"slimvector_raft_term{{{labels}}} {status.Term}\n" +
            $"slimvector_raft_last_applied_index{{{labels}}} {status.LastAppliedIndex}\n" +
            $"slimvector_raft_last_committed_index{{{labels}}} {status.LastCommittedIndex}\n" +
            $"slimvector_raft_applied_commands_total{{{labels}}} {status.AppliedCommandCount}\n";
    }

    foreach (ClusterMembershipStatus status in membership.GetMembershipStatuses())
    {
        string group = EscapePrometheusLabel(status.GroupId);
        body += $"slimvector_raft_members{{group=\"{group}\"}} {status.Members.Count}\n" +
            $"slimvector_raft_membership_change_in_progress{{group=\"{group}\"}} {(status.ChangeState is null ? 0 : 1)}\n";
        if (status.ChangeState is not null)
        {
            body += $"slimvector_raft_membership_change_info{{group=\"{group}\",state=\"{EscapePrometheusLabel(status.ChangeState)}\",endpoint=\"{EscapePrometheusLabel(status.ChangeEndpoint ?? string.Empty)}\"}} 1\n";
        }

        foreach (RaftMemberStatus member in status.Members)
        {
            string memberLabels = $"group=\"{group}\",endpoint=\"{EscapePrometheusLabel(member.Endpoint)}\",transport=\"{EscapePrometheusLabel(member.TransportStatus)}\"";
            body += $"slimvector_raft_member_info{{{memberLabels},leader=\"{member.IsLeader.ToString().ToLowerInvariant()}\",remote=\"{member.IsRemote.ToString().ToLowerInvariant()}\"}} 1\n";
            if (member.MatchIndex.HasValue)
            {
                body += $"slimvector_raft_member_match_index{{{memberLabels}}} {member.MatchIndex.Value}\n";
            }

            if (member.ReplicationLag.HasValue)
            {
                body += $"slimvector_raft_member_replication_lag{{{memberLabels}}} {member.ReplicationLag.Value}\n";
            }
        }
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
    AdmissionMetricsSnapshot admissionMetrics = admission.GetSnapshot();
    body += $"slimvector_admission_accepted_total {admissionMetrics.Accepted}\n" +
        $"slimvector_admission_contractual_rejections_total {admissionMetrics.ContractualRejections}\n" +
        $"slimvector_admission_congestion_rejections_total {admissionMetrics.CongestionRejections}\n" +
        $"slimvector_admission_adaptive_rate_ratio {admissionMetrics.AdaptiveRateRatio}\n" +
        $"slimvector_admission_pressure {admissionMetrics.Pressure}\n";

    return Results.Text(body, "text/plain; version=0.0.4");
});
app.MapSlimVectorApi();
app.MapBackupAdminApi();
app.MapSlimVectorAdminApi();
app.MapGeoReplicationEndpoint();
app.Run();

static string EscapePrometheusLabel(string value) => value
    .Replace("\\", "\\\\", StringComparison.Ordinal)
    .Replace("\n", "\\n", StringComparison.Ordinal)
    .Replace("\"", "\\\"", StringComparison.Ordinal);

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
