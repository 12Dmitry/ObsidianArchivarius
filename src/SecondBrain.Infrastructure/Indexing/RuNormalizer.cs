namespace SecondBrain.Infrastructure.Indexing;

public static class RuNormalizer
{
    public static string NormalizeForSearch(string? text)
    {
        var tokens = TokenizeNormalized(text);
        return tokens.Count == 0 ? string.Empty : string.Join(' ', tokens);
    }

    public static IReadOnlyList<string> TokenizeNormalized(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var tokens = new List<string>();
        var span = text.AsSpan();
        var i = 0;

        while (i < span.Length)
        {
            while (i < span.Length && !char.IsLetterOrDigit(span[i]))
            {
                i++;
            }

            if (i >= span.Length)
            {
                break;
            }

            var start = i;
            while (i < span.Length && char.IsLetterOrDigit(span[i]))
            {
                i++;
            }

            var token = RuStemmer.Stem(span[start..i]);
            if (token.Length > 0)
            {
                tokens.Add(token);
            }
        }

        return tokens;
    }
}