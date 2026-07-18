namespace SlimVector.Indexing;

public interface IVectorIndex
{
    int Count { get; }

    void Upsert(string id, ReadOnlySpan<float> vector);

    bool Remove(string id);

    IReadOnlyList<RankedResult> Search(ReadOnlySpan<float> query, int limit, IReadOnlySet<string>? candidates = null);
}
