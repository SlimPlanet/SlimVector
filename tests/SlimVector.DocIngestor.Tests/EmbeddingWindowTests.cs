using SlimVector.DocIngestor.Embeddings;

namespace SlimVector.DocIngestor.Tests;

public sealed class EmbeddingWindowTests
{
    [Fact]
    public void LongTokenSequencesAreWindowedWithoutDroppingContent()
    {
        uint[] ids = [101, .. Enumerable.Range(1, 300).Select(static value => (uint)value), 102];
        uint[] typeIds = new uint[ids.Length];

        IReadOnlyList<OnnxSentenceEmbeddingGenerator.EncodedTokenWindow> windows =
            OnnxSentenceEmbeddingGenerator.CreateTokenWindows(ids, typeIds, maximumSequenceLength: 128);

        Assert.Equal([126, 126, 48], windows.Select(static window => window.ContentTokenCount));
        Assert.All(windows, static window => Assert.InRange(window.Ids.Length, 3, 128));
        Assert.All(windows, static window => Assert.Equal(101u, window.Ids[0]));
        Assert.All(windows, static window => Assert.Equal(102u, window.Ids[^1]));
        Assert.Equal(
            ids.Skip(1).SkipLast(1),
            windows.SelectMany(static window => window.Ids.Skip(1).SkipLast(1)));
    }

    [Fact]
    public void ShortTokenSequencesRemainInOneWindow()
    {
        uint[] ids = [101, 10, 11, 12, 102];
        uint[] typeIds = new uint[ids.Length];

        OnnxSentenceEmbeddingGenerator.EncodedTokenWindow window = Assert.Single(
            OnnxSentenceEmbeddingGenerator.CreateTokenWindows(ids, typeIds, maximumSequenceLength: 128));

        Assert.Same(ids, window.Ids);
        Assert.Same(typeIds, window.TypeIds);
        Assert.Equal(3, window.ContentTokenCount);
    }
}
