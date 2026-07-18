using SlimVector.Raft;

namespace SlimVector.Application;

internal sealed class ConsensusCoordinatorHolder(IConsensusCoordinator local)
{
    public IConsensusCoordinator Local { get; } = local;
}
