using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;
using SecondBrain.Infrastructure.Vault;

namespace SecondBrain.Infrastructure.Capture;

public sealed record CaptureExecutionResult(
    bool Applied,
    bool Success,
    string Path,
    string? Diff,
    string? Error,
    NoteDocument Document);

public sealed partial class LocalCaptureService
{
    private const int MaxDerivedTitleLength = 80;

    private readonly IVaultStore _vaultStore;
    private readonly JsonlWriteJournal _journal;
    private readonly IVaultIndex _vaultIndex;
    private readonly TimeProvider _timeProvider;

    [GeneratedRegex(@"(?<=^|\s)\^(e-[0-9]{4}(?:-[0-9]+)?)(?=$|\s)", RegexOptions.CultureInvariant)]
    private static partial Regex DiaryAnchorRegex();

    public LocalCaptureService(
        IVaultStore vaultStore,
        JsonlWriteJournal journal,
        IVaultIndex vaultIndex,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(vaultStore);
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(vaultIndex);

        _vaultStore = vaultStore;
        _journal = journal;
        _vaultIndex = vaultIndex;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<CaptureExecutionResult> CaptureAsync(
        string rawText,
        string? titleOverride = null,
        bool apply = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(rawText);
        ct.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var parsed = DirectiveParser.Parse(rawText);
        var directives = parsed.Directives;

        var noteType = ResolveNoteType(directives.NoteType);
        var folder = ResolveFolder(directives.Folder, noteType);
        var title = ResolveTitle(titleOverride, parsed.CleanBody);

        var existingEntries = await _vaultStore.ListAsync($"{folder}/", ct).ConfigureAwait(false);
        var existingSlugs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var folderPrefix = $"{folder}/";

        foreach (var entry in existingEntries)
        {
            if (!entry.Path.StartsWith(folderPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var remainder = entry.Path[folderPrefix.Length..];
            if (!remainder.Contains('/') && remainder.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            {
                existingSlugs.Add(Path.GetFileNameWithoutExtension(remainder));
            }
        }

        var slug = Slugger.ToUniqueSlug(title, existingSlugs);
        var path = $"{folder}/{slug}.md";
        var id = BuildDeterministicId(title, now);

        var doc = new NoteDocument(
            Path: path,
            Metadata: new NoteMetadata
            {
                Id = id,
                Title = title,
                Type = noteType,
                Tags = directives.Tags,
                Aliases = directives.Aliases,
                Up = directives.Maps,
                SourceUrl = directives.SourceUrl,
                Created = now,
                Updated = now
            },
            Body: parsed.CleanBody,
            ContentHash: string.Empty,
            FileModifiedAt: now);

        if (!apply)
        {
            var dryResult = await _vaultStore.WriteAsync(
                doc,
                new WriteOptions(ExpectedHash: string.Empty, DryRun: true, Reason: "capture:dry-run"),
                ct).ConfigureAwait(false);

            return new CaptureExecutionResult(
                Applied: false,
                Success: dryResult.Success,
                Path: path,
                Diff: dryResult.Diff,
                Error: dryResult.Error,
                Document: doc);
        }

        var serialized = FrontmatterCodec.Serialize(doc);
        var journalEntry = await _journal.BeginAsync(
            new WriteOperation("create", path, string.Empty, serialized, "capture"),
            ct).ConfigureAwait(false);

        try
        {
            var writeResult = await _vaultStore.WriteAsync(
                doc,
                new WriteOptions(ExpectedHash: string.Empty, DryRun: false, Reason: "capture"),
                ct).ConfigureAwait(false);

            if (!writeResult.Success)
            {
                await _journal.RollbackAsync(journalEntry.OperationId, CancellationToken.None).ConfigureAwait(false);
                return new CaptureExecutionResult(
                    Applied: false,
                    Success: false,
                    Path: path,
                    Diff: writeResult.Diff,
                    Error: writeResult.Error,
                    Document: doc);
            }

            var savedDoc = doc with
            {
                ContentHash = writeResult.Hash ?? string.Empty,
                FileModifiedAt = now
            };

            await _journal.CommitAsync(journalEntry.OperationId, writeResult, ct).ConfigureAwait(false);
            await _vaultIndex.UpsertAsync(savedDoc, ct).ConfigureAwait(false);

            return new CaptureExecutionResult(
                Applied: true,
                Success: true,
                Path: path,
                Diff: writeResult.Diff,
                Error: null,
                Document: savedDoc);
        }
        catch (Exception ex)
        {
            await _journal.RollbackAsync(journalEntry.OperationId, CancellationToken.None).ConfigureAwait(false);
            return new CaptureExecutionResult(
                Applied: false,
                Success: false,
                Path: path,
                Diff: null,
                Error: ex.Message,
                Document: doc);
        }
    }

    public async Task<CaptureExecutionResult> AppendDiaryAsync(
        string entryText,
        bool apply = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(entryText);
        ct.ThrowIfCancellationRequested();

        var now = _timeProvider.GetUtcNow();
        var dateStamp = now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var path = $"20_Areas/Diary/{dateStamp}.md";

        var existing = await _vaultStore.ReadAsync(path, ct).ConfigureAwait(false);
        var baseAnchor = $"e-{now.ToString("HHmm", CultureInfo.InvariantCulture)}";
        var anchor = ResolveUniqueDiaryAnchor(existing?.Body, baseAnchor);
        var timeDisplay = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        var trimmedEntry = entryText.Trim();

        NoteDocument targetDoc;
        string expectedHash;
        string opKind;

        if (existing is null)
        {
            var body = $"## {timeDisplay} ^{anchor}\n\n{trimmedEntry}";
            targetDoc = new NoteDocument(
                Path: path,
                Metadata: new NoteMetadata
                {
                    Id = $"diary-{dateStamp}",
                    Title = $"Diary {dateStamp}",
                    Type = NoteType.Diary,
                    Usecase = "diary",
                    Created = now,
                    Updated = now
                },
                Body: body,
                ContentHash: string.Empty,
                FileModifiedAt: now);
            expectedHash = string.Empty;
            opKind = "create";
        }
        else
        {
            var entryBlock = $"\n## {timeDisplay} ^{anchor}\n\n{trimmedEntry}";
            var updatedBody = string.IsNullOrWhiteSpace(existing.Body)
                ? $"## {timeDisplay} ^{anchor}\n\n{trimmedEntry}"
                : existing.Body.EndsWith('\n')
                    ? existing.Body + entryBlock
                    : existing.Body + "\n" + entryBlock;

            targetDoc = existing with
            {
                Metadata = existing.Metadata with { Updated = now },
                Body = updatedBody,
                FileModifiedAt = now
            };
            expectedHash = existing.ContentHash;
            opKind = "append_entry";
        }

        if (!apply)
        {
            var dryResult = await _vaultStore.WriteAsync(
                targetDoc,
                new WriteOptions(ExpectedHash: expectedHash, DryRun: true, Reason: "diary:dry-run"),
                ct).ConfigureAwait(false);

            return new CaptureExecutionResult(
                Applied: false,
                Success: dryResult.Success,
                Path: path,
                Diff: dryResult.Diff,
                Error: dryResult.Error,
                Document: targetDoc);
        }

        var serialized = FrontmatterCodec.Serialize(targetDoc);
        var journalEntry = await _journal.BeginAsync(
            new WriteOperation(opKind, path, expectedHash, serialized, "diary:append"),
            ct).ConfigureAwait(false);

        try
        {
            var writeResult = await _vaultStore.WriteAsync(
                targetDoc,
                new WriteOptions(ExpectedHash: expectedHash, DryRun: false, Reason: "diary:append"),
                ct).ConfigureAwait(false);

            if (!writeResult.Success)
            {
                await _journal.RollbackAsync(journalEntry.OperationId, CancellationToken.None).ConfigureAwait(false);
                return new CaptureExecutionResult(
                    Applied: false,
                    Success: false,
                    Path: path,
                    Diff: writeResult.Diff,
                    Error: writeResult.Error,
                    Document: targetDoc);
            }

            var savedDoc = targetDoc with
            {
                ContentHash = writeResult.Hash ?? string.Empty,
                FileModifiedAt = now
            };

            await _journal.CommitAsync(journalEntry.OperationId, writeResult, ct).ConfigureAwait(false);
            await _vaultIndex.UpsertAsync(savedDoc, ct).ConfigureAwait(false);

            return new CaptureExecutionResult(
                Applied: true,
                Success: true,
                Path: path,
                Diff: writeResult.Diff,
                Error: null,
                Document: savedDoc);
        }
        catch (Exception ex)
        {
            await _journal.RollbackAsync(journalEntry.OperationId, CancellationToken.None).ConfigureAwait(false);
            return new CaptureExecutionResult(
                Applied: false,
                Success: false,
                Path: path,
                Diff: null,
                Error: ex.Message,
                Document: targetDoc);
        }
    }

    private static NoteType ResolveNoteType(string? rawType)
    {
        if (string.IsNullOrWhiteSpace(rawType))
        {
            return NoteType.Fleeting;
        }

        var normalized = rawType.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
        return Enum.TryParse<NoteType>(normalized, ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed
            : NoteType.Fleeting;
    }

    private static string ResolveFolder(string? directiveFolder, NoteType noteType)
    {
        if (!string.IsNullOrWhiteSpace(directiveFolder))
        {
            var cleaned = directiveFolder.Replace('\\', '/').Trim('/');
            if (cleaned.Length > 0)
            {
                return cleaned;
            }
        }

        return noteType switch
        {
            NoteType.Decision => "80_Decisions",
            NoteType.Insight => "40_Insights",
            NoteType.Project => "10_Projects",
            NoteType.Task => "60_Tasks",
            NoteType.Meeting => "70_Meetings",
            NoteType.Person => "50_People",
            NoteType.Resource => "30_Resources",
            NoteType.Review => "90_Meta/Reviews",
            NoteType.Diary => "20_Areas/Diary",
            NoteType.Map => "90_Meta/Maps",
            NoteType.Fleeting => "00_Inbox/Raw",
            _ => "00_Inbox/Raw"
        };
    }

    private static string ResolveTitle(string? titleOverride, string cleanBody)
    {
        if (!string.IsNullOrWhiteSpace(titleOverride))
        {
            return titleOverride.Trim();
        }

        if (string.IsNullOrWhiteSpace(cleanBody))
        {
            return "Untitled Note";
        }

        var lines = cleanBody.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        string? firstNonEmptyLine = null;

        for (var i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            firstNonEmptyLine ??= trimmed;

            if (TryExtractH1Heading(trimmed, out var headingTitle))
            {
                return headingTitle;
            }
        }

        if (firstNonEmptyLine is null)
        {
            return "Untitled Note";
        }

        return firstNonEmptyLine.Length <= MaxDerivedTitleLength
            ? firstNonEmptyLine
            : firstNonEmptyLine[..MaxDerivedTitleLength].TrimEnd();
    }

    private static bool TryExtractH1Heading(string trimmedLine, out string headingTitle)
    {
        headingTitle = string.Empty;
        if (!trimmedLine.StartsWith('#'))
        {
            return false;
        }

        if (trimmedLine.Length < 2 || trimmedLine[1] is not (' ' or '\t'))
        {
            return false;
        }

        var text = trimmedLine[2..].Trim();
        if (text.Length == 0)
        {
            return false;
        }

        headingTitle = text;
        return true;
    }

    private static string BuildDeterministicId(string title, DateTimeOffset now)
    {
        var input = $"{title}\n{now.ToString("O", CultureInfo.InvariantCulture)}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var shortHex = Convert.ToHexStringLower(hash)[..4];
        return $"n-{now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture)}-{shortHex}";
    }

    private static string ResolveUniqueDiaryAnchor(string? existingBody, string baseAnchor)
    {
        if (string.IsNullOrWhiteSpace(existingBody))
        {
            return baseAnchor;
        }

        var existingAnchors = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in DiaryAnchorRegex().Matches(existingBody))
        {
            if (match.Success)
            {
                existingAnchors.Add(match.Groups[1].Value);
            }
        }

        if (!existingAnchors.Contains(baseAnchor))
        {
            return baseAnchor;
        }

        for (var suffix = 2; ; suffix++)
        {
            var candidate = $"{baseAnchor}-{suffix.ToString(CultureInfo.InvariantCulture)}";
            if (!existingAnchors.Contains(candidate))
            {
                return candidate;
            }
        }
    }
}