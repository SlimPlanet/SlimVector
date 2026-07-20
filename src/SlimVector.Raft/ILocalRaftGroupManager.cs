namespace SlimVector.Raft;

public interface ILocalRaftGroupManager
{
    IReadOnlyList<string> GetHostedDataGroupIds();

    ValueTask AddLocalDataGroupAsync(
        RaftGroupNodeOptions options,
        CancellationToken cancellationToken = default);

    ValueTask RemoveLocalDataGroupAsync(
        string groupId,
        CancellationToken cancellationToken = default);
}
