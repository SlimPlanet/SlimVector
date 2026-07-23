using MemoryPack;
using SlimVector.Domain;

namespace SlimVector.Indexing;

public sealed class Bm25Index
{
    private readonly double _k1;
    private readonly double _b;
    private readonly int _maximumTermsPerDocument;
    private readonly Dictionary<string, Dictionary<string, int>> _postings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, int>> _documentTerms = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _documentLengths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _canonicalTerms = new(StringComparer.Ordinal);
    private long _totalTerms;

    public Bm25Index(double k1 = 1.2, double b = 0.75, int maximumTermsPerDocument = 100_000)
    {
        if (k1 <= 0 || b is < 0 or > 1 || maximumTermsPerDocument < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(k1), "BM25 settings are invalid.");
        }

        _k1 = k1;
        _b = b;
        _maximumTermsPerDocument = maximumTermsPerDocument;
    }

    public int Count => _documentLengths.Count;

    internal void EnsureCapacity(int documentCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(documentCount);
        _documentTerms.EnsureCapacity(documentCount);
        _documentLengths.EnsureCapacity(documentCount);
    }

    public void Upsert(string id, string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        ArgumentNullException.ThrowIfNull(text);

        Dictionary<string, int> countedTerms = TextTokenizer.CountTerms(text);
        int length = countedTerms.Values.Sum();
        if (length > _maximumTermsPerDocument)
        {
            throw new DomainException(
                ErrorCodes.TextTooLarge,
                $"Document text exceeds the configured limit of {_maximumTermsPerDocument} terms.");
        }

        Remove(id);
        Dictionary<string, int> terms = new(countedTerms.Count, StringComparer.Ordinal);
        foreach ((string term, int frequency) in countedTerms)
        {
            if (!_canonicalTerms.TryGetValue(term, out string? canonical))
            {
                canonical = term;
                _canonicalTerms.Add(canonical, canonical);
            }

            terms.Add(canonical, frequency);
        }

        _documentTerms[id] = terms;
        _documentLengths[id] = length;
        _totalTerms += length;

        foreach ((string term, int frequency) in terms)
        {
            if (!_postings.TryGetValue(term, out Dictionary<string, int>? posting))
            {
                posting = new Dictionary<string, int>(StringComparer.Ordinal);
                _postings.Add(term, posting);
            }

            posting[id] = frequency;
        }
    }

    public bool Remove(string id)
    {
        if (!_documentTerms.Remove(id, out Dictionary<string, int>? terms))
        {
            return false;
        }

        _totalTerms -= _documentLengths[id];
        _documentLengths.Remove(id);
        foreach (string term in terms.Keys)
        {
            Dictionary<string, int> posting = _postings[term];
            posting.Remove(id);
            if (posting.Count == 0)
            {
                _postings.Remove(term);
                _canonicalTerms.Remove(term);
            }
        }

        return true;
    }

    public IReadOnlyList<RankedResult> Search(
        string query,
        int limit,
        IReadOnlySet<string>? candidates = null,
        Bm25CorpusStatistics? corpusStatistics = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);

        string[] queryTerms = TextTokenizer.Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();
        if (queryTerms.Length == 0 || _documentLengths.Count == 0)
        {
            return [];
        }

        long corpusDocumentCount = corpusStatistics?.DocumentCount ?? _documentLengths.Count;
        long corpusTotalTerms = corpusStatistics?.TotalTerms ?? _totalTerms;
        double averageLength = (double)corpusTotalTerms / Math.Max(corpusDocumentCount, 1);
        Dictionary<string, long>? globalDocumentFrequencies = corpusStatistics?.Terms.ToDictionary(
            static term => term.Term,
            static term => term.DocumentFrequency,
            StringComparer.Ordinal);
        Dictionary<string, double> scores = new(StringComparer.Ordinal);
        foreach (string term in queryTerms)
        {
            if (!_postings.TryGetValue(term, out Dictionary<string, int>? posting))
            {
                continue;
            }

            long documentFrequency = globalDocumentFrequencies?.GetValueOrDefault(term) ?? posting.Count;
            double inverseDocumentFrequency = Math.Log(
                1 + ((corpusDocumentCount - documentFrequency + 0.5) / (documentFrequency + 0.5)));
            foreach ((string id, int termFrequency) in posting)
            {
                if (candidates is not null && !candidates.Contains(id))
                {
                    continue;
                }

                int documentLength = _documentLengths[id];
                double denominator = termFrequency + _k1 * (1 - _b + (_b * documentLength / Math.Max(averageLength, 1)));
                double contribution = inverseDocumentFrequency * ((termFrequency * (_k1 + 1)) / denominator);
                scores.TryGetValue(id, out double score);
                scores[id] = score + contribution;
            }
        }

        return scores
            .Select(static pair => new RankedResult(pair.Key, pair.Value))
            .OrderByDescending(static result => result.Score)
            .ThenBy(static result => result.Id, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    public Bm25CorpusStatistics GetCorpusStatistics(string query, IReadOnlySet<string>? candidates = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        IEnumerable<KeyValuePair<string, int>> lengths = candidates is null
            ? _documentLengths
            : _documentLengths.Where(pair => candidates.Contains(pair.Key));
        KeyValuePair<string, int>[] included = lengths.ToArray();
        string[] terms = TextTokenizer.Tokenize(query).Distinct(StringComparer.Ordinal).ToArray();
        return new Bm25CorpusStatistics
        {
            DocumentCount = included.LongLength,
            TotalTerms = included.Sum(static pair => (long)pair.Value),
            Terms = terms.Select(term => new Bm25TermStatistics
            {
                Term = term,
                DocumentFrequency = _postings.TryGetValue(term, out Dictionary<string, int>? posting)
                    ? candidates is null
                        ? posting.Count
                        : posting.Keys.LongCount(candidates.Contains)
                    : 0,
            }).ToArray(),
        };
    }

    internal byte[] Serialize()
    {
        Bm25Snapshot snapshot = new()
        {
            K1 = _k1,
            B = _b,
            MaximumTermsPerDocument = _maximumTermsPerDocument,
            Documents = _documentTerms
                .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => new Bm25DocumentSnapshot
                {
                    Id = pair.Key,
                    Terms = pair.Value
                        .OrderBy(static term => term.Key, StringComparer.Ordinal)
                        .Select(static term => new Bm25TermSnapshot { Term = term.Key, Frequency = term.Value })
                        .ToArray(),
                })
                .ToArray(),
        };
        return MemoryPackSerializer.Serialize(snapshot);
    }

    internal static Bm25Index? Deserialize(
        ReadOnlySpan<byte> data,
        double expectedK1,
        double expectedB,
        int expectedMaximumTermsPerDocument)
    {
        Bm25Snapshot? snapshot;
        try
        {
            snapshot = MemoryPackSerializer.Deserialize<Bm25Snapshot>(data);
        }
        catch (MemoryPackSerializationException)
        {
            return null;
        }

        if (snapshot is null || snapshot.FormatVersion != 1 || snapshot.Documents is null ||
            snapshot.K1 != expectedK1 || snapshot.B != expectedB ||
            snapshot.MaximumTermsPerDocument != expectedMaximumTermsPerDocument ||
            snapshot.Documents.Select(static document => document.Id).Distinct(StringComparer.Ordinal).Count() != snapshot.Documents.Length ||
            snapshot.Documents.Any(static document =>
                document is null || string.IsNullOrWhiteSpace(document.Id) || document.Terms is null ||
                document.Terms.Any(static term => term is null || string.IsNullOrEmpty(term.Term) || term.Frequency < 1) ||
                document.Terms.Select(static term => term.Term).Distinct(StringComparer.Ordinal).Count() != document.Terms.Length))
        {
            return null;
        }

        Bm25Index index = new(snapshot.K1, snapshot.B, snapshot.MaximumTermsPerDocument);
        index.EnsureCapacity(snapshot.Documents.Length);
        foreach (Bm25DocumentSnapshot document in snapshot.Documents)
        {
            Dictionary<string, int> terms = new(document.Terms.Length, StringComparer.Ordinal);
            foreach (Bm25TermSnapshot term in document.Terms)
            {
                if (!index._canonicalTerms.TryGetValue(term.Term, out string? canonical))
                {
                    canonical = term.Term;
                    index._canonicalTerms.Add(canonical, canonical);
                }

                terms.Add(canonical, term.Frequency);
            }

            int length = terms.Values.Sum();
            index._documentTerms.Add(document.Id, terms);
            index._documentLengths.Add(document.Id, length);
            index._totalTerms += length;
            foreach ((string term, int frequency) in terms)
            {
                if (!index._postings.TryGetValue(term, out Dictionary<string, int>? posting))
                {
                    posting = new Dictionary<string, int>(StringComparer.Ordinal);
                    index._postings.Add(term, posting);
                }

                posting.Add(document.Id, frequency);
            }
        }

        return index;
    }

    internal void ValidateText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int terms = 0;
        foreach (string _ in TextTokenizer.Tokenize(text))
        {
            terms++;
            if (terms > _maximumTermsPerDocument)
            {
                throw new DomainException(
                    ErrorCodes.TextTooLarge,
                    $"Document text exceeds the configured limit of {_maximumTermsPerDocument} terms.");
            }
        }
    }
}

[MemoryPackable]
public sealed partial class Bm25CorpusStatistics
{
    public long DocumentCount { get; set; }

    public long TotalTerms { get; set; }

    public Bm25TermStatistics[] Terms { get; set; } = [];
}

[MemoryPackable]
public sealed partial class Bm25TermStatistics
{
    public string Term { get; set; } = string.Empty;

    public long DocumentFrequency { get; set; }
}

[MemoryPackable]
internal sealed partial class Bm25Snapshot
{
    public int FormatVersion { get; set; } = 1;

    public double K1 { get; set; }

    public double B { get; set; }

    public int MaximumTermsPerDocument { get; set; }

    public Bm25DocumentSnapshot[] Documents { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class Bm25DocumentSnapshot
{
    public string Id { get; set; } = string.Empty;

    public Bm25TermSnapshot[] Terms { get; set; } = [];
}

[MemoryPackable]
internal sealed partial class Bm25TermSnapshot
{
    public string Term { get; set; } = string.Empty;

    public int Frequency { get; set; }
}
