namespace SlimVector.Indexing;

internal interface IBulkVectorIndex
{
    void Build(IReadOnlyList<(string Id, float[] Vector)> vectors);
}
