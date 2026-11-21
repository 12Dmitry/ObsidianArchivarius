namespace SecondBrain.Tests.Vault;

using System;
using System.Text;
using FluentAssertions;
using SecondBrain.Infrastructure.Vault;
using Xunit;

public class ManagedBlockCodecTests
{
    [Fact]
    public void UpsertBlock_PreservesPrefixAndSuffixByteForByte_WithBomCyrillicCrlfAndTrailingSpaces()
    {
        string prefix = "\uFEFF# Заметка: Архитектура Второго Мозга   \r\n" +
                        "Вторая строка с эмодзи 🧠 и завершающими пробелами     \r\n" +
                        "<!-- brain:links:begin -->\r\n";
        string oldContent = "- [Старая ссылка](https://old.domain.org)\r\n";
        string suffix = "<!-- brain:links:end -->\r\n" +
                        "## Пользовательский раздел   \r\n" +
                        "Текст после блока с кириллицей и CRLF.\r\n";

        string originalMarkdown = prefix + oldContent + suffix;
        string newContent = "- [Новая ссылка 1](https://example.com)\r\n- [Новая ссылка 2](https://test.org)";

        string updatedMarkdown = ManagedBlockCodec.UpsertBlock(originalMarkdown, "links", newContent);

        int prefixLength = prefix.Length;
        int suffixLength = suffix.Length;

        string updatedPrefix = updatedMarkdown[..prefixLength];
        string updatedSuffix = updatedMarkdown[^suffixLength..];

        updatedPrefix.Should().Be(prefix);
        updatedSuffix.Should().Be(suffix);

        byte[] originalPrefixBytes = Encoding.UTF8.GetBytes(prefix);
        byte[] updatedPrefixBytes = Encoding.UTF8.GetBytes(updatedPrefix);
        updatedPrefixBytes.Should().Equal(originalPrefixBytes);

        byte[] originalSuffixBytes = Encoding.UTF8.GetBytes(suffix);
        byte[] updatedSuffixBytes = Encoding.UTF8.GetBytes(updatedSuffix);
        updatedSuffixBytes.Should().Equal(originalSuffixBytes);
    }

    [Fact]
    public void UpsertBlock_PreservesManualEdits_BeforeBetweenMultipleBlocksAndAfter()
    {
        string markdown = "# Главный заголовок\n\n" +
                          "Ручные правки ДО первого блока.\n\n" +
                          "<!-- brain:links:begin -->\n" +
                          "- link 1\n" +
                          "<!-- brain:links:end -->\n\n" +
                          "Ручные правки МЕЖДУ блоками links и summary:\n" +
                          "1. Важная мысль\n" +
                          "2. Ещё мысль\n\n" +
                          "<!-- brain:summary:begin -->\n" +
                          "Старое резюме.\n" +
                          "<!-- brain:summary:end -->\n\n" +
                          "Ручные правки ПОСЛЕ всех блоков.\n";

        string afterLinksUpdate = ManagedBlockCodec.UpsertBlock(
            markdown,
            "links",
            "- updated link A\n- updated link B");

        afterLinksUpdate.Should().Contain("Ручные правки ДО первого блока.");
        afterLinksUpdate.Should().Contain("Ручные правки МЕЖДУ блоками links и summary:\n1. Важная мысль\n2. Ещё мысль");
        afterLinksUpdate.Should().Contain("Старое резюме.");
        afterLinksUpdate.Should().Contain("Ручные правки ПОСЛЕ всех блоков.");
        ManagedBlockCodec.ExtractBlock(afterLinksUpdate, "links").Should().Be("- updated link A\n- updated link B");

        string afterBothUpdates = ManagedBlockCodec.UpsertBlock(
            afterLinksUpdate,
            "brain:summary",
            "Новое сжатое резюме заметки.");

        afterBothUpdates.Should().Contain("Ручные правки ДО первого блока.");
        afterBothUpdates.Should().Contain("- updated link A\n- updated link B");
        afterBothUpdates.Should().Contain("Ручные правки МЕЖДУ блоками links и summary:\n1. Важная мысль\n2. Ещё мысль");
        afterBothUpdates.Should().Contain("Ручные правки ПОСЛЕ всех блоков.");
        ManagedBlockCodec.ExtractBlock(afterBothUpdates, "summary").Should().Be("Новое сжатое резюме заметки.");
    }

    [Fact]
    public void UpsertBlock_IsIdempotent_WhenCalledMultipleTimesWithSameContent()
    {
        string markdown = "# Document\n\n" +
                          "<!-- brain:links:begin -->\n" +
                          "Initial content\n" +
                          "<!-- brain:links:end -->\n\n" +
                          "Footer text\n";

        string newContent = "Deterministic Item 1\nDeterministic Item 2";

        string firstPass = ManagedBlockCodec.UpsertBlock(markdown, "links", newContent);
        string secondPass = ManagedBlockCodec.UpsertBlock(firstPass, "links", newContent);
        string thirdPass = ManagedBlockCodec.UpsertBlock(secondPass, "brain:links", newContent);

        secondPass.Should().Be(firstPass);
        thirdPass.Should().Be(firstPass);

        byte[] firstBytes = Encoding.UTF8.GetBytes(firstPass);
        byte[] secondBytes = Encoding.UTF8.GetBytes(secondPass);
        byte[] thirdBytes = Encoding.UTF8.GetBytes(thirdPass);

        secondBytes.Should().Equal(firstBytes);
        thirdBytes.Should().Equal(firstBytes);
    }

    [Fact]
    public void BlockId_BothBareAndPrefixedWithBrain_WorkIdentically()
    {
        string markdown = "# Header\n\n" +
                          "<!-- brain:links:begin -->\n" +
                          "Existing item\n" +
                          "<!-- brain:links:end -->\n";

        ManagedBlockCodec.ContainsBlock(markdown, "links").Should().BeTrue();
        ManagedBlockCodec.ContainsBlock(markdown, "brain:links").Should().BeTrue();

        ManagedBlockCodec.ExtractBlock(markdown, "links").Should().Be("Existing item");
        ManagedBlockCodec.ExtractBlock(markdown, "brain:links").Should().Be("Existing item");

        string updatedWithPrefixed = ManagedBlockCodec.UpsertBlock(markdown, "brain:links", "Updated content");
        ManagedBlockCodec.ExtractBlock(updatedWithPrefixed, "links").Should().Be("Updated content");

        string removedWithPrefixed = ManagedBlockCodec.RemoveBlock(markdown, "brain:links");
        ManagedBlockCodec.ContainsBlock(removedWithPrefixed, "links").Should().BeFalse();
    }

    [Fact]
    public void ScanDocument_IgnoresMarkersInsideFencedCodeBlocks_BackticksAndTildes()
    {
        string markdown = "# Документация плагина\n\n" +
                          "Пример маркеров в Markdown fenced code block:\n" +
                          "```markdown\n" +
                          "<!-- brain:links:begin -->\n" +
                          "Пример содержимого внутри блока ссылки\n" +
                          "<!-- brain:links:end -->\n" +
                          "```\n\n" +
                          "И пример с тильдами:\n" +
                          "~~~markdown\n" +
                          "<!-- brain:links:begin -->\n" +
                          "Пример без закрывающего тега не должен вызывать FormatException\n" +
                          "~~~\n\n" +
                          "Настоящий рабочий блок:\n" +
                          "<!-- brain:links:begin -->\n" +
                          "Настоящий контент блока\n" +
                          "<!-- brain:links:end -->\n";

        ManagedBlockCodec.ContainsBlock(markdown, "links").Should().BeTrue();
        ManagedBlockCodec.ExtractBlock(markdown, "links").Should().Be("Настоящий контент блока");

        string updated = ManagedBlockCodec.UpsertBlock(markdown, "links", "Обновлённый настоящий контент");

        updated.Should().Contain("Пример содержимого внутри блока ссылки");
        updated.Should().Contain("Пример без закрывающего тега не должен вызывать FormatException");
        ManagedBlockCodec.ExtractBlock(updated, "links").Should().Be("Обновлённый настоящий контент");
    }

    [Fact]
    public void UpsertBlock_WhenBlockMissingAndAppendIfMissingIsTrue_AppendsCleanly()
    {
        string emptyDoc = string.Empty;
        string resultOnEmpty = ManagedBlockCodec.UpsertBlock(emptyDoc, "links", "Item on empty", appendIfMissing: true);
        resultOnEmpty.Should().Be("<!-- brain:links:begin -->\nItem on empty\n<!-- brain:links:end -->\n");
        ManagedBlockCodec.ExtractBlock(resultOnEmpty, "links").Should().Be("Item on empty");

        string noTrailingNewlineDoc = "# Header\nSome note text without newline";
        string resultNoNewline = ManagedBlockCodec.UpsertBlock(noTrailingNewlineDoc, "links", "Item 1", appendIfMissing: true);
        resultNoNewline.Should().Be("# Header\nSome note text without newline\n\n<!-- brain:links:begin -->\nItem 1\n<!-- brain:links:end -->\n");

        string singleNewlineDoc = "# Header\nSome note text\n";
        string resultSingleNewline = ManagedBlockCodec.UpsertBlock(singleNewlineDoc, "links", "Item 1", appendIfMissing: true);
        resultSingleNewline.Should().Be("# Header\nSome note text\n\n<!-- brain:links:begin -->\nItem 1\n<!-- brain:links:end -->\n");

        string doubleNewlineDoc = "# Header\nSome note text\n\n";
        string resultDoubleNewline = ManagedBlockCodec.UpsertBlock(doubleNewlineDoc, "links", "Item 1", appendIfMissing: true);
        resultDoubleNewline.Should().Be("# Header\nSome note text\n\n<!-- brain:links:begin -->\nItem 1\n<!-- brain:links:end -->\n");

        string crlfDoc = "# Header\r\nТекст с CRLF.\r\n";
        string resultCrlf = ManagedBlockCodec.UpsertBlock(crlfDoc, "links", "Пункт 1\r\nПункт 2", appendIfMissing: true);
        resultCrlf.Should().Be("# Header\r\nТекст с CRLF.\r\n\r\n<!-- brain:links:begin -->\r\nПункт 1\r\nПункт 2\r\n<!-- brain:links:end -->\r\n");
    }

    [Fact]
    public void UpsertBlock_WhenBlockMissingAndAppendIfMissingIsFalse_ThrowsInvalidOperationException()
    {
        string markdown = "# Note\nSome content without managed block.\n";

        Action act = () => ManagedBlockCodec.UpsertBlock(markdown, "links", "content", appendIfMissing: false);

        act.Should().Throw<InvalidOperationException>()
           .WithMessage("*links*");
    }

    [Fact]
    public void UpsertBlock_UpdatingWithEmptyContent_ClearsBlockContent()
    {
        string markdown = "# Header\n" +
                          "<!-- brain:links:begin -->\n" +
                          "- Old Link 1\n" +
                          "- Old Link 2\n" +
                          "<!-- brain:links:end -->\n" +
                          "# Footer\n";

        string updated = ManagedBlockCodec.UpsertBlock(markdown, "links", string.Empty);

        updated.Should().Be("# Header\n<!-- brain:links:begin -->\n<!-- brain:links:end -->\n# Footer\n");
        ManagedBlockCodec.ContainsBlock(updated, "links").Should().BeTrue();
        ManagedBlockCodec.ExtractBlock(updated, "links").Should().Be(string.Empty);
    }

    [Fact]
    public void RemoveBlock_RemovesExistingBlockAndMarkers_PreservingSurroundingDocument()
    {
        string markdown = "# Заголовок\n\n" +
                          "<!-- brain:links:begin -->\n" +
                          "- Ссылка 1\n" +
                          "- Ссылка 2\n" +
                          "<!-- brain:links:end -->\n\n" +
                          "# Подвал\n";

        string removed = ManagedBlockCodec.RemoveBlock(markdown, "links");

        removed.Should().Be("# Заголовок\n\n\n# Подвал\n");
        ManagedBlockCodec.ContainsBlock(removed, "links").Should().BeFalse();
        ManagedBlockCodec.ExtractBlock(removed, "links").Should().BeNull();
    }

    [Fact]
    public void RemoveBlock_WhenBlockNotFound_ReturnsOriginalDocumentUnchanged()
    {
        string markdown = "# Header\nContent without block\n";

        string result = ManagedBlockCodec.RemoveBlock(markdown, "nonexistent");

        result.Should().Be(markdown);
    }

    [Fact]
    public void IntegrityValidation_UnclosedBegin_ThrowsFormatException()
    {
        string markdown = "# Header\n<!-- brain:links:begin -->\nSome dangling content without end marker";

        Action act = () => ManagedBlockCodec.ContainsBlock(markdown, "links");

        act.Should().Throw<FormatException>()
           .WithMessage("*without a corresponding end marker*");
    }

    [Fact]
    public void IntegrityValidation_OrphanEndMarker_ThrowsFormatException()
    {
        string markdown = "# Header\nSome note\n<!-- brain:links:end -->\n";

        Action act = () => ManagedBlockCodec.ContainsBlock(markdown, "links");

        act.Should().Throw<FormatException>()
           .WithMessage("*without an opening begin marker*");
    }

    [Fact]
    public void IntegrityValidation_NestedBegin_ThrowsFormatException()
    {
        string markdown = "<!-- brain:blockA:begin -->\n" +
                          "outer content\n" +
                          "<!-- brain:blockB:begin -->\n" +
                          "inner content\n" +
                          "<!-- brain:blockB:end -->\n" +
                          "<!-- brain:blockA:end -->\n";

        Action act = () => ManagedBlockCodec.ContainsBlock(markdown, "blockA");

        act.Should().Throw<FormatException>()
           .WithMessage("*nested begin marker*");
    }

    [Fact]
    public void IntegrityValidation_DuplicateBlockId_ThrowsFormatException()
    {
        string markdown = "<!-- brain:links:begin -->\n" +
                          "Block 1\n" +
                          "<!-- brain:links:end -->\n\n" +
                          "<!-- brain:links:begin -->\n" +
                          "Duplicate Block 2\n" +
                          "<!-- brain:links:end -->\n";

        Action act = () => ManagedBlockCodec.ContainsBlock(markdown, "links");

        act.Should().Throw<FormatException>()
           .WithMessage("*Duplicate block*");
    }

    [Fact]
    public void IntegrityValidation_MismatchedEndMarker_ThrowsFormatException()
    {
        string markdown = "<!-- brain:links:begin -->\n" +
                          "content\n" +
                          "<!-- brain:summary:end -->\n";

        Action act = () => ManagedBlockCodec.ContainsBlock(markdown, "links");

        act.Should().Throw<FormatException>()
           .WithMessage("*Mismatched end marker*");
    }

    [Theory]
    [InlineData("<!-- brain:links:start -->\n<!-- brain:links:end -->\n")]
    [InlineData("<!-- brain::begin -->\n<!-- brain::end -->\n")]
    [InlineData("<!-- brain:bad id:begin -->\n<!-- brain:bad id:end -->\n")]
    public void IntegrityValidation_MalformedMarkerInDocument_ThrowsFormatException(string malformedMarkdown)
    {
        Action act = () => ManagedBlockCodec.ContainsBlock(malformedMarkdown, "links");

        act.Should().Throw<FormatException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bad id")]
    [InlineData("a-->b")]
    [InlineData("brain:")]
    [InlineData("brain:bad id")]
    [InlineData("special#id")]
    [InlineData("brain:brain:links")]
    public void Validation_InvalidBlockId_ThrowsArgumentException(string invalidId)
    {
        string validMarkdown = "# Test\n";

        Action contains = () => ManagedBlockCodec.ContainsBlock(validMarkdown, invalidId);
        Action extract = () => ManagedBlockCodec.ExtractBlock(validMarkdown, invalidId);
        Action upsert = () => ManagedBlockCodec.UpsertBlock(validMarkdown, invalidId, "data");
        Action remove = () => ManagedBlockCodec.RemoveBlock(validMarkdown, invalidId);

        contains.Should().Throw<ArgumentException>();
        extract.Should().Throw<ArgumentException>();
        upsert.Should().Throw<ArgumentException>();
        remove.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Validation_NullArguments_ThrowArgumentNullException()
    {
        Action act1 = () => ManagedBlockCodec.ContainsBlock(null!, "links");
        Action act2 = () => ManagedBlockCodec.ContainsBlock("# Doc", null!);
        Action act3 = () => ManagedBlockCodec.ExtractBlock(null!, "links");
        Action act4 = () => ManagedBlockCodec.ExtractBlock("# Doc", null!);
        Action act5 = () => ManagedBlockCodec.UpsertBlock(null!, "links", "c");
        Action act6 = () => ManagedBlockCodec.UpsertBlock("# Doc", null!, "c");
        Action act7 = () => ManagedBlockCodec.UpsertBlock("# Doc", "links", null!);
        Action act8 = () => ManagedBlockCodec.RemoveBlock(null!, "links");
        Action act9 = () => ManagedBlockCodec.RemoveBlock("# Doc", null!);

        act1.Should().Throw<ArgumentNullException>();
        act2.Should().Throw<ArgumentNullException>();
        act3.Should().Throw<ArgumentNullException>();
        act4.Should().Throw<ArgumentNullException>();
        act5.Should().Throw<ArgumentNullException>();
        act6.Should().Throw<ArgumentNullException>();
        act7.Should().Throw<ArgumentNullException>();
        act8.Should().Throw<ArgumentNullException>();
        act9.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void UpsertBlock_NormalizesContentNewlines_ToDocumentDominantStyle()
    {
        string crlfDoc = "# Title\r\n<!-- brain:links:begin -->\r\n<!-- brain:links:end -->\r\n";
        string lfContent = "Line 1\nLine 2\nLine 3";

        string updatedCrlf = ManagedBlockCodec.UpsertBlock(crlfDoc, "links", lfContent);
        updatedCrlf.Should().Be("# Title\r\n<!-- brain:links:begin -->\r\nLine 1\r\nLine 2\r\nLine 3\r\n<!-- brain:links:end -->\r\n");

        string lfDoc = "# Title\n<!-- brain:links:begin -->\n<!-- brain:links:end -->\n";
        string crlfContent = "Line A\r\nLine B\r\nLine C";

        string updatedLf = ManagedBlockCodec.UpsertBlock(lfDoc, "links", crlfContent);
        updatedLf.Should().Be("# Title\n<!-- brain:links:begin -->\nLine A\nLine B\nLine C\n<!-- brain:links:end -->\n");
    }

    [Fact]
    public void ExtractBlock_WhenBlockDoesNotExist_ReturnsNull()
    {
        string doc = "# Just Markdown\nWithout any managed block.\n";

        string? extracted = ManagedBlockCodec.ExtractBlock(doc, "links");

        extracted.Should().BeNull();
    }
}