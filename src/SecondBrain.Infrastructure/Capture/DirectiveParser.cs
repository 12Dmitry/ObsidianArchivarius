using System.Text;
using SecondBrain.Core.Ports;

namespace SecondBrain.Infrastructure.Capture;

public sealed record ParsedDirectives(Directives Directives, string CleanBody);

public static class DirectiveParser
{
    public static ParsedDirectives Parse(string? rawText)
    {
        if (string.IsNullOrEmpty(rawText))
        {
            return new ParsedDirectives(
                new Directives(null, null, [], [], [], null),
                string.Empty);
        }

        string? folder = null;
        string? noteType = null;
        string? sourceUrl = null;

        var tags = new List<string>();
        var tagSet = new HashSet<string>(StringComparer.Ordinal);
        var maps = new List<string>();
        var mapSet = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new List<string>();
        var aliasSet = new HashSet<string>(StringComparer.Ordinal);

        var normalizedInput = rawText.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var lines = normalizedInput.Split('\n');
        var bodyLines = new List<string>(lines.Length);

        var inFence = false;
        var fenceChar = '\0';
        var fenceLength = 0;

        for (var lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var lineSpan = line.AsSpan();

            if (inFence)
            {
                bodyLines.Add(line);
                if (IsFenceClose(lineSpan, fenceChar, fenceLength))
                {
                    inFence = false;
                }

                continue;
            }

            if (TryMatchFenceOpen(lineSpan, out var fc, out var fl))
            {
                inFence = true;
                fenceChar = fc;
                fenceLength = fl;
                bodyLines.Add(line);
                continue;
            }

            if (!line.Contains(">>", StringComparison.Ordinal))
            {
                bodyLines.Add(line);
                continue;
            }

            var directivePositions = FindDirectivePositions(line);
            var rebuilt = new StringBuilder(line.Length);
            if (directivePositions[0] > 0)
            {
                rebuilt.Append(line, 0, directivePositions[0]);
            }

            var hadRecognizedDirective = false;

            for (var i = 0; i < directivePositions.Count; i++)
            {
                var start = directivePositions[i];
                var end = i + 1 < directivePositions.Count ? directivePositions[i + 1] : line.Length;
                var rawSegment = line[start..end];
                var content = rawSegment[2..].Trim();

                if (TryApplyDirective(
                    content,
                    ref folder,
                    ref noteType,
                    ref sourceUrl,
                    tags,
                    tagSet,
                    maps,
                    mapSet,
                    aliases,
                    aliasSet))
                {
                    hadRecognizedDirective = true;
                }
                else
                {
                    rebuilt.Append(rawSegment);
                }
            }

            if (!hadRecognizedDirective)
            {
                bodyLines.Add(line);
                continue;
            }

            var cleanedLine = rebuilt.ToString().TrimEnd();
            if (!string.IsNullOrWhiteSpace(cleanedLine))
            {
                bodyLines.Add(cleanedLine);
            }
        }

        var cleanBody = string.Join('\n', bodyLines).Trim();
        var directives = new Directives(
            Folder: folder,
            NoteType: noteType,
            Tags: [.. tags],
            Maps: [.. maps],
            Aliases: [.. aliases],
            SourceUrl: sourceUrl);

        return new ParsedDirectives(directives, cleanBody);
    }

    private static List<int> FindDirectivePositions(string line)
    {
        var positions = new List<int>();
        var index = 0;

        while (true)
        {
            var found = line.IndexOf(">>", index, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            positions.Add(found);
            index = found + 2;
        }

        return positions;
    }

    private static bool TryApplyDirective(
        string content,
        ref string? folder,
        ref string? noteType,
        ref string? sourceUrl,
        List<string> tags,
        HashSet<string> tagSet,
        List<string> maps,
        HashSet<string> mapSet,
        List<string> aliases,
        HashSet<string> aliasSet)
    {
        if (content.Length == 0)
        {
            return false;
        }

        if (content.StartsWith('#') && content.Length > 1 && !char.IsWhiteSpace(content[1]))
        {
            var normalizedTag = content.TrimStart('#').Trim().ToLowerInvariant();
            if (normalizedTag.Length > 0 && tagSet.Add(normalizedTag))
            {
                tags.Add(normalizedTag);
            }

            return normalizedTag.Length > 0;
        }

        var colonIndex = content.IndexOf(':');
        if (colonIndex <= 0)
        {
            return false;
        }

        var key = content[..colonIndex].Trim().ToLowerInvariant();
        var value = content[(colonIndex + 1)..].Trim();

        if (value.Length == 0)
        {
            return false;
        }

        switch (key)
        {
            case "folder":
                var normalizedFolder = value.Replace('\\', '/').Trim('/');
                if (normalizedFolder.Length == 0)
                {
                    return false;
                }

                folder = normalizedFolder;
                return true;

            case "type":
                noteType = value;
                return true;

            case "tag":
                var normalizedTag = value.TrimStart('#').Trim().ToLowerInvariant();
                if (normalizedTag.Length == 0)
                {
                    return false;
                }

                if (tagSet.Add(normalizedTag))
                {
                    tags.Add(normalizedTag);
                }

                return true;

            case "map":
                if (mapSet.Add(value))
                {
                    maps.Add(value);
                }

                return true;

            case "alias":
                if (aliasSet.Add(value))
                {
                    aliases.Add(value);
                }

                return true;

            case "url":
            case "source_url":
                sourceUrl = value;
                return true;

            default:
                return false;
        }
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
}