using System.Text;

namespace SlimVector.Indexing;

internal static class TextTokenizer
{
    public static Dictionary<string, int> CountTerms(string text)
    {
        Dictionary<string, int> terms = new(StringComparer.Ordinal);
        foreach (string term in Tokenize(text))
        {
            terms.TryGetValue(term, out int count);
            terms[term] = count + 1;
        }

        return terms;
    }

    public static IEnumerable<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        StringBuilder token = new();
        foreach (char character in text)
        {
            if (char.IsLetterOrDigit(character))
            {
                token.Append(char.ToLowerInvariant(character));
            }
            else if (token.Length > 0)
            {
                yield return token.ToString();
                token.Clear();
            }
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }
}
