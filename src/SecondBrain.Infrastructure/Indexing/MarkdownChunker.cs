using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SecondBrain.Core.Model;
using SecondBrain.Core.Recipes;

namespace SecondBrain.Infrastructure.Indexing;

public sealed record NoteChunk(
    string ChunkId,
    string Path,
    string Anchor,
    string HeaderBreadcrumb,
    string Body,
    string BodyNorm,
    string EmbedText);

public static partial class MarkdownChunker
{
    public const string VariantBodyOnly = "body_only";
    public const string VariantTitleHeader = "title_header";
    public const string VariantTitleHeaderTags = "title_header_tags";

    [GeneratedRegex(@"(?<=^|\s)\^(e-[0-9]{4}(?:-[0-9]+)?)(?=$|\s)", RegexOptions.CultureInvariant)]
    private static partial Regex EntryAnchorRegex();

    public static IReadOnlyList<NoteChunk> Chunk(NoteDocument doc, ChunkPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(doc);

        var effectivePolicy = policy ?? new ChunkPolicy();
        if (effectivePolicy.Size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Chunk size must be positive.");
        }

        if (effectivePolicy.Overlap < 0 || effectivePolicy.Overlap >= effectivePolicy.Size)
        {
            throw new ArgumentOutOfRangeException(nameof(policy), "Chunk overlap must be >= 0 and < Size.");
        }

        if (string.IsNullOrWhiteSpace(doc.Body))
        {
            return [];
        }

        var normalizedPath = NormalizePath(doc.Path);
        var title = doc.Metadata.Title;

        if (string.Equals(effectivePolicy.Strategy, "by_header", StringComparison.OrdinalIgnoreCase))
        {
            return ChunkByHeader(normalizedPath, title, doc.Body, effectivePolicy);
        }

        if (string.Equals(effectivePolicy.Strategy, "by_entry", StringComparison.OrdinalIgnoreCase))
        {
            return ChunkByEntry(normalizedPath, title, doc.Body, effectivePolicy);
        }

        throw new NotSupportedException($"Unsupported chunking strategy '{effectivePolicy.Strategy}'.");
    }

    public static string BuildEmbedText(
        string title,
        string headerBreadcrumb,
        string body,
        IReadOnlyList<string>? tags = null,
        string variant = VariantTitleHeader)
    {
        ArgumentNullException.ThrowIfNull(title);
        ArgumentNullException.ThrowIfNull(headerBreadcrumb);
        ArgumentNullException.ThrowIfNull(body);

        return variant switch
        {
            VariantBodyOnly => body,
            VariantTitleHeader => string.IsNullOrWhiteSpace(headerBreadcrumb)
                ? $"{title}\n{body}"
                : $"{title}\n{headerBreadcrumb}\n{body}",
            VariantTitleHeaderTags => BuildTitleHeaderTagsEmbedText(title, headerBreadcrumb, body, tags),
            _ => throw new ArgumentException($"Unsupported embed text variant '{variant}'.", nameof(variant))
        };
    }

    public static string ComputeChunkId(string normalizedPath, string anchor)
    {
        var path = NormalizePath(normalizedPath);
        var bytes = Encoding.UTF8.GetBytes($"{path}#{anchor}");
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    private static string BuildTitleHeaderTagsEmbedText(
        string title,
        string headerBreadcrumb,
        string body,
        IReadOnlyList<string>? tags)
    {
        var baseText = string.IsNullOrWhiteSpace(headerBreadcrumb)
            ? $"{title}\n{body}"
            : $"{title}\n{headerBreadcrumb}\n{body}";

        if (tags is not { Count: > 0 })
        {
            return baseText;
        }

        return $"{baseText}\nTags: {string.Join(", ", tags)}";
    }

    private static List<NoteChunk> ChunkByHeader(
        string normalizedPath,
        string title,
        string rawBody,
        ChunkPolicy policy)
    {
        var lines = SplitNormalizedLines(rawBody);
        var sections = new List<HeaderSection>();

        var stack = new List<(int Level, string Text)>();
        var currentHeaderLine = (string?)null;
        var currentBreadcrumb = string.Empty;
        var currentAnchorBase = "root";
        var currentLines = new List<string>();
        var sectionCounter = 0;

        var inFence = false;
        var fenceChar = '\0';
        var fenceLength = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];

            if (inFence)
            {
                currentLines.Add(line);
                if (IsFenceClose(line.AsSpan(), fenceChar, fenceLength))
                {
                    inFence = false;
                }

                continue;
            }

            if (TryMatchFenceOpen(line.AsSpan(), out var fc, out var fl))
            {
                inFence = true;
                fenceChar = fc;
                fenceLength = fl;
                currentLines.Add(line);
                continue;
            }

            if (TryParseAtxHeader(line.AsSpan(), out var level, out var headerText))
            {
                if (currentHeaderLine is not null || HasNonWhitespace(currentLines))
                {
                    sections.Add(new HeaderSection(
                        currentAnchorBase,
                        currentBreadcrumb,
                        JoinAndTrimLines(currentLines),
                        currentHeaderLine));
                }

                while (stack.Count > 0 && stack[^1].Level >= level)
                {
                    stack.RemoveAt(stack.Count - 1);
                }

                stack.Add((level, headerText));
                sectionCounter++;

                currentHeaderLine = line;
                currentBreadcrumb = BuildBreadcrumb(stack);
                currentAnchorBase = $"h-{sectionCounter}-{Slugify(headerText)}";
                currentLines.Clear();
                continue;
            }

            currentLines.Add(line);
        }

        if (currentHeaderLine is not null || HasNonWhitespace(currentLines))
        {
            sections.Add(new HeaderSection(
                currentAnchorBase,
                currentBreadcrumb,
                JoinAndTrimLines(currentLines),
                currentHeaderLine));
        }

        var nonEmptySections = sections.Where(s => !string.IsNullOrWhiteSpace(s.Body)).ToList();
        if (nonEmptySections.Count == 0)
        {
            // Fallback for notes containing only headers without body lines
            nonEmptySections = sections
                .Where(s => !string.IsNullOrWhiteSpace(s.HeaderLine))
                .Select(s => s with { Body = s.HeaderLine!.Trim() })
                .ToList();
        }

        var chunks = new List<NoteChunk>();
        foreach (var section in nonEmptySections)
        {
            AppendWindowedChunks(
                chunks,
                normalizedPath,
                title,
                section.AnchorBase,
                section.HeaderBreadcrumb,
                section.Body,
                policy);
        }

        return chunks;
    }

    private static List<NoteChunk> ChunkByEntry(
        string normalizedPath,
        string title,
        string rawBody,
        ChunkPolicy policy)
    {
        var lines = SplitNormalizedLines(rawBody);
        var entries = new List<EntrySection>();

        var hasHeaders = ContainsAtxHeaderOutsideFence(lines);
        if (hasHeaders)
        {
            var stack = new List<(int Level, string Text)>();
            var currentHeaderText = string.Empty;
            var currentLines = new List<string>();
            var seenHeader = false;
            var fallbackIndex = 0;

            var inFence = false;
            var fenceChar = '\0';
            var fenceLength = 0;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                if (inFence)
                {
                    currentLines.Add(line);
                    if (IsFenceClose(line.AsSpan(), fenceChar, fenceLength))
                    {
                        inFence = false;
                    }

                    continue;
                }

                if (TryMatchFenceOpen(line.AsSpan(), out var fc, out var fl))
                {
                    inFence = true;
                    fenceChar = fc;
                    fenceLength = fl;
                    currentLines.Add(line);
                    continue;
                }

                if (TryParseAtxHeader(line.AsSpan(), out var level, out var headerText))
                {
                    if (seenHeader || HasNonWhitespace(currentLines))
                    {
                        var bodyText = JoinAndTrimLines(currentLines);
                        if (!string.IsNullOrWhiteSpace(bodyText))
                        {
                            var anchor = ExtractFirstEntryAnchor(bodyText) ?? $"entry-{fallbackIndex}";
                            fallbackIndex++;
                            entries.Add(new EntrySection(anchor, currentHeaderText, bodyText));
                        }
                    }

                    while (stack.Count > 0 && stack[^1].Level >= level)
                    {
                        stack.RemoveAt(stack.Count - 1);
                    }

                    stack.Add((level, headerText));
                    currentHeaderText = BuildBreadcrumb(stack);
                    currentLines.Clear();
                    currentLines.Add(line);
                    seenHeader = true;
                    continue;
                }

                currentLines.Add(line);
            }

            if (seenHeader || HasNonWhitespace(currentLines))
            {
                var bodyText = JoinAndTrimLines(currentLines);
                if (!string.IsNullOrWhiteSpace(bodyText))
                {
                    var anchor = ExtractFirstEntryAnchor(bodyText) ?? $"entry-{fallbackIndex}";
                    entries.Add(new EntrySection(anchor, currentHeaderText, bodyText));
                }
            }
        }
        else
        {
            var currentLines = new List<string>();
            string? pendingAnchor = null;
            var fallbackIndex = 0;

            var inFence = false;
            var fenceChar = '\0';
            var fenceLength = 0;

            for (var i = 0; i < lines.Count; i++)
            {
                var line = lines[i];

                if (inFence)
                {
                    currentLines.Add(line);
                    if (IsFenceClose(line.AsSpan(), fenceChar, fenceLength))
                    {
                        inFence = false;
                    }

                    continue;
                }

                if (TryMatchFenceOpen(line.AsSpan(), out var fc, out var fl))
                {
                    inFence = true;
                    fenceChar = fc;
                    fenceLength = fl;
                    currentLines.Add(line);
                    continue;
                }

                var match = EntryAnchorRegex().Match(line);
                if (match.Success)
                {
                    var matchedAnchor = match.Groups[1].Value;
                    var textBeforeMarker = line[..match.Index];
                    var lineStartsWithMarker = string.IsNullOrWhiteSpace(textBeforeMarker);

                    if (lineStartsWithMarker && !HasNonWhitespace(currentLines) && pendingAnchor is null)
                    {
                        // Leading marker for upcoming entry
                        pendingAnchor = matchedAnchor;
                        currentLines.Add(line);
                    }
                    else if (lineStartsWithMarker && pendingAnchor is not null)
                    {
                        // New leading marker closes the previous entry
                        var prevBody = JoinAndTrimLines(currentLines);
                        if (!string.IsNullOrWhiteSpace(prevBody))
                        {
                            entries.Add(new EntrySection(pendingAnchor, string.Empty, prevBody));
                        }

                        currentLines.Clear();
                        pendingAnchor = matchedAnchor;
                        currentLines.Add(line);
                    }
                    else
                    {
                        // Trailing marker closes the current entry
                        currentLines.Add(line);
                        var entryBody = JoinAndTrimLines(currentLines);
                        if (!string.IsNullOrWhiteSpace(entryBody))
                        {
                            entries.Add(new EntrySection(pendingAnchor ?? matchedAnchor, string.Empty, entryBody));
                        }

                        currentLines.Clear();
                        pendingAnchor = null;
                    }

                    continue;
                }

                currentLines.Add(line);
            }

            if (HasNonWhitespace(currentLines))
            {
                var trailingBody = JoinAndTrimLines(currentLines);
                if (!string.IsNullOrWhiteSpace(trailingBody))
                {
                    entries.Add(new EntrySection(
                        pendingAnchor ?? $"entry-{fallbackIndex}",
                        string.Empty,
                        trailingBody));
                }
            }
        }

        var chunks = new List<NoteChunk>();
        var usedAnchors = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var uniqueAnchor = entry.Anchor;
            if (!usedAnchors.Add(uniqueAnchor))
            {
                var suffix = 1;
                while (!usedAnchors.Add($"{entry.Anchor}-{suffix}"))
                {
                    suffix++;
                }

                uniqueAnchor = $"{entry.Anchor}-{suffix}";
            }

            AppendWindowedChunks(
                chunks,
                normalizedPath,
                title,
                uniqueAnchor,
                entry.HeaderBreadcrumb,
                entry.Body,
                policy);
        }

        return chunks;
    }

    private static void AppendWindowedChunks(
        List<NoteChunk> target,
        string normalizedPath,
        string title,
        string baseAnchor,
        string headerBreadcrumb,
        string body,
        ChunkPolicy policy)
    {
        if (body.Length <= policy.Size)
        {
            target.Add(CreateChunk(normalizedPath, title, baseAnchor, headerBreadcrumb, body));
            return;
        }

        var start = 0;
        var partIndex = 0;

        while (start < body.Length)
        {
            if (body.Length - start <= policy.Size)
            {
                var tailText = body[start..].Trim();
                if (tailText.Length > 0)
                {
                    target.Add(CreateChunk(
                        normalizedPath,
                        title,
                        $"{baseAnchor}-p{partIndex}",
                        headerBreadcrumb,
                        tailText));
                }

                break;
            }

            var rawEnd = start + policy.Size;
            var minEnd = start + policy.Overlap + 1;
            var end = rawEnd;

            var newlinePos = body.LastIndexOf('\n', rawEnd - 1, rawEnd - minEnd + 1);
            if (newlinePos >= minEnd)
            {
                end = newlinePos;
            }
            else
            {
                var wsPos = -1;
                for (var k = rawEnd; k >= minEnd; k--)
                {
                    if (char.IsWhiteSpace(body[k - 1]))
                    {
                        wsPos = k;
                        break;
                    }
                }

                if (wsPos >= minEnd)
                {
                    end = wsPos;
                }
            }

            var windowText = body[start..end].Trim();
            if (windowText.Length > 0)
            {
                target.Add(CreateChunk(
                    normalizedPath,
                    title,
                    $"{baseAnchor}-p{partIndex}",
                    headerBreadcrumb,
                    windowText));
                partIndex++;
            }

            if (end >= body.Length)
            {
                break;
            }

            start = Math.Max(start + 1, end - policy.Overlap);
        }
    }

    private static NoteChunk CreateChunk(
        string normalizedPath,
        string title,
        string anchor,
        string headerBreadcrumb,
        string body)
    {
        var chunkId = ComputeChunkId(normalizedPath, anchor);
        var bodyNorm = RuNormalizer.NormalizeForSearch(body);
        var embedText = BuildEmbedText(title, headerBreadcrumb, body, tags: null, variant: VariantTitleHeader);

        return new NoteChunk(
            ChunkId: chunkId,
            Path: normalizedPath,
            Anchor: anchor,
            HeaderBreadcrumb: headerBreadcrumb,
            Body: body,
            BodyNorm: bodyNorm,
            EmbedText: embedText);
    }

    private static string? ExtractFirstEntryAnchor(string text)
    {
        var lines = SplitNormalizedLines(text);
        var inFence = false;
        var fenceChar = '\0';
        var fenceLength = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            if (inFence)
            {
                if (IsFenceClose(line.AsSpan(), fenceChar, fenceLength))
                {
                    inFence = false;
                }

                continue;
            }

            if (TryMatchFenceOpen(line.AsSpan(), out var fc, out var fl))
            {
                inFence = true;
                fenceChar = fc;
                fenceLength = fl;
                continue;
            }

            var match = EntryAnchorRegex().Match(line);
            if (match.Success)
            {
                return match.Groups[1].Value;
            }
        }

        return null;
    }

    private static bool ContainsAtxHeaderOutsideFence(List<string> lines)
    {
        var inFence = false;
        var fenceChar = '\0';
        var fenceLength = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var span = lines[i].AsSpan();
            if (inFence)
            {
                if (IsFenceClose(span, fenceChar, fenceLength))
                {
                    inFence = false;
                }

                continue;
            }

            if (TryMatchFenceOpen(span, out var fc, out var fl))
            {
                inFence = true;
                fenceChar = fc;
                fenceLength = fl;
                continue;
            }

            if (TryParseAtxHeader(span, out _, out _))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryParseAtxHeader(ReadOnlySpan<char> line, out int level, out string headerText)
    {
        level = 0;
        headerText = string.Empty;

        var i = 0;
        while (i < line.Length && i < 3 && line[i] == ' ')
        {
            i++;
        }

        if (i >= line.Length || line[i] != '#')
        {
            return false;
        }

        var hashes = 0;
        while (i < line.Length && line[i] == '#')
        {
            hashes++;
            i++;
        }

        if (hashes is < 1 or > 6)
        {
            return false;
        }

        if (i < line.Length && line[i] != ' ' && line[i] != '\t')
        {
            return false;
        }

        var rest = line[i..].Trim();
        while (rest.Length > 0 && rest[^1] == '#')
        {
            var beforeHash = rest.Length - 2;
            if (beforeHash < 0 || rest[beforeHash] == ' ' || rest[beforeHash] == '\t')
            {
                rest = rest[..^1].TrimEnd();
            }
            else
            {
                break;
            }
        }

        if (rest.IsEmpty)
        {
            return false;
        }

        level = hashes;
        headerText = new string(rest);
        return true;
    }

    private static bool TryMatchFenceOpen(ReadOnlySpan<char> line, out char fenceChar, out int fenceLength)
    {
        fenceChar = '\0';
        fenceLength = 0;

        var i = 0;
        while (i < line.Length && i < 3 && line[i] == ' ')
        {
            i++;
        }

        if (i >= line.Length)
        {
            return false;
        }

        var ch = line[i];
        if (ch != '`' && ch != '~')
        {
            return false;
        }

        var count = 0;
        while (i < line.Length && line[i] == ch)
        {
            count++;
            i++;
        }

        if (count < 3)
        {
            return false;
        }

        if (ch == '`' && line[i..].Contains('`'))
        {
            return false;
        }

        fenceChar = ch;
        fenceLength = count;
        return true;
    }

    private static bool IsFenceClose(ReadOnlySpan<char> line, char openFenceChar, int openFenceLength)
    {
        var i = 0;
        while (i < line.Length && i < 3 && line[i] == ' ')
        {
            i++;
        }

        if (i >= line.Length || line[i] != openFenceChar)
        {
            return false;
        }

        var count = 0;
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

    private static string BuildBreadcrumb(List<(int Level, string Text)> stack)
        => stack.Count == 0 ? string.Empty : string.Join(" > ", stack.Select(static item => item.Text));

    private static string Slugify(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasDash = false;

        foreach (var rawChar in text)
        {
            var c = char.ToLowerInvariant(rawChar);
            if (c == 'ё')
            {
                c = 'е';
            }

            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash && sb.Length > 0)
            {
                sb.Append('-');
                lastWasDash = true;
            }
        }

        var result = sb.ToString().Trim('-');
        return result.Length == 0 ? "section" : result;
    }

    private static string NormalizePath(string path)
    {
        var unified = path.Replace('\\', '/');
        while (unified.StartsWith("./", StringComparison.Ordinal))
        {
            unified = unified[2..];
        }

        return unified.TrimStart('/');
    }

    private static List<string> SplitNormalizedLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                             .Replace('\r', '\n');
        return [.. normalized.Split('\n')];
    }

    private static bool HasNonWhitespace(List<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (!string.IsNullOrWhiteSpace(lines[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static string JoinAndTrimLines(List<string> lines)
        => string.Join('\n', lines).Trim();

    private sealed record HeaderSection(
        string AnchorBase,
        string HeaderBreadcrumb,
        string Body,
        string? HeaderLine);

    private sealed record EntrySection(
        string Anchor,
        string HeaderBreadcrumb,
        string Body);
}