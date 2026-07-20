using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Domain;
using SlimVector.Raft;

namespace SlimVector.Application.Placement;

public sealed partial class ClusterFailureDetector(
    IClusterTopologyService topologyService,
    ISharedNothingRebalanceCoordinator rebalanceCoordinator,
    IConsensusCoordinator consensus,
    IOptions<RaftOptions> raftOptions,
    IOptions<DataPlacementOptions> placementOptions,
    TimeProvider timeProvider,
    ILogger<ClusterFailureDetector> logger) : BackgroundService
{
    private readonly TimeSpan _interval = TimeSpan.FromSeconds(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (raftOptions.Value.Mode == ExecutionMode.SingleNode)
        {
            return;
        }

        using PeriodicTimer timer = new(_interval);
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                await DetectOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception) when (exception is DomainException or ConsensusUnavailableException or InvalidOperationException)
            {
                LogDetectionFailure(logger, exception.Message);
            }
        }
    }

    internal async ValueTask DetectOnceAsync(CancellationToken cancellationToken)
    {
        if (!consensus.GetStatuses().Any(static status =>
                string.Equals(status.GroupId, MultiRaftNode.CatalogGroupId, StringComparison.Ordinal) && status.IsLeader))
        {
            return;
        }

        ClusterTopology topology = await topologyService.GetAsync(cancellationToken).ConfigureAwait(false);
        DateTimeOffset cutoff = timeProvider.GetUtcNow() - placementOptions.Value.FailureReplacementDelay;
        ClusterNodeDescriptor[] failed = topology.Nodes
            .Where(node => node.State == ClusterNodeState.Active && node.LastSeenAt < cutoff &&
                !string.Equals(node.NodeId, raftOptions.Value.NodeId, StringComparison.Ordinal))
            .OrderBy(static node => node.LastSeenAt)
            .ToArray();
        foreach (ClusterNodeDescriptor node in failed)
        {
            ClusterTopology unavailable = await topologyService.MarkUnavailableAsync(node.NodeId, cancellationToken)
                .ConfigureAwait(false);
            int activeDataNodes = unavailable.Nodes.Count(static candidate => candidate.State == ClusterNodeState.Active &&
                candidate.Roles.Contains("data", StringComparer.OrdinalIgnoreCase));
            if (activeDataNodes < placementOptions.Value.ReplicationFactor)
            {
                LogInsufficientReplacementNodes(logger, node.NodeId, activeDataNodes);
                continue;
            }

            SharedNothingRebalancePlan plan = await rebalanceCoordinator.PlanAsync(node.NodeId, cancellationToken)
                .ConfigureAwait(false);
            if (plan.ReplicaMoves.Count > 0)
            {
                _ = await rebalanceCoordinator.ApproveAsync(plan.PlanId, cancellationToken).ConfigureAwait(false);
                LogReplacementPlanned(logger, node.NodeId, plan.ReplicaMoves.Count);
            }
        }
    }

    [LoggerMessage(EventId = 7501, Level = LogLevel.Warning, Message = "Cluster failure detection pass failed: {Message}")]
    private static partial void LogDetectionFailure(ILogger logger, string message);

    [LoggerMessage(EventId = 7502, Level = LogLevel.Error, Message = "Node {NodeId} is unavailable, but only {ActiveDataNodes} active data nodes remain; RF recovery is waiting for capacity")]
    private static partial void LogInsufficientReplacementNodes(ILogger logger, string nodeId, int activeDataNodes);

    [LoggerMessage(EventId = 7503, Level = LogLevel.Warning, Message = "Node {NodeId} is unavailable; approved {MoveCount} automatic replica replacements")]
    private static partial void LogReplacementPlanned(ILogger logger, string nodeId, int moveCount);
}
