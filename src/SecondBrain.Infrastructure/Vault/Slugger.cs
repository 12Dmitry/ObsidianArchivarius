// src/SecondBrain.Infrastructure/Vault/Slugger.cs

using System.Globalization;
using System.Text;

namespace SecondBrain.Infrastructure.Vault;

public static class Slugger
{
    private const string FallbackSlug = "note";

    public static string ToSlug(string? title, int maxLength = 60)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxLength, 8);

        if (string.IsNullOrWhiteSpace(title))
        {
            return FallbackSlug;
        }

        var lowerTitle = title.ToLowerInvariant();
        var transliterated = new StringBuilder(lowerTitle.Length);

        foreach (var character in lowerTitle)
        {
            transliterated.Append(TransliterateCyrillic(character));
        }

        var normalized = transliterated.ToString().Normalize(NormalizationForm.FormD);
        var slug = new StringBuilder(normalized.Length);
        var separatorPending = false;

        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            var isAllowed = character is >= 'a' and <= 'z' or >= '0' and <= '9';

            if (isAllowed)
            {
                if (separatorPending && slug.Length > 0)
                {
                    slug.Append('-');
                }

                slug.Append(character);
                separatorPending = false;
            }
            else if (slug.Length > 0)
            {
                separatorPending = true;
            }
        }

        if (slug.Length == 0)
        {
            return FallbackSlug;
        }

        var result = slug.ToString();

        if (result.Length > maxLength)
        {
            result = result[..maxLength].TrimEnd('-');
        }

        return result.Length == 0 ? FallbackSlug : result;
    }

    public static string ToUniqueSlug(
        string? title,
        Func<string, bool> exists,
        int maxLength = 60)
    {
        ArgumentNullException.ThrowIfNull(exists);

        var baseSlug = ToSlug(title, maxLength);

        if (!exists(baseSlug))
        {
            return baseSlug;
        }

        for (var suffixNum = 2; ; suffixNum++)
        {
            var suffix = "-" + suffixNum.ToString(CultureInfo.InvariantCulture);
            var availablePrefixLength = maxLength - suffix.Length;

            var prefix = availablePrefixLength > 0
                ? baseSlug[..Math.Min(baseSlug.Length, availablePrefixLength)]
                : string.Empty;

            prefix = prefix.TrimEnd('-');

            if (prefix.Length == 0)
            {
                prefix = "n";
            }

            var candidate = prefix + suffix;

            if (!exists(candidate))
            {
                return candidate;
            }
        }
    }

    public static string ToUniqueSlug(
        string? title,
        IReadOnlySet<string> existingSlugs,
        int maxLength = 60)
    {
        ArgumentNullException.ThrowIfNull(existingSlugs);

        return ToUniqueSlug(title, existingSlugs.Contains, maxLength);
    }

    private static string TransliterateCyrillic(char character)
    {
        return character switch
        {
            'а' => "a",
            'б' => "b",
            'в' => "v",
            'г' => "g",
            'д' => "d",
            'е' => "e",
            'ё' => "yo",
            'ж' => "zh",
            'з' => "z",
            'и' => "i",
            'й' => "y",
            'к' => "k",
            'л' => "l",
            'м' => "m",
            'н' => "n",
            'о' => "o",
            'п' => "p",
            'р' => "r",
            'с' => "s",
            'т' => "t",
            'у' => "u",
            'ф' => "f",
            'х' => "kh",
            'ц' => "ts",
            'ч' => "ch",
            'ш' => "sh",
            'щ' => "shch",
            'ъ' => string.Empty,
            'ы' => "y",
            'ь' => string.Empty,
            'э' => "e",
            'ю' => "yu",
            'я' => "ya",
            _ => character.ToString()
        };
    }
}
