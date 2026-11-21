namespace SecondBrain.Infrastructure.Vault;

using System;
using System.Collections.Generic;
using System.Text;

/// <summary>
/// Provides deterministic decoding, extraction, upsertion, and removal
/// of managed blocks inside Obsidian Markdown notes.
/// </summary>
public static class ManagedBlockCodec
{
    private const string BrainPrefix = "brain:";
    private const string MarkerPrefix = "<!-- brain:";
    private const string MarkerSuffix = " -->";
    private const string BeginToken = ":begin";
    private const string EndToken = ":end";

    /// <summary>
    /// Checks whether the specified managed block exists in the markdown document.
    /// </summary>
    /// <param name="markdown">The markdown text to scan.</param>
    /// <param name="blockId">The identifier of the block (e.g. "links" or "brain:links").</param>
    /// <returns><c>true</c> if the block is found; otherwise, <c>false</c>.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="markdown"/> or <paramref name="blockId"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="blockId"/> has an invalid format.</exception>
    /// <exception cref="FormatException">Thrown if document markers are corrupted or malformed.</exception>
    public static bool ContainsBlock(string markdown, string blockId)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        string normalizedId = NormalizeAndValidateBlockId(blockId);

        Dictionary<string, BlockLocation> blocks = ScanDocument(markdown);
        return blocks.ContainsKey(normalizedId);
    }

    /// <summary>
    /// Extracts the content between the begin and end markers of the specified managed block.
    /// </summary>
    /// <param name="markdown">The markdown text to scan.</param>
    /// <param name="blockId">The identifier of the block (e.g. "links" or "brain:links").</param>
    /// <returns>
    /// The content of the block without the markers and without the trailing newline separating the end marker,
    /// or <c>null</c> if the block is not found.
    /// </returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="markdown"/> or <paramref name="blockId"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="blockId"/> has an invalid format.</exception>
    /// <exception cref="FormatException">Thrown if document markers are corrupted or malformed.</exception>
    public static string? ExtractBlock(string markdown, string blockId)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        string normalizedId = NormalizeAndValidateBlockId(blockId);

        Dictionary<string, BlockLocation> blocks = ScanDocument(markdown);
        if (!blocks.TryGetValue(normalizedId, out BlockLocation? location))
        {
            return null;
        }

        string rawContent = markdown[location.ContentStartIndex..location.ContentEndIndex];

        if (rawContent.EndsWith("\r\n", StringComparison.Ordinal))
        {
            return rawContent[..^2];
        }

        if (rawContent.EndsWith('\n'))
        {
            return rawContent[..^1];
        }

        return rawContent;
    }

    /// <summary>
    /// Inserts or replaces a managed block in the markdown document while preserving surrounding content byte-for-byte.
    /// </summary>
    /// <param name="markdown">The markdown text.</param>
    /// <param name="blockId">The identifier of the block (e.g. "links" or "brain:links").</param>
    /// <param name="content">The inner content to place inside the managed block.</param>
    /// <param name="appendIfMissing"><c>true</c> to append the block at the end if absent; <c>false</c> to throw.</param>
    /// <returns>The updated markdown string.</returns>
    /// <exception cref="ArgumentNullException">Thrown if any parameter is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="blockId"/> has an invalid format.</exception>
    /// <exception cref="InvalidOperationException">Thrown if the block does not exist and <paramref name="appendIfMissing"/> is false.</exception>
    /// <exception cref="FormatException">Thrown if document markers are corrupted or malformed.</exception>
    public static string UpsertBlock(string markdown, string blockId, string content, bool appendIfMissing = true)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        ArgumentNullException.ThrowIfNull(content);
        string normalizedId = NormalizeAndValidateBlockId(blockId);

        Dictionary<string, BlockLocation> blocks = ScanDocument(markdown);
        string eol = DetectDominantEol(markdown);

        string trimmedContent = content.TrimEnd('\r', '\n');
        string normalizedContentWithEol = trimmedContent.Length > 0
            ? NormalizeNewlines(trimmedContent, eol) + eol
            : string.Empty;

        if (blocks.TryGetValue(normalizedId, out BlockLocation? location))
        {
            // Character-for-character preservation:
            // Everything up to ContentStartIndex (includes begin marker line and its newline)
            // + normalized content + everything from ContentEndIndex (includes end marker line to EOF).
            string prefix = markdown[..location.ContentStartIndex];
            string suffix = markdown[location.ContentEndIndex..];
            return prefix + normalizedContentWithEol + suffix;
        }

        if (!appendIfMissing)
        {
            throw new InvalidOperationException(
                $"Block '{normalizedId}' was not found in the markdown document and {nameof(appendIfMissing)} is false.");
        }

        string separator = DetermineAppendSeparator(markdown, eol);
        var sb = new StringBuilder(markdown.Length + separator.Length + (normalizedId.Length * 2) + normalizedContentWithEol.Length + 64);
        sb.Append(markdown);
        sb.Append(separator);
        sb.Append(MarkerPrefix).Append(normalizedId).Append(BeginToken).Append(MarkerSuffix).Append(eol);
        if (normalizedContentWithEol.Length > 0)
        {
            sb.Append(normalizedContentWithEol);
        }

        sb.Append(MarkerPrefix).Append(normalizedId).Append(EndToken).Append(MarkerSuffix).Append(eol);

        return sb.ToString();
    }

    /// <summary>
    /// Removes the specified managed block and its enclosing markers from the markdown document.
    /// </summary>
    /// <param name="markdown">The markdown text.</param>
    /// <param name="blockId">The identifier of the block (e.g. "links" or "brain:links").</param>
    /// <returns>The markdown string without the block, or the original string if the block was not present.</returns>
    /// <exception cref="ArgumentNullException">Thrown if <paramref name="markdown"/> or <paramref name="blockId"/> is null.</exception>
    /// <exception cref="ArgumentException">Thrown if <paramref name="blockId"/> has an invalid format.</exception>
    /// <exception cref="FormatException">Thrown if document markers are corrupted or malformed.</exception>
    public static string RemoveBlock(string markdown, string blockId)
    {
        ArgumentNullException.ThrowIfNull(markdown);
        string normalizedId = NormalizeAndValidateBlockId(blockId);

        Dictionary<string, BlockLocation> blocks = ScanDocument(markdown);
        if (!blocks.TryGetValue(normalizedId, out BlockLocation? location))
        {
            return markdown;
        }

        string prefix = markdown[..location.BeginLine.StartIndex];
        string suffix = markdown[location.EndLine.NextLineStartIndex..];

        return prefix + suffix;
    }

    private static string NormalizeAndValidateBlockId(string blockId)
    {
        ArgumentNullException.ThrowIfNull(blockId);

        string normalized = blockId.StartsWith(BrainPrefix, StringComparison.Ordinal)
            ? blockId[BrainPrefix.Length..]
            : blockId;

        if (normalized.Length == 0 || !IsValidId(normalized.AsSpan()))
        {
            throw new ArgumentException(
                $"Block ID '{blockId}' is invalid. The normalized ID must be non-empty and match ^[a-zA-Z0-9_-]+$.",
                nameof(blockId));
        }

        return normalized;
    }

    private static bool IsValidId(ReadOnlySpan<char> idSpan)
    {
        if (idSpan.IsEmpty)
        {
            return false;
        }

        foreach (char c in idSpan)
        {
            bool isAlphaNumOrDash = (c >= 'a' && c <= 'z') ||
                                    (c >= 'A' && c <= 'Z') ||
                                    (c >= '0' && c <= '9') ||
                                    c == '_' ||
                                    c == '-';

            if (!isAlphaNumOrDash)
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, BlockLocation> ScanDocument(string markdown)
    {
        var blocks = new Dictionary<string, BlockLocation>(StringComparer.Ordinal);
        var seenIds = new HashSet<string>(StringComparer.Ordinal);

        bool inFence = false;
        char fenceChar = '\0';
        int fenceLength = 0;

        string? openBlockId = null;
        LineRange? openBlockBeginLine = null;

        List<LineRange> lines = PartitionLines(markdown);

        foreach (LineRange line in lines)
        {
            ReadOnlySpan<char> lineSpan = markdown.AsSpan(line.StartIndex, line.LineEndIndex - line.StartIndex);

            // Strip UTF-8 BOM if present on the very first line of the document
            if (line.StartIndex == 0 && lineSpan.StartsWith("\uFEFF".AsSpan()))
            {
                lineSpan = lineSpan[1..];
            }

            if (inFence)
            {
                if (IsFenceClose(lineSpan, fenceChar, fenceLength))
                {
                    inFence = false;
                }

                continue;
            }

            if (TryMatchFenceOpen(lineSpan, out char fc, out int fl))
            {
                inFence = true;
                fenceChar = fc;
                fenceLength = fl;
                continue;
            }

            if (TryParseMarker(lineSpan, out string markerId, out bool isBegin))
            {
                if (isBegin)
                {
                    if (openBlockId is not null)
                    {
                        throw new FormatException(
                            $"Found nested begin marker for block '{markerId}' while block '{openBlockId}' is still open.");
                    }

                    if (!seenIds.Add(markerId))
                    {
                        throw new FormatException($"Duplicate block '{markerId}' found in markdown document.");
                    }

                    openBlockId = markerId;
                    openBlockBeginLine = line;
                }
                else
                {
                    if (openBlockId is null)
                    {
                        throw new FormatException(
                            $"Found end marker for block '{markerId}' without an opening begin marker.");
                    }

                    if (!string.Equals(openBlockId, markerId, StringComparison.Ordinal))
                    {
                        throw new FormatException(
                            $"Mismatched end marker for block '{markerId}'. Currently open block is '{openBlockId}'.");
                    }

                    blocks[markerId] = new BlockLocation(markerId, openBlockBeginLine!.Value, line);
                    openBlockId = null;
                    openBlockBeginLine = null;
                }
            }
        }

        if (openBlockId is not null)
        {
            throw new FormatException(
                $"Block '{openBlockId}' has a begin marker without a corresponding end marker.");
        }

        return blocks;
    }

    private static bool TryParseMarker(ReadOnlySpan<char> lineSpan, out string markerId, out bool isBegin)
    {
        markerId = string.Empty;
        isBegin = false;

        ReadOnlySpan<char> trimmed = lineSpan.Trim([' ', '\t']);

        if (!trimmed.StartsWith(MarkerPrefix.AsSpan(), StringComparison.Ordinal) ||
            !trimmed.EndsWith(MarkerSuffix.AsSpan(), StringComparison.Ordinal))
        {
            return false;
        }

        ReadOnlySpan<char> middle = trimmed[MarkerPrefix.Length..^MarkerSuffix.Length];

        if (middle.EndsWith(BeginToken.AsSpan(), StringComparison.Ordinal))
        {
            ReadOnlySpan<char> idSpan = middle[..^BeginToken.Length];
            if (!IsValidId(idSpan))
            {
                throw new FormatException($"Malformed brain marker '{trimmed.ToString()}': invalid block ID '{idSpan.ToString()}'.");
            }

            markerId = idSpan.ToString();
            isBegin = true;
            return true;
        }

        if (middle.EndsWith(EndToken.AsSpan(), StringComparison.Ordinal))
        {
            ReadOnlySpan<char> idSpan = middle[..^EndToken.Length];
            if (!IsValidId(idSpan))
            {
                throw new FormatException($"Malformed brain marker '{trimmed.ToString()}': invalid block ID '{idSpan.ToString()}'.");
            }

            markerId = idSpan.ToString();
            isBegin = false;
            return true;
        }

        throw new FormatException($"Malformed brain marker '{trimmed.ToString()}': expected '{BeginToken}' or '{EndToken}'.");
    }

    private static bool TryMatchFenceOpen(ReadOnlySpan<char> line, out char fenceChar, out int fenceLength)
    {
        fenceChar = '\0';
        fenceLength = 0;

        int i = 0;
        while (i < line.Length && i < 3 && line[i] == ' ')
        {
            i++;
        }

        if (i >= line.Length)
        {
            return false;
        }

        char ch = line[i];
        if (ch != '`' && ch != '~')
        {
            return false;
        }

        int count = 0;
        while (i < line.Length && line[i] == ch)
        {
            count++;
            i++;
        }

        if (count < 3)
        {
            return false;
        }

        ReadOnlySpan<char> rest = line[i..];
        if (ch == '`' && rest.Contains('`'))
        {
            return false;
        }

        fenceChar = ch;
        fenceLength = count;
        return true;
    }

    private static bool IsFenceClose(ReadOnlySpan<char> line, char openFenceChar, int openFenceLength)
    {
        int i = 0;
        while (i < line.Length && i < 3 && line[i] == ' ')
        {
            i++;
        }

        if (i >= line.Length || line[i] != openFenceChar)
        {
            return false;
        }

        int count = 0;
        while (i < line.Length && line[i] == openFenceChar)
        {
            count++;
            i++;
        }

        if (count < openFenceLength)
        {
            return false;
        }

        while (i < line.Length)
        {
            if (line[i] != ' ' && line[i] != '\t')
            {
                return false;
            }

            i++;
        }

        return true;
    }

    private static List<LineRange> PartitionLines(string text)
    {
        var lines = new List<LineRange>();
        int length = text.Length;
        int start = 0;
        int i = 0;

        while (i < length)
        {
            if (text[i] == '\r')
            {
                int lineEnd = i;
                if (i + 1 < length && text[i + 1] == '\n')
                {
                    i += 2;
                }
                else
                {
                    i += 1;
                }

                lines.Add(new LineRange(start, lineEnd, i));
                start = i;
            }
            else if (text[i] == '\n')
            {
                int lineEnd = i;
                i += 1;
                lines.Add(new LineRange(start, lineEnd, i));
                start = i;
            }
            else
            {
                i++;
            }
        }

        if (start < length)
        {
            lines.Add(new LineRange(start, length, length));
        }

        return lines;
    }

    private static string DetectDominantEol(string markdown)
    {
        return markdown.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
    }

    private static string NormalizeNewlines(string text, string targetEol)
    {
        string unified = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace("\r", "\n", StringComparison.Ordinal);

        return targetEol == "\n" ? unified : unified.Replace("\n", targetEol, StringComparison.Ordinal);
    }

    private static string DetermineAppendSeparator(string markdown, string eol)
    {
        if (markdown.Length == 0)
        {
            return string.Empty;
        }

        ReadOnlySpan<char> span = markdown.AsSpan();
        if (span.StartsWith("\uFEFF".AsSpan()))
        {
            span = span[1..];
        }

        if (span.Trim().IsEmpty)
        {
            return string.Empty;
        }

        if (markdown.EndsWith("\r\n\r\n", StringComparison.Ordinal) ||
            markdown.EndsWith("\n\n", StringComparison.Ordinal) ||
            markdown.EndsWith("\r\n\n", StringComparison.Ordinal) ||
            markdown.EndsWith("\n\r\n", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (markdown.EndsWith("\r\n", StringComparison.Ordinal) || markdown.EndsWith('\n'))
        {
            int cut = markdown.EndsWith("\r\n", StringComparison.Ordinal) ? 2 : 1;
            ReadOnlySpan<char> beforeFinalNewline = markdown.AsSpan(0, markdown.Length - cut);
            int lastCr = beforeFinalNewline.LastIndexOf('\r');
            int lastLf = beforeFinalNewline.LastIndexOf('\n');
            int prevLineStart = Math.Max(lastCr, lastLf);
            ReadOnlySpan<char> lastLine = prevLineStart >= 0
                ? beforeFinalNewline[(prevLineStart + 1)..]
                : beforeFinalNewline;

            if (lastLine.Trim([' ', '\t']).IsEmpty)
            {
                return string.Empty;
            }

            return eol;
        }

        return eol + eol;
    }

    private readonly record struct LineRange(int StartIndex, int LineEndIndex, int NextLineStartIndex);

    private sealed record BlockLocation(
        string Id,
        LineRange BeginLine,
        LineRange EndLine)
    {
        public int ContentStartIndex => BeginLine.NextLineStartIndex;
        public int ContentEndIndex => EndLine.StartIndex;
    }
}