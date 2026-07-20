using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using SlimVector.Application.Admission;
using SlimVector.Application.Backups;
using SlimVector.Application.Configuration;
using SlimVector.Application.Placement;
using SlimVector.Application.Routing;
using SlimVector.Application.Writes;
using SlimVector.Domain;
using SlimVector.Raft;
using SlimVector.Replication;
using SlimVector.Storage;

namespace SlimVector.Application;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddSlimVector(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();
        services.AddOptions<StorageOptions>().Bind(configuration.GetSection(StorageOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<CollectionsOptions>, CollectionsOptionsValidator>();
        services.AddOptions<CollectionsOptions>().Bind(configuration.GetSection(CollectionsOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<VectorIndexOptions>, VectorIndexOptionsValidator>();
        services.AddOptions<VectorIndexOptions>().Bind(configuration.GetSection(VectorIndexOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AutoIndexOptions>, AutoIndexOptionsValidator>();
        services.AddOptions<AutoIndexOptions>().Bind(configuration.GetSection(AutoIndexOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<HnswOptions>, HnswOptionsValidator>();
        services.AddOptions<HnswOptions>().Bind(configuration.GetSection(HnswOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<IvfOptions>, IvfOptionsValidator>();
        services.AddOptions<IvfOptions>().Bind(configuration.GetSection(IvfOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<PqOptions>, PqOptionsValidator>();
        services.AddOptions<PqOptions>().Bind(configuration.GetSection(PqOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DiskAnnOptions>, DiskAnnOptionsValidator>();
        services.AddOptions<DiskAnnOptions>().Bind(configuration.GetSection(DiskAnnOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<TextIndexOptions>, TextIndexOptionsValidator>();
        services.AddOptions<TextIndexOptions>().Bind(configuration.GetSection(TextIndexOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<MetadataIndexOptions>, MetadataIndexOptionsValidator>();
        services.AddOptions<MetadataIndexOptions>().Bind(configuration.GetSection(MetadataIndexOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<RaftOptions>, RaftOptionsValidator>();
        services.AddOptions<RaftOptions>().Bind(configuration.GetSection(RaftOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ClusterMembershipOptions>, ClusterMembershipOptionsValidator>();
        services.AddOptions<ClusterMembershipOptions>().Bind(configuration.GetSection(ClusterMembershipOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<RebalancingOptions>, RebalancingOptionsValidator>();
        services.AddOptions<RebalancingOptions>().Bind(configuration.GetSection(RebalancingOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<DataPlacementOptions>, DataPlacementOptionsValidator>();
        services.AddOptions<DataPlacementOptions>().Bind(configuration.GetSection(DataPlacementOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<GeoReplicationOptions>, GeoReplicationOptionsValidator>();
        services.AddOptions<GeoReplicationOptions>().Bind(configuration.GetSection(GeoReplicationOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<AdaptiveBatchingOptions>, AdaptiveBatchingOptionsValidator>();
        services.AddOptions<AdaptiveBatchingOptions>().Bind(configuration.GetSection(AdaptiveBatchingOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BackpressureOptions>, BackpressureOptionsValidator>();
        services.AddOptions<BackpressureOptions>().Bind(configuration.GetSection(BackpressureOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<RateLimitOptions>, RateLimitOptionsValidator>();
        services.AddOptions<RateLimitOptions>().Bind(configuration.GetSection(RateLimitOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<BackupOptions>, BackupOptionsValidator>();
        services.AddOptions<BackupOptions>().Bind(configuration.GetSection(BackupOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ApiOptions>, ApiOptionsValidator>();
        services.AddOptions<ApiOptions>().Bind(configuration.GetSection(ApiOptions.SectionName)).ValidateOnStart();
        services.AddSingleton<IValidateOptions<ObservabilityOptions>, ObservabilityOptionsValidator>();
        services.AddOptions<ObservabilityOptions>().Bind(configuration.GetSection(ObservabilityOptions.SectionName)).ValidateOnStart();

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<OperationalMetrics>();
        services.AddSingleton<StorageMetrics>();
        services.AddSingleton<IStorageEngine>(provider =>
        {
            StorageOptions options = provider.GetRequiredService<IOptions<StorageOptions>>().Value;
            return new FileSystemStorageEngine(
                new StorageSettings
                {
                    Path = options.Path,
                    FlushToDisk = options.FlushToDisk,
                    MaximumSegmentsBeforeCompaction = options.MaximumSegmentsBeforeCompaction,
                },
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<StorageMetrics>());
        });
        services.AddSingleton<IDataGroupStorage>(provider =>
        {
            StorageOptions options = provider.GetRequiredService<IOptions<StorageOptions>>().Value;
            return new FileSystemDataGroupStorage(
                new StorageSettings
                {
                    Path = options.Path,
                    FlushToDisk = options.FlushToDisk,
                    MaximumSegmentsBeforeCompaction = options.MaximumSegmentsBeforeCompaction,
                },
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<StorageMetrics>());
        });
        services.AddSingleton<IClusterTopologyStore>(provider =>
        {
            StorageOptions options = provider.GetRequiredService<IOptions<StorageOptions>>().Value;
            return new FileSystemClusterTopologyStore(
                new StorageSettings
                {
                    Path = options.Path,
                    FlushToDisk = options.FlushToDisk,
                    MaximumSegmentsBeforeCompaction = options.MaximumSegmentsBeforeCompaction,
                },
                provider.GetRequiredService<StorageMetrics>());
        });
        services.AddSingleton(provider =>
        {
            RaftOptions raftOptions = provider.GetRequiredService<IOptions<RaftOptions>>().Value;
            string[] dataGroupIds = Enumerable.Range(0, raftOptions.DataGroupCount)
                .Select(static index => $"data-{index}")
                .ToArray();
            return new StorageRaftCommandApplier(
                provider.GetRequiredService<IStorageEngine>(),
                dataGroupIds,
                provider.GetRequiredService<IDataGroupStorage>(),
                provider.GetRequiredService<IClusterTopologyStore>());
        });
        services.AddSingleton(provider => new ConsensusCoordinatorHolder(CreateConsensusCoordinator(
            provider.GetRequiredService<IOptions<RaftOptions>>().Value,
            provider.GetRequiredService<IOptions<ClusterMembershipOptions>>().Value,
            provider.GetRequiredService<IOptions<DataPlacementOptions>>().Value,
            provider.GetRequiredService<IOptions<StorageOptions>>().Value,
            provider.GetRequiredService<StorageRaftCommandApplier>())));
        services.AddSingleton<IClusterMembershipCoordinator>(provider =>
            provider.GetRequiredService<ConsensusCoordinatorHolder>().Local as IClusterMembershipCoordinator ??
            new SingleNodeMembershipCoordinator());
        services.AddSingleton<ILocalRaftGroupManager>(provider =>
            provider.GetRequiredService<ConsensusCoordinatorHolder>().Local as ILocalRaftGroupManager ??
            new SingleNodeLocalRaftGroupManager());
        services.AddSingleton<ILocalRaftCommandReplicator>(provider =>
            provider.GetRequiredService<ConsensusCoordinatorHolder>().Local as ILocalRaftCommandReplicator ??
            throw new InvalidOperationException("The local consensus coordinator cannot replicate internal Raft commands."));
        services.AddSingleton<DataNodeRpcMetrics>();
        services.AddHttpClient("SlimVector.DataNodeRpc");
        services.AddSingleton<IDataNodeRpcClient>(provider => new DataNodeRpcClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("SlimVector.DataNodeRpc"),
            provider.GetRequiredService<IClusterTopologyStore>(),
            provider.GetRequiredService<IOptions<RaftOptions>>(),
            provider.GetRequiredService<IOptions<ApiOptions>>(),
            provider.GetRequiredService<DataNodeRpcMetrics>()));
        services.AddSingleton<IDataNodeRpcReceiver, DataNodeRpcReceiver>();
        services.AddSingleton<IDataNodeQueryClient>(provider => new DataNodeQueryClient(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("SlimVector.DataNodeRpc"),
            provider.GetRequiredService<IClusterTopologyStore>(),
            provider.GetRequiredService<IOptions<RaftOptions>>(),
            provider.GetRequiredService<IOptions<ApiOptions>>(),
            provider.GetRequiredService<DataNodeRpcMetrics>()));
        services.AddSingleton<ILocalCatalogSnapshotExchange>(provider => new LocalCatalogSnapshotExchange(
            provider.GetRequiredService<ConsensusCoordinatorHolder>().Local,
            provider.GetRequiredService<StorageRaftCommandApplier>(),
            provider.GetRequiredService<IOptions<ApiOptions>>()));
        services.AddHttpClient("SlimVector.CatalogCache");
        services.AddSingleton<CatalogCacheSynchronizer>(provider => new CatalogCacheSynchronizer(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("SlimVector.CatalogCache"),
            provider.GetRequiredService<IClusterTopologyStore>(),
            provider.GetRequiredService<ILocalCatalogSnapshotExchange>(),
            provider.GetRequiredService<IOptions<RaftOptions>>(),
            provider.GetRequiredService<IOptions<RebalancingOptions>>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<CatalogCacheSynchronizer>>()));
        services.AddSingleton<ICatalogCacheSynchronizer>(provider =>
            provider.GetRequiredService<CatalogCacheSynchronizer>());
        services.AddSingleton(provider => new SharedNothingConsensusCoordinator(
            provider.GetRequiredService<ConsensusCoordinatorHolder>().Local,
            provider.GetRequiredService<ILocalRaftGroupManager>(),
            provider.GetRequiredService<IClusterTopologyStore>(),
            provider.GetRequiredService<IDataNodeRpcClient>(),
            provider.GetRequiredService<ILocalRaftCommandReplicator>(),
            provider.GetRequiredService<ICatalogCacheSynchronizer>(),
            provider.GetRequiredService<IOptions<RaftOptions>>()));
        services.AddHttpClient("SlimVector.GeoReplication");
        services.AddSingleton<IGeoReplicationService>(provider => new GeoReplicationService(
            CreateGeoReplicationSettings(provider.GetRequiredService<IOptions<GeoReplicationOptions>>().Value),
            provider.GetRequiredService<IHttpClientFactory>().CreateClient("SlimVector.GeoReplication"),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IGeoReplicationReceiver>(provider => new GeoReplicationReceiver(
            CreateGeoReplicationSettings(provider.GetRequiredService<IOptions<GeoReplicationOptions>>().Value),
            provider.GetRequiredService<ConsensusCoordinatorHolder>().Local));
        services.AddSingleton<IConsensusCoordinator>(provider => new GeoReplicatingConsensusCoordinator(
            provider.GetRequiredService<SharedNothingConsensusCoordinator>(),
            provider.GetRequiredService<IGeoReplicationService>()));
        services.AddSingleton<IWriteScheduler, AdaptiveWriteScheduler>();
        services.AddSingleton<IPlacementController, PlacementController>();
        services.AddSingleton<IClusterTopologyService, ClusterTopologyService>();
        services.AddSingleton<ISharedNothingPlacementPlanner, SharedNothingPlacementPlanner>();
        services.AddHttpClient("SlimVector.ClusterAdministration");
        services.AddSingleton<SharedNothingRebalanceCoordinator>();
        services.AddSingleton<ISharedNothingRebalanceCoordinator>(provider =>
            provider.GetRequiredService<SharedNothingRebalanceCoordinator>());
        services.AddSingleton<IAdmissionController, AdaptiveAdmissionController>();
        services.AddHttpClient("SlimVector.Backup.S3");
        services.AddSingleton<IBackupService, BackupService>();
        services.AddSingleton<SlimVectorDatabase>();
        services.AddSingleton<ISlimVectorDatabase>(provider => provider.GetRequiredService<SlimVectorDatabase>());
        services.AddSingleton<ILocalDataQueryService>(provider => provider.GetRequiredService<SlimVectorDatabase>());
        services.AddSingleton<IDataNodeQueryReceiver, DataNodeQueryReceiver>();
        services.AddSingleton<LocalDataGroupReconciler>();
        services.AddSingleton<ILocalDataGroupProvisioner>(provider =>
            provider.GetRequiredService<LocalDataGroupReconciler>());
        services.AddHostedService<SlimVectorHostedService>();
        services.AddHostedService<ClusterTopologyBootstrapService>();
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider =>
            provider.GetRequiredService<CatalogCacheSynchronizer>());
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider =>
            provider.GetRequiredService<LocalDataGroupReconciler>());
        services.AddSingleton<Microsoft.Extensions.Hosting.IHostedService>(provider =>
            provider.GetRequiredService<SharedNothingRebalanceCoordinator>());
        services.AddHostedService<NodeCapacityReporter>();
        services.AddHostedService<ClusterFailureDetector>();
        services.AddHostedService<BackupHostedService>();
        services.AddHostedService<PlacementControllerHostedService>();
        return services;
    }

    private static IConsensusCoordinator CreateConsensusCoordinator(
        RaftOptions options,
        ClusterMembershipOptions membershipOptions,
        DataPlacementOptions placementOptions,
        StorageOptions storageOptions,
        StorageRaftCommandApplier applier)
    {
        if (options.Mode == ExecutionMode.SingleNode)
        {
            return new DirectConsensusCoordinator(applier);
        }

        if (!RaftOptionsValidator.TryParseEndpoint(options.PublicEndpoint, out IPEndPoint? localBaseEndpoint))
        {
            throw new OptionsValidationException(
                RaftOptions.SectionName,
                typeof(RaftOptions),
                ["Raft:PublicEndpoint is invalid."]);
        }

        IPEndPoint[] memberBaseEndpoints = options.Members.Select(member =>
        {
            if (!RaftOptionsValidator.TryParseEndpoint(member, out IPEndPoint? endpoint))
            {
                throw new OptionsValidationException(
                    RaftOptions.SectionName,
                    typeof(RaftOptions),
                    [$"Raft member endpoint '{member}' is invalid."]);
            }

            return endpoint!;
        }).ToArray();
        string raftStoragePath = Path.Combine(Path.GetFullPath(storageOptions.Path), "raft", options.NodeId);
        List<(string GroupId, int PortOffset, IPEndPoint[] Members)> groups =
        [
            (MultiRaftNode.CatalogGroupId, 0, memberBaseEndpoints.Take(3).ToArray()),
        ];
        if (options.JoinExistingCluster)
        {
            groups.AddRange(Enumerable.Range(0, options.DataGroupCount)
                .Select(index => ($"data-{index}", index + 1, Array.Empty<IPEndPoint>())));
        }
        else
        {
            int localMemberIndex = Array.FindIndex(memberBaseEndpoints, endpoint => endpoint.Equals(localBaseEndpoint));
            int replicationFactor = Math.Min(placementOptions.ReplicationFactor, memberBaseEndpoints.Length);
            for (int groupIndex = 0; groupIndex < options.DataGroupCount; groupIndex++)
            {
                int[] replicaIndexes = Enumerable.Range(0, replicationFactor)
                    .Select(offset => ((groupIndex * replicationFactor) + offset) % memberBaseEndpoints.Length)
                    .Distinct()
                    .ToArray();
                if (replicaIndexes.Contains(localMemberIndex))
                {
                    groups.Add((
                        $"data-{groupIndex}",
                        groupIndex + 1,
                        replicaIndexes.Select(index => memberBaseEndpoints[index]).ToArray()));
                }
            }
        }

        int upperElectionTimeout = checked((int)Math.Ceiling(options.ElectionTimeout.TotalMilliseconds));
        int lowerElectionTimeout = Math.Max(50, upperElectionTimeout / 2);
        RaftGroupNodeOptions[] groupOptions = groups.Select(group => new RaftGroupNodeOptions
        {
            GroupId = group.GroupId,
            LocalEndpoint = OffsetPort(localBaseEndpoint!, group.PortOffset),
            Members = group.Members.Select(endpoint => OffsetPort(endpoint, group.PortOffset)).ToArray(),
            StoragePath = raftStoragePath,
            LowerElectionTimeoutMilliseconds = lowerElectionTimeout,
            UpperElectionTimeoutMilliseconds = upperElectionTimeout,
            HeartbeatThreshold = options.HeartbeatInterval.TotalMilliseconds / lowerElectionTimeout,
            RequestTimeout = options.ElectionTimeout * 2,
            SnapshotEveryEntries = options.SnapshotEveryEntries,
            TransmissionBlockSize = options.TransmissionBlockSize,
            WarmupRounds = membershipOptions.WarmupRounds,
            MaximumReplicationLag = membershipOptions.MaximumCatchUpLagEntries,
            StartAsJoiningMember = options.JoinExistingCluster,
        }).ToArray();
        MultiRaftNode node = new(groupOptions, _ => applier);
        return new DistributedConsensusCoordinator(node, applier);
    }

    private static IPEndPoint OffsetPort(IPEndPoint endpoint, int offset) =>
        new(endpoint.Address, checked(endpoint.Port + offset));

    private static GeoReplicationSettings CreateGeoReplicationSettings(GeoReplicationOptions options) => new()
    {
        Enabled = options.Enabled,
        AcceptIncoming = options.AcceptIncoming,
        Mode = options.Mode,
        OriginId = options.OriginId,
        SecondaryEndpoint = options.SecondaryEndpoint is null ? null : new Uri(options.SecondaryEndpoint, UriKind.Absolute),
        SharedSecret = options.SharedSecret,
        StatePath = options.StatePath,
        QueueCapacity = options.QueueCapacity,
        RetryMinimumDelay = options.RetryMinimumDelay,
        RetryMaximumDelay = options.RetryMaximumDelay,
        RequestTimeout = options.RequestTimeout,
    };
}
