namespace SlimVector.Indexing.Tests;

public sealed class DistributedBm25Tests
{
    [Fact]
    public void AggregatedCorpusStatisticsProduceTheSameRankingAsMonolithicBm25()
    {
        Bm25Index left = new();
        Bm25Index right = new();
        Bm25Index monolithic = new();
        (string Id, string Text)[] documents =
        [
            ("a", "distributed vector database database"),
            ("b", "vector search"),
            ("c", "distributed systems search"),
            ("d", "unrelated document"),
        ];
        foreach ((string id, string text) in documents)
        {
            monolithic.Upsert(id, text);
        }

        foreach ((string id, string text) in documents[..2])
        {
            left.Upsert(id, text);
        }

        foreach ((string id, string text) in documents[2..])
        {
            right.Upsert(id, text);
        }

        const string query = "distributed database";
        Bm25CorpusStatistics statistics = Aggregate(
            left.GetCorpusStatistics(query),
            right.GetCorpusStatistics(query));
        RankedResult[] distributed = left.Search(query, 4, corpusStatistics: statistics)
            .Concat(right.Search(query, 4, corpusStatistics: statistics))
            .OrderByDescending(static result => result.Score)
            .ThenBy(static result => result.Id, StringComparer.Ordinal)
            .ToArray();
        IReadOnlyList<RankedResult> expected = monolithic.Search(query, 4);

        Assert.Equal(expected.Select(static result => result.Id), distributed.Select(static result => result.Id));
        for (int index = 0; index < expected.Count; index++)
        {
            Assert.Equal(expected[index].Score, distributed[index].Score, precision: 12);
        }
    }

    private static Bm25CorpusStatistics Aggregate(params Bm25CorpusStatistics[] parts) => new()
    {
        DocumentCount = parts.Sum(static part => part.DocumentCount),
        TotalTerms = parts.Sum(static part => part.TotalTerms),
        Terms = parts.SelectMany(static part => part.Terms)
            .GroupBy(static term => term.Term, StringComparer.Ordinal)
            .Select(static group => new Bm25TermStatistics
            {
                Term = group.Key,
                DocumentFrequency = group.Sum(static term => term.DocumentFrequency),
            }).ToArray(),
    };
}
