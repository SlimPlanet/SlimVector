using SlimVector.Domain;

namespace SlimVector.Indexing.Tests;

public sealed class Bm25IndexTests
{
    [Fact]
    public void SearchRanksTermFrequencyAndRemovesOldTermsOnUpdate()
    {
        Bm25Index index = new();
        index.Upsert("one", "vector database vector search");
        index.Upsert("two", "database only");

        Assert.Equal("one", index.Search("vector", 2)[0].Id);

        index.Upsert("one", "unrelated content");
        Assert.Empty(index.Search("vector", 2));
    }

    [Fact]
    public void ConfiguredTermLimitRejectsOversizedText()
    {
        Bm25Index index = new(k1: 1.4, b: 0.6, maximumTermsPerDocument: 2);

        DomainException exception = Assert.Throws<DomainException>(() =>
            index.Upsert("one", "one two three"));

        Assert.Equal(ErrorCodes.TextTooLarge, exception.Code);
        Assert.Equal(0, index.Count);

        index.Upsert("one", "one two");
        _ = Assert.Throws<DomainException>(() => index.Upsert("one", "replacement has three terms"));
        Assert.Equal("one", Assert.Single(index.Search("one", 1)).Id);
    }
}
