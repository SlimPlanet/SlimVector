using System.Text;

namespace SlimVector.DocIngestor.Extractors;

internal static class TextNormalization
{
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string normalizedNewLines = value.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        StringBuilder builder = new(normalizedNewLines.Length);
        bool previousSpace = false;
        int consecutiveNewLines = 0;
        foreach (char character in normalizedNewLines)
        {
            if (character == '\n')
            {
                while (builder.Length > 0 && builder[^1] == ' ')
                {
                    builder.Length--;
                }

                if (builder.Length > 0 && consecutiveNewLines < 2)
                {
                    builder.Append('\n');
                    consecutiveNewLines++;
                }

                previousSpace = false;
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                if (!previousSpace && builder.Length > 0 && builder[^1] != '\n')
                {
                    builder.Append(' ');
                }

                previousSpace = true;
                continue;
            }

            builder.Append(character);
            previousSpace = false;
            consecutiveNewLines = 0;
        }

        return builder.ToString().Trim();
    }
}
