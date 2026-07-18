using System.Text;
using SlimVector.DocIngestor.Abstractions;
using SlimVector.DocIngestor.Models;

namespace SlimVector.DocIngestor.Chunking;

public sealed class RecursiveTextChunker : ITextChunker
{
    private static readonly string[] RecursiveSeparators = ["\n\n", "\n", ". ", "! ", "? ", "; ", ", ", " "];
    private static readonly string[] ParagraphSeparators = ["\n\n", "\n", " "];
    private static readonly string[] SentenceSeparators = [". ", "! ", "? ", "; ", "\n", " "];
    private readonly ITokenCounter _tokenCounter;

    public RecursiveTextChunker(ITokenCounter tokenCounter)
    {
        _tokenCounter = tokenCounter ?? throw new ArgumentNullException(nameof(tokenCounter));
    }

    public IReadOnlyList<TextChunk> Chunk(ExtractedDocument document, ChunkingOptions options)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();

        string[] separators = options.Strategy switch
        {
            ChunkingStrategy.Paragraph => ParagraphSeparators,
            ChunkingStrategy.Sentence => SentenceSeparators,
            _ => RecursiveSeparators,
        };

        List<AtomicUnit> units = [];
        foreach (ExtractedSection section in document.Sections)
        {
            string text = section.Text.Trim();
            if (text.Length == 0)
            {
                continue;
            }

            foreach (string piece in SplitRecursively(text, separators, 0, options.MaximumTokens))
            {
                int tokens = _tokenCounter.CountTokens(piece);
                if (tokens > 0)
                {
                    units.Add(new AtomicUnit(piece, tokens, section.Sequence, section.Location, section.Heading));
                }
            }
        }

        List<TextChunk> chunks = [];
        List<AtomicUnit> current = [];
        int currentTokens = 0;
        foreach (AtomicUnit unit in units)
        {
            bool wouldOverflow = current.Count > 0 && currentTokens + unit.Tokens > options.MaximumTokens;
            bool targetReached = current.Count > 0 && currentTokens >= options.TargetTokens;
            if (wouldOverflow || targetReached)
            {
                Emit(chunks, current);
                AtomicUnit? overlap = CreateOverlap(current, options.OverlapTokens);
                current.Clear();
                currentTokens = 0;
                if (overlap is not null && overlap.Tokens + unit.Tokens <= options.MaximumTokens)
                {
                    current.Add(overlap);
                    currentTokens = overlap.Tokens;
                }
            }

            current.Add(unit);
            currentTokens += unit.Tokens;
        }

        if (current.Count > 0)
        {
            Emit(chunks, current);
        }

        MergeSmallTail(chunks, options);
        return chunks;
    }

    private IEnumerable<string> SplitRecursively(string text, string[] separators, int separatorIndex, int maximumTokens)
    {
        if (_tokenCounter.CountTokens(text) <= maximumTokens)
        {
            yield return text.Trim();
            yield break;
        }

        if (separatorIndex >= separators.Length)
        {
            foreach (string piece in HardSplit(text, maximumTokens))
            {
                yield return piece;
            }

            yield break;
        }

        List<string> pieces = SplitPreservingDelimiter(text, separators[separatorIndex]);
        if (pieces.Count == 1)
        {
            foreach (string nested in SplitRecursively(text, separators, separatorIndex + 1, maximumTokens))
            {
                yield return nested;
            }

            yield break;
        }

        foreach (string piece in pieces)
        {
            foreach (string nested in SplitRecursively(piece, separators, separatorIndex + 1, maximumTokens))
            {
                yield return nested;
            }
        }
    }

    private IEnumerable<string> HardSplit(string text, int maximumTokens)
    {
        string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        StringBuilder builder = new();
        foreach (string word in words)
        {
            string candidate = builder.Length == 0 ? word : $"{builder} {word}";
            if (builder.Length > 0 && _tokenCounter.CountTokens(candidate) > maximumTokens)
            {
                yield return builder.ToString();
                builder.Clear();
            }

            if (_tokenCounter.CountTokens(word) > maximumTokens)
            {
                if (builder.Length > 0)
                {
                    yield return builder.ToString();
                    builder.Clear();
                }

                int characterWindow = Math.Max(1, maximumTokens * 3);
                for (int offset = 0; offset < word.Length; offset += characterWindow)
                {
                    yield return word.Substring(offset, Math.Min(characterWindow, word.Length - offset));
                }

                continue;
            }

            if (builder.Length > 0)
            {
                builder.Append(' ');
            }

            builder.Append(word);
        }

        if (builder.Length > 0)
        {
            yield return builder.ToString();
        }
    }

    private static List<string> SplitPreservingDelimiter(string text, string separator)
    {
        List<string> result = [];
        int start = 0;
        while (start < text.Length)
        {
            int index = text.IndexOf(separator, start, StringComparison.Ordinal);
            if (index < 0)
            {
                string tail = text[start..].Trim();
                if (tail.Length > 0)
                {
                    result.Add(tail);
                }

                break;
            }

            int end = index + separator.Length;
            string piece = text[start..end].Trim();
            if (piece.Length > 0)
            {
                result.Add(piece);
            }

            start = end;
        }

        return result;
    }

    private AtomicUnit? CreateOverlap(IReadOnlyList<AtomicUnit> emitted, int overlapTokens)
    {
        if (overlapTokens == 0 || emitted.Count == 0)
        {
            return null;
        }

        AtomicUnit source = emitted[^1];
        string[] words = string.Join(" ", emitted.Select(static unit => unit.Text))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (words.Length == 0)
        {
            return null;
        }

        int first = words.Length - 1;
        string tail = words[first];
        while (first > 0)
        {
            string candidate = $"{words[first - 1]} {tail}";
            if (_tokenCounter.CountTokens(candidate) > overlapTokens)
            {
                break;
            }

            tail = candidate;
            first--;
        }

        int tokens = _tokenCounter.CountTokens(tail);
        return tokens == 0 ? null : source with { Text = tail, Tokens = tokens };
    }

    private void Emit(List<TextChunk> chunks, IReadOnlyList<AtomicUnit> units)
    {
        string text = string.Join("\n\n", units.Select(static unit => unit.Text)).Trim();
        if (text.Length == 0)
        {
            return;
        }

        chunks.Add(new TextChunk
        {
            Sequence = chunks.Count,
            Text = text,
            EstimatedTokens = _tokenCounter.CountTokens(text),
            SectionSequences = units.Select(static unit => unit.SectionSequence).Distinct().ToArray(),
            Locations = units.Select(static unit => unit.Location).Distinct(StringComparer.Ordinal).ToArray(),
            Heading = units.Select(static unit => unit.Heading).FirstOrDefault(static heading => !string.IsNullOrWhiteSpace(heading)),
        });
    }

    private void MergeSmallTail(List<TextChunk> chunks, ChunkingOptions options)
    {
        if (chunks.Count < 2 || chunks[^1].EstimatedTokens >= options.MinimumChunkTokens)
        {
            return;
        }

        TextChunk previous = chunks[^2];
        TextChunk tail = chunks[^1];
        string mergedText = $"{previous.Text}\n\n{tail.Text}";
        int mergedTokens = _tokenCounter.CountTokens(mergedText);
        if (mergedTokens > options.MaximumTokens)
        {
            return;
        }

        chunks[^2] = previous with
        {
            Text = mergedText,
            EstimatedTokens = mergedTokens,
            SectionSequences = previous.SectionSequences.Concat(tail.SectionSequences).Distinct().ToArray(),
            Locations = previous.Locations.Concat(tail.Locations).Distinct(StringComparer.Ordinal).ToArray(),
        };
        chunks.RemoveAt(chunks.Count - 1);
    }

    private sealed record AtomicUnit(string Text, int Tokens, int SectionSequence, string Location, string? Heading);
}
