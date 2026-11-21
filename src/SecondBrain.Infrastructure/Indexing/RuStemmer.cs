namespace SecondBrain.Infrastructure.Indexing;

public static class RuStemmer
{
    private const int MinStemLength = 3;
    private const int MinWordLengthToStem = 4;

    private static readonly string[] PerfectiveGerundGroup1 = ["вшись", "вши", "в"];
    private static readonly string[] PerfectiveGerundGroup2 = ["ившись", "ывшись", "ивши", "ывши", "ив", "ыв"];

    private static readonly string[] Reflexive = ["ся", "сь"];

    private static readonly string[] Adjective =
    [
        "ими", "ыми", "его", "ого", "ему", "ому",
        "ее", "ие", "ые", "ое", "ей", "ий", "ый", "ой",
        "ем", "им", "ым", "ом", "их", "ых", "ую", "юю",
        "ая", "яя", "ою", "ею"
    ];

    private static readonly string[] ParticipleGroup1 = ["ем", "нн", "вш", "ющ", "щ"];
    private static readonly string[] ParticipleGroup2 = ["ивш", "ывш", "ующ"];

    private static readonly string[] VerbGroup1 =
    [
        "ете", "йте", "ешь", "нно",
        "ла", "на", "ли", "ем", "ло", "но", "ет", "ют", "ны", "ть",
        "й", "л", "н"
    ];

    private static readonly string[] VerbGroup2 =
    [
        "ейте", "уйте",
        "ила", "ыла", "ена", "ите", "или", "ыли", "ило", "ыло", "ено", "ует", "уют", "ены", "ить", "ыть", "ишь",
        "ей", "уй", "ил", "ыл", "им", "ым", "ен", "ят", "ит", "ыт", "ую",
        "ю"
    ];

    private static readonly string[] Noun =
    [
        "иями", "ями", "ами", "ией", "иям", "ием", "иях",
        "ев", "ов", "ие", "ье", "еи", "ии", "ей", "ой", "ий", "ям", "ем", "ам", "ом", "ах", "ях", "ию", "ью", "ия", "ья",
        "а", "е", "и", "й", "о", "у", "ы", "ь", "ю", "я"
    ];

    private static readonly string[] Superlative = ["ейше", "ейш"];
    private static readonly string[] Derivational = ["ость", "ост"];

    public static string Stem(string word)
    {
        if (string.IsNullOrEmpty(word))
        {
            return string.Empty;
        }

        return Stem(word.AsSpan());
    }

    internal static string Stem(ReadOnlySpan<char> word)
    {
        if (word.IsEmpty)
        {
            return string.Empty;
        }

        Span<char> buffer = word.Length <= 256
            ? stackalloc char[word.Length]
            : new char[word.Length];

        var hasCyrillic = false;
        for (var i = 0; i < word.Length; i++)
        {
            var lowered = char.ToLowerInvariant(word[i]);
            if (lowered == 'ё')
            {
                lowered = 'е';
            }

            if (lowered is >= 'а' and <= 'я')
            {
                hasCyrillic = true;
            }

            buffer[i] = lowered;
        }

        ReadOnlySpan<char> normalized = buffer;
        if (!hasCyrillic || normalized.Length < MinWordLengthToStem)
        {
            return new string(normalized);
        }

        FindRegions(normalized, out var rv, out var r2);
        if (rv >= normalized.Length)
        {
            return new string(normalized);
        }

        var current = normalized;

        // Step 1: Perfective gerund, otherwise reflexive + (adjectival | verb | noun)
        var strippedLength = TryMatchGroup1Or2(current, rv, PerfectiveGerundGroup1, PerfectiveGerundGroup2);
        if (strippedLength > 0)
        {
            current = current[..^strippedLength];
        }
        else
        {
            var reflLength = FindLongestSuffix(current, rv, Reflexive, requirePrecedingAOrYa: false);
            if (reflLength > 0)
            {
                current = current[..^reflLength];
            }

            var adjLength = FindLongestSuffix(current, rv, Adjective, requirePrecedingAOrYa: false);
            if (adjLength > 0)
            {
                current = current[..^adjLength];
                var partLength = TryMatchGroup1Or2(current, rv, ParticipleGroup1, ParticipleGroup2);
                if (partLength > 0)
                {
                    current = current[..^partLength];
                }
            }
            else
            {
                var verbLength = TryMatchGroup1Or2(current, rv, VerbGroup1, VerbGroup2);
                if (verbLength > 0)
                {
                    current = current[..^verbLength];
                }
                else
                {
                    var nounLength = FindLongestSuffix(current, rv, Noun, requirePrecedingAOrYa: false);
                    if (nounLength > 0)
                    {
                        current = current[..^nounLength];
                    }
                }
            }
        }

        // Step 2: Trailing 'и' in RV
        if (current.Length > rv && current[^1] == 'и')
        {
            current = current[..^1];
        }

        // Step 3: Derivational suffix in R2
        if (current.Length > r2)
        {
            var derivLength = FindLongestSuffix(current, r2, Derivational, requirePrecedingAOrYa: false);
            if (derivLength > 0)
            {
                current = current[..^derivLength];
            }
        }

        // Step 4: Undouble 'нн', superlative (+ undouble 'нн'), or trailing 'ь' in RV
        if (EndsWithDoubleN(current, rv))
        {
            current = current[..^1];
        }
        else
        {
            var supLength = FindLongestSuffix(current, rv, Superlative, requirePrecedingAOrYa: false);
            if (supLength > 0)
            {
                current = current[..^supLength];
                if (EndsWithDoubleN(current, rv))
                {
                    current = current[..^1];
                }
            }
            else if (current.Length > rv && current[^1] == 'ь')
            {
                current = current[..^1];
            }
        }

        return current.Length < MinStemLength
            ? new string(normalized)
            : new string(current);
    }

    private static void FindRegions(ReadOnlySpan<char> word, out int rv, out int r2)
    {
        rv = word.Length;
        var r1 = word.Length;
        r2 = word.Length;

        for (var i = 0; i < word.Length; i++)
        {
            if (IsVowel(word[i]))
            {
                rv = i + 1;
                break;
            }
        }

        for (var i = 0; i < word.Length - 1; i++)
        {
            if (IsVowel(word[i]) && !IsVowel(word[i + 1]))
            {
                r1 = i + 2;
                break;
            }
        }

        for (var i = r1; i < word.Length - 1; i++)
        {
            if (IsVowel(word[i]) && !IsVowel(word[i + 1]))
            {
                r2 = i + 2;
                break;
            }
        }
    }

    private static int TryMatchGroup1Or2(
        ReadOnlySpan<char> word,
        int regionStart,
        string[] group1,
        string[] group2)
    {
        var len1 = FindLongestSuffix(word, regionStart, group1, requirePrecedingAOrYa: true);
        var len2 = FindLongestSuffix(word, regionStart, group2, requirePrecedingAOrYa: false);
        return Math.Max(len1, len2);
    }

    private static int FindLongestSuffix(
        ReadOnlySpan<char> word,
        int regionStart,
        string[] suffixes,
        bool requirePrecedingAOrYa)
    {
        var bestLength = 0;
        var available = word.Length - regionStart;
        if (available <= 0)
        {
            return 0;
        }

        for (var i = 0; i < suffixes.Length; i++)
        {
            var suffix = suffixes[i];
            if (suffix.Length <= bestLength || suffix.Length > available)
            {
                continue;
            }

            if (!word.EndsWith(suffix.AsSpan(), StringComparison.Ordinal))
            {
                continue;
            }

            if (requirePrecedingAOrYa)
            {
                var precedingIndex = word.Length - suffix.Length - 1;
                if (precedingIndex < regionStart)
                {
                    continue;
                }

                var ch = word[precedingIndex];
                if (ch != 'а' && ch != 'я')
                {
                    continue;
                }
            }

            bestLength = suffix.Length;
        }

        return bestLength;
    }

    private static bool EndsWithDoubleN(ReadOnlySpan<char> word, int rv)
        => word.Length - rv >= 2 && word[^1] == 'н' && word[^2] == 'н';

    private static bool IsVowel(char c)
        => c is 'а' or 'е' or 'и' or 'о' or 'у' or 'ы' or 'э' or 'ю' or 'я';
}