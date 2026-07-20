namespace SlimVector.Indexing;

public enum IvfTrainingState
{
    Untrained,
    Collecting,
    Active,
    NeedsRetrain,
}
