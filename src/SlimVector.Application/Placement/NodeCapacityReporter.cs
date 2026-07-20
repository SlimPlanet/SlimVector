using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using SlimVector.Application.Configuration;
using SlimVector.Raft;
using SlimVector.Storage;

namespace SlimVector.Application.Placement;

public sealed class NodeCapacityReporter(
    IClusterTopologyService topology,
    IDataGroupStorage dataGroups,
    IClusterMembershipCoordinator membership,
    IOptions<RaftOptions> raftOptions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using PeriodicTimer timer = new(TimeSpan.FromSeconds(10));
        do
        {
            IReadOnlyList<string> localGroups = dataGroups.GetLocalDataGroupIds();
            Dictionary<string, long> sizes = localGroups.ToDictionary(
                static groupId => groupId,
                dataGroups.GetAllocatedBytes,
                StringComparer.Ordinal);
            Dictionary<string, ReplicaHealthReport> health = membership.GetMembershipStatuses()
                .Where(status => localGroups.Contains(status.GroupId, StringComparer.Ordinal))
                .Select(status => (status.GroupId, Local: status.Members.FirstOrDefault(static member => !member.IsRemote)))
                .Where(static item => item.Local is not null)
                .ToDictionary(
                    static item => item.GroupId,
                    static item => new ReplicaHealthReport(
                        item.Local!.ReplicationLag,
                        string.Equals(item.Local.TransportStatus, "Available", StringComparison.OrdinalIgnoreCase)),
                    StringComparer.Ordinal);
            await topology.ReportStorageUsageAsync(
                raftOptions.Value.NodeId,
                sizes,
                health,
                stoppingToken).ConfigureAwait(false);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
