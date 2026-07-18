using SlimVector.DocIngestor.Abstractions;

namespace SlimVector.DocIngestor.Chunking;

public sealed class ApproximateTokenCounter : ITokenCounter
{
    public int CountTokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int tokens = 0;
        int wordCharacters = 0;
        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character) || character is '\'' or '’' or '_')
            {
                wordCharacters++;
                continue;
            }

            if (wordCharacters > 0)
            {
                tokens += Math.Max(1, (wordCharacters + 3) / 4);
                wordCharacters = 0;
            }

            if (!char.IsWhiteSpace(character))
            {
                tokens++;
            }
        }

        if (wordCharacters > 0)
        {
            tokens += Math.Max(1, (wordCharacters + 3) / 4);
        }

        return tokens;
    }
}
