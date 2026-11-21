// tests/SecondBrain.Tests/Vault/SluggerTests.cs
using FluentAssertions;
using SecondBrain.Infrastructure.Vault;
using Xunit;

namespace SecondBrain.Tests.Vault;

public sealed class SluggerTests
{
    private const string SlugPattern = "^[a-z0-9]+(-[a-z0-9]+)*$";

    [Fact]
    public void ToSlug_TransliteratesDifficultCyrillicLetters()
    {
        const string title = "Ё й щ ъ ь ц ж ю я";

        var result = Slugger.ToSlug(title);

        result.Should().Be("yo-y-shch-ts-zh-yu-ya");
        result.Should().MatchRegex(SlugPattern);
    }

    [Theory]
    [InlineData("Привет, мир!", "privet-mir")]
    [InlineData("Хлеб чай шкаф", "khleb-chay-shkaf")]
    [InlineData("Объём и мягкий знак", "obyom-i-myagkiy-znak")]
    [InlineData("Съёмка ёлки", "syomka-yolki")]
    [InlineData("Цапля жужжит", "tsaplya-zhuzhzhit")]
    public void ToSlug_TransliteratesCyrillicText(string title, string expected)
    {
        var result = Slugger.ToSlug(title);

        result.Should().Be(expected);
        result.Should().MatchRegex(SlugPattern);
    }

    [Theory]
    [InlineData("C# 14 и .NET 10 — обзор", "c-14-i-net-10-obzor")]
    [InlineData("../../etc/passwd", "etc-passwd")]
    [InlineData("Title: \"Hello\" / world!", "title-hello-world")]
    [InlineData("one---two___three", "one-two-three")]
    [InlineData("  multiple\twhitespace\ncharacters  ", "multiple-whitespace-characters")]
    public void ToSlug_NormalizesMixedTextAndSpecialCharacters(
        string title,
        string expected)
    {
        var result = Slugger.ToSlug(title);

        result.Should().Be(expected);
        result.Should().MatchRegex(SlugPattern);
    }

    [Fact]
    public void ToSlug_RemovesLatinDiacritics()
    {
        const string title = "Café résumé naïve";

        var result = Slugger.ToSlug(title);

        result.Should().Be("cafe-resume-naive");
        result.Should().MatchRegex(SlugPattern);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("🚀🔥")]
    [InlineData("... !!! ???")]
    public void ToSlug_ReturnsFallbackForEmptyOrNonSlugInput(string? title)
    {
        var result = Slugger.ToSlug(title);

        result.Should().Be("note");
        result.Should().MatchRegex(SlugPattern);
    }

    [Fact]
    public void ToSlug_TruncatesLongTitleToDefaultMaximumLength()
    {
        var title = new string('a', 150);

        var result = Slugger.ToSlug(title);

        result.Should().HaveLength(60);
        result.Should().Be(new string('a', 60));
        result.Should().NotEndWith("-");
        result.Should().MatchRegex(SlugPattern);
    }

    [Fact]
    public void ToSlug_RemovesTrailingSeparatorCreatedByTruncation()
    {
        var title = new string('a', 59) + " " + new string('b', 80);

        var result = Slugger.ToSlug(title);

        result.Should().Be(new string('a', 59));
        result.Length.Should().BeLessThanOrEqualTo(60);
        result.Should().NotEndWith("-");
        result.Should().MatchRegex(SlugPattern);
    }

    [Fact]
    public void ToSlug_RespectsCustomMaximumLength()
    {
        var result = Slugger.ToSlug("a very long title", maxLength: 8);

        result.Should().Be("a-very-l");
        result.Length.Should().BeInRange(1, 8);
        result.Should().MatchRegex(SlugPattern);
    }

    [Fact]
    public void ToUniqueSlug_ReturnsBaseSlugWhenItIsAvailable()
    {
        IReadOnlySet<string> existingSlugs = new HashSet<string>(StringComparer.Ordinal)
        {
            "another-note"
        };

        var result = Slugger.ToUniqueSlug("Test", existingSlugs);

        result.Should().Be("test");
    }

    [Fact]
    public void ToUniqueSlug_SkipsAllOccupiedNumericSuffixes()
    {
        IReadOnlySet<string> existingSlugs = new HashSet<string>(StringComparer.Ordinal)
        {
            "test",
            "test-2",
            "test-3"
        };

        var result = Slugger.ToUniqueSlug("Test", existingSlugs);

        result.Should().Be("test-4");
        result.Length.Should().BeLessThanOrEqualTo(60);
        result.Should().MatchRegex(SlugPattern);
    }

    [Fact]
    public void ToUniqueSlug_TruncatesLongBaseSlugAtMaximumLengthBoundary()
    {
        var baseSlug = new string('a', 60);
        var expectedSecondCandidate = new string('a', 58) + "-2";
        var expectedThirdCandidate = new string('a', 58) + "-3";

        IReadOnlySet<string> existingSlugs = new HashSet<string>(StringComparer.Ordinal)
        {
            baseSlug,
            expectedSecondCandidate,
            expectedThirdCandidate
        };

        var result = Slugger.ToUniqueSlug(baseSlug, existingSlugs);

        result.Should().Be(new string('a', 58) + "-4");
        result.Should().HaveLength(60);
        result.Should().NotContain("--2");
        result.Should().NotContain("--");
        existingSlugs.Contains(result).Should().BeFalse();
        result.Should().MatchRegex(SlugPattern);
    }

    [Fact]
    public void ToUniqueSlug_TrimsTrailingSeparatorBeforeAddingSuffix()
    {
        // Длина ровно 60: 57 символов 'a', затем '-' и 'bb'.
        // При обрезке префикса до 58 символов останется 57 * 'a' + '-'.
        var baseSlug = new string('a', 57) + "-bb";
        var checkedCandidates = new List<string>();

        var result = Slugger.ToUniqueSlug(
            baseSlug,
            candidate =>
            {
                checkedCandidates.Add(candidate);
                return candidate == baseSlug;
            });

        result.Should().Be(new string('a', 57) + "-2");
        result.Should().HaveLength(59);
        result.Should().NotContain("--");
        checkedCandidates.Should().Equal(baseSlug, result);
        result.Should().MatchRegex(SlugPattern);
    }

    [Fact]
    public void ToUniqueSlug_UsesNumericSuffixForFallbackSlug()
    {
        IReadOnlySet<string> existingSlugs = new HashSet<string>(StringComparer.Ordinal)
        {
            "note",
            "note-2"
        };

        var result = Slugger.ToUniqueSlug("🚀🔥", existingSlugs);

        result.Should().Be("note-3");
        result.Should().MatchRegex(SlugPattern);
    }

    [Fact]
    public void ToSlug_IsDeterministic()
    {
        const string title = "Ёж Café: C# 14 и .NET 10 🚀";

        var first = Slugger.ToSlug(title);
        var second = Slugger.ToSlug(title);

        first.Should().Be(second);
        first.Should().Be("yozh-cafe-c-14-i-net-10");
        first.Should().MatchRegex(SlugPattern);
    }

    [Fact]
    public void ToUniqueSlug_IsDeterministicForTheSameExistingSlugs()
    {
        IReadOnlySet<string> existingSlugs = new HashSet<string>(StringComparer.Ordinal)
        {
            "test",
            "test-2"
        };

        var first = Slugger.ToUniqueSlug("Test", existingSlugs);
        var second = Slugger.ToUniqueSlug("Test", existingSlugs);

        first.Should().Be("test-3");
        second.Should().Be(first);
    }

    [Theory]
    [InlineData(7)]
    [InlineData(0)]
    [InlineData(-1)]
    public void ToSlug_ThrowsWhenMaximumLengthIsTooSmall(int maxLength)
    {
        var action = () => Slugger.ToSlug("Test", maxLength);

        action.Should().Throw<ArgumentOutOfRangeException>()
            .WithParameterName("maxLength");
    }

    [Fact]
    public void ToUniqueSlug_ThrowsWhenExistsCallbackIsNull()
    {
        Func<string, bool> exists = null!;

        var action = () => Slugger.ToUniqueSlug("Test", exists);

        action.Should().Throw<ArgumentNullException>()
            .WithParameterName("exists");
    }

    [Fact]
    public void ToUniqueSlug_ThrowsWhenExistingSlugsSetIsNull()
    {
        IReadOnlySet<string> existingSlugs = null!;

        var action = () => Slugger.ToUniqueSlug("Test", existingSlugs);

        action.Should().Throw<ArgumentNullException>()
            .WithParameterName("existingSlugs");
    }
}
