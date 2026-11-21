using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;
using SecondBrain.Infrastructure.Capture;
using SecondBrain.Infrastructure.Indexing;
using SecondBrain.Infrastructure.Vault;

namespace SecondBrain.Cli;

public sealed partial class CliApp(TextWriter stdout, TextWriter stderr, TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly TextWriter _stdout = stdout ?? throw new ArgumentNullException(nameof(stdout));
    private readonly TextWriter _stderr = stderr ?? throw new ArgumentNullException(nameof(stderr));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    [GeneratedRegex(@"(?<=^|\s)\^e-[A-Za-z0-9-]+(?=$|\s)", RegexOptions.CultureInvariant)]
    private static partial Regex DiaryAnchorRegex();

    public async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(args);
        ct.ThrowIfCancellationRequested();

        if (args.Length == 0)
        {
            await WriteHelpAsync(ct).ConfigureAwait(false);
            return 0;
        }

        string? command = null;
        string? vaultOption = null;
        var vaultProvided = false;
        var json = false;
        var helpRequested = false;
        var remainingStartIndex = args.Length;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (arg is "--help" or "-h")
            {
                helpRequested = true;
                continue;
            }

            if (TryParseGlobalFlag(args, ref i, ref vaultOption, ref vaultProvided, ref json, out var flagError))
            {
                if (flagError is not null)
                {
                    await _stderr.WriteLineAsync(flagError.AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                continue;
            }

            if (arg == "--")
            {
                if (i + 1 < args.Length)
                {
                    command = args[i + 1];
                    remainingStartIndex = i + 2;
                }

                break;
            }

            if (arg.StartsWith('-') && arg.Length > 1)
            {
                await _stderr.WriteLineAsync($"Unknown option '{arg}'.".AsMemory(), ct).ConfigureAwait(false);
                return 1;
            }

            command = arg;
            remainingStartIndex = i + 1;
            break;
        }

        if (string.Equals(command, "help", StringComparison.OrdinalIgnoreCase) ||
            (command is null && helpRequested))
        {
            await WriteHelpAsync(ct).ConfigureAwait(false);
            return 0;
        }

        if (command is null)
        {
            await _stderr.WriteLineAsync("Missing command. Run 'help' to see available commands.".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        return command.ToLowerInvariant() switch
        {
            "doctor" => await RunDoctorAsync(args, remainingStartIndex, vaultOption, vaultProvided, json, helpRequested, ct).ConfigureAwait(false),
            "capture" => await RunCaptureAsync(args, remainingStartIndex, vaultOption, vaultProvided, json, helpRequested, ct).ConfigureAwait(false),
            "diary" => await RunDiaryAsync(args, remainingStartIndex, vaultOption, vaultProvided, json, helpRequested, ct).ConfigureAwait(false),
            "search" => await RunSearchAsync(args, remainingStartIndex, vaultOption, vaultProvided, json, helpRequested, ct).ConfigureAwait(false),
            "show" => await RunShowAsync(args, remainingStartIndex, vaultOption, vaultProvided, json, helpRequested, ct).ConfigureAwait(false),
            "rebuild-index" => await RunRebuildIndexAsync(args, remainingStartIndex, vaultOption, vaultProvided, json, helpRequested, ct).ConfigureAwait(false),
            _ => await UnknownCommandAsync(command, ct).ConfigureAwait(false)
        };
    }

    private async Task<int> RunDoctorAsync(
        string[] args,
        int startIndex,
        string? vaultOption,
        bool vaultProvided,
        bool json,
        bool helpRequested,
        CancellationToken ct)
    {
        var endOfOptions = false;
        for (var i = startIndex; i < args.Length; i++)
        {
            var arg = args[i];
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && arg is "--help" or "-h")
            {
                helpRequested = true;
                continue;
            }

            if (!endOfOptions && TryParseGlobalFlag(args, ref i, ref vaultOption, ref vaultProvided, ref json, out var flagError))
            {
                if (flagError is not null)
                {
                    await _stderr.WriteLineAsync(flagError.AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                continue;
            }

            if (!endOfOptions && arg.StartsWith('-') && arg.Length > 1)
            {
                await _stderr.WriteLineAsync($"Unknown option '{arg}' for 'doctor'.".AsMemory(), ct).ConfigureAwait(false);
                return 1;
            }

            await _stderr.WriteLineAsync($"Unexpected argument '{arg}' for 'doctor'.".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        if (helpRequested)
        {
            await WriteHelpAsync(ct).ConfigureAwait(false);
            return 0;
        }

        if (!TryResolveVaultPath(vaultOption, vaultProvided, out var fullVaultPath, out var pathError))
        {
            await _stderr.WriteLineAsync((pathError ?? "Invalid vault path.").AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        if (!Directory.Exists(fullVaultPath))
        {
            await _stderr.WriteLineAsync($"Vault directory does not exist: {fullVaultPath}".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        var vaultStore = new FileSystemVaultStore(fullVaultPath);
        using var index = new SqliteVaultIndex(fullVaultPath, vaultStore, _timeProvider);
        await index.EnsureInitializedAsync(ct).ConfigureAwait(false);

        var entries = await vaultStore.ListAsync(ct: ct).ConfigureAwait(false);
        var notesCount = entries.Count(static e => e.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase));
        var stats = await index.GetStatsAsync(ct).ConfigureAwait(false);

        if (json)
        {
            await WriteJsonAsync(
                new DoctorOutput(
                    VaultPath: fullVaultPath,
                    NotesCount: notesCount,
                    Chunks: stats.Chunks,
                    Vectors: stats.Vectors,
                    EmbeddingModel: stats.EmbeddingModel),
                ct).ConfigureAwait(false);
        }
        else
        {
            await _stdout.WriteLineAsync($"VaultPath: {fullVaultPath}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync($"NotesCount: {notesCount.ToString(CultureInfo.InvariantCulture)}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync($"Chunks: {stats.Chunks.ToString(CultureInfo.InvariantCulture)}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync($"Vectors: {stats.Vectors.ToString(CultureInfo.InvariantCulture)}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync($"EmbeddingModel: {stats.EmbeddingModel}".AsMemory(), ct).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> RunCaptureAsync(
        string[] args,
        int startIndex,
        string? vaultOption,
        bool vaultProvided,
        bool json,
        bool helpRequested,
        CancellationToken ct)
    {
        string? title = null;
        var hasApply = false;
        var hasDryRun = false;
        var endOfOptions = false;
        var textParts = new List<string>();

        for (var i = startIndex; i < args.Length; i++)
        {
            var arg = args[i];
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && arg is "--help" or "-h")
            {
                helpRequested = true;
                continue;
            }

            if (!endOfOptions && TryParseGlobalFlag(args, ref i, ref vaultOption, ref vaultProvided, ref json, out var flagError))
            {
                if (flagError is not null)
                {
                    await _stderr.WriteLineAsync(flagError.AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                continue;
            }

            if (!endOfOptions && TryParseValueFlag(args, ref i, "--title", out var titleValue, out var titleError))
            {
                if (titleError is not null)
                {
                    await _stderr.WriteLineAsync(titleError.AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                title = titleValue;
                continue;
            }

            if (!endOfOptions && arg == "--apply")
            {
                hasApply = true;
                continue;
            }

            if (!endOfOptions && arg == "--dry-run")
            {
                hasDryRun = true;
                continue;
            }

            if (!endOfOptions && arg.StartsWith('-') && arg.Length > 1)
            {
                await _stderr.WriteLineAsync($"Unknown option '{arg}' for 'capture'.".AsMemory(), ct).ConfigureAwait(false);
                return 1;
            }

            textParts.Add(arg);
        }

        if (helpRequested)
        {
            await WriteHelpAsync(ct).ConfigureAwait(false);
            return 0;
        }

        var rawText = string.Join(' ', textParts);
        if (string.IsNullOrWhiteSpace(rawText))
        {
            await _stderr.WriteLineAsync("Missing required argument <text> for 'capture'.".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        if (!TryResolveVaultPath(vaultOption, vaultProvided, out var fullVaultPath, out var pathError))
        {
            await _stderr.WriteLineAsync((pathError ?? "Invalid vault path.").AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        var apply = hasApply && !hasDryRun;
        var vaultStore = new FileSystemVaultStore(fullVaultPath);
        var journal = new JsonlWriteJournal(fullVaultPath, _timeProvider);
        using var index = new SqliteVaultIndex(fullVaultPath, vaultStore, _timeProvider);
        var captureService = new LocalCaptureService(vaultStore, journal, index, _timeProvider);

        var result = await captureService.CaptureAsync(rawText, title, apply, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            await _stderr.WriteLineAsync((result.Error ?? "Capture failed.").AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        await WriteCaptureResultAsync(result, json, ct).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RunDiaryAsync(
        string[] args,
        int startIndex,
        string? vaultOption,
        bool vaultProvided,
        bool json,
        bool helpRequested,
        CancellationToken ct)
    {
        var statsMode = false;
        var hasDryRun = false;
        var endOfOptions = false;
        var textParts = new List<string>();

        for (var i = startIndex; i < args.Length; i++)
        {
            var arg = args[i];
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && arg is "--help" or "-h")
            {
                helpRequested = true;
                continue;
            }

            if (!endOfOptions && TryParseGlobalFlag(args, ref i, ref vaultOption, ref vaultProvided, ref json, out var flagError))
            {
                if (flagError is not null)
                {
                    await _stderr.WriteLineAsync(flagError.AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                continue;
            }

            if (!endOfOptions && arg == "--stats")
            {
                statsMode = true;
                continue;
            }

            if (!endOfOptions && arg == "--apply")
            {
                continue;
            }

            if (!endOfOptions && arg == "--dry-run")
            {
                hasDryRun = true;
                continue;
            }

            if (!endOfOptions && arg.StartsWith('-') && arg.Length > 1)
            {
                await _stderr.WriteLineAsync($"Unknown option '{arg}' for 'diary'.".AsMemory(), ct).ConfigureAwait(false);
                return 1;
            }

            textParts.Add(arg);
        }

        if (helpRequested)
        {
            await WriteHelpAsync(ct).ConfigureAwait(false);
            return 0;
        }

        if (!TryResolveVaultPath(vaultOption, vaultProvided, out var fullVaultPath, out var pathError))
        {
            await _stderr.WriteLineAsync((pathError ?? "Invalid vault path.").AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        if (statsMode)
        {
            if (textParts.Count > 0)
            {
                await _stderr.WriteLineAsync($"Unexpected argument '{textParts[0]}' for 'diary --stats'.".AsMemory(), ct).ConfigureAwait(false);
                return 1;
            }

            if (!Directory.Exists(fullVaultPath))
            {
                await _stderr.WriteLineAsync($"Vault directory does not exist: {fullVaultPath}".AsMemory(), ct).ConfigureAwait(false);
                return 1;
            }

            var statsVaultStore = new FileSystemVaultStore(fullVaultPath);
            var entries = await statsVaultStore.ListAsync("20_Areas/Diary", ct).ConfigureAwait(false);
            var diaryFiles = entries
                .Where(static e =>
                    e.Path.StartsWith("20_Areas/Diary/", StringComparison.OrdinalIgnoreCase) &&
                    e.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var totalEntries = 0;
            foreach (var entry in diaryFiles)
            {
                var doc = await statsVaultStore.ReadAsync(entry.Path, ct).ConfigureAwait(false);
                if (doc is not null && !string.IsNullOrEmpty(doc.Body))
                {
                    totalEntries += DiaryAnchorRegex().Count(doc.Body);
                }
            }

            if (json)
            {
                await WriteJsonAsync(
                    new DiaryStatsOutput(FilesCount: diaryFiles.Count, EntriesCount: totalEntries),
                    ct).ConfigureAwait(false);
            }
            else
            {
                await _stdout.WriteLineAsync($"FilesCount: {diaryFiles.Count.ToString(CultureInfo.InvariantCulture)}".AsMemory(), ct).ConfigureAwait(false);
                await _stdout.WriteLineAsync($"EntriesCount: {totalEntries.ToString(CultureInfo.InvariantCulture)}".AsMemory(), ct).ConfigureAwait(false);
            }

            return 0;
        }

        var entryText = string.Join(' ', textParts);
        if (string.IsNullOrWhiteSpace(entryText))
        {
            await _stderr.WriteLineAsync("Missing required argument <text> for 'diary'.".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        var apply = !hasDryRun;
        var vaultStore = new FileSystemVaultStore(fullVaultPath);
        var journal = new JsonlWriteJournal(fullVaultPath, _timeProvider);
        using var index = new SqliteVaultIndex(fullVaultPath, vaultStore, _timeProvider);
        var captureService = new LocalCaptureService(vaultStore, journal, index, _timeProvider);

        var result = await captureService.AppendDiaryAsync(entryText, apply, ct).ConfigureAwait(false);
        if (!result.Success)
        {
            await _stderr.WriteLineAsync((result.Error ?? "Diary append failed.").AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        await WriteCaptureResultAsync(result, json, ct).ConfigureAwait(false);
        return 0;
    }

    private async Task<int> RunSearchAsync(
        string[] args,
        int startIndex,
        string? vaultOption,
        bool vaultProvided,
        bool json,
        bool helpRequested,
        CancellationToken ct)
    {
        var limit = 8;
        string[]? tags = null;
        NoteType? noteType = null;
        string? prefix = null;
        var endOfOptions = false;
        var queryParts = new List<string>();

        for (var i = startIndex; i < args.Length; i++)
        {
            var arg = args[i];
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && arg is "--help" or "-h")
            {
                helpRequested = true;
                continue;
            }

            if (!endOfOptions && TryParseGlobalFlag(args, ref i, ref vaultOption, ref vaultProvided, ref json, out var flagError))
            {
                if (flagError is not null)
                {
                    await _stderr.WriteLineAsync(flagError.AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                continue;
            }

            if (!endOfOptions && TryParseValueFlag(args, ref i, "--limit", out var limitVal, out var limitErr))
            {
                if (limitErr is not null)
                {
                    await _stderr.WriteLineAsync(limitErr.AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                if (!int.TryParse(limitVal, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedLimit) || parsedLimit <= 0)
                {
                    await _stderr.WriteLineAsync($"Invalid positive integer '{limitVal}' for '--limit'.".AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                limit = parsedLimit;
                continue;
            }

            if (!endOfOptions && TryParseValueFlag(args, ref i, "--tags", out var tagsVal, out var tagsErr))
            {
                if (tagsErr is not null)
                {
                    await _stderr.WriteLineAsync(tagsErr.AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                var parsedTags = tagsVal
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                    .Select(static t => t.TrimStart('#'))
                    .Where(static t => t.Length > 0)
                    .ToArray();
                tags = parsedTags.Length > 0 ? parsedTags : null;
                continue;
            }

            if (!endOfOptions && TryParseValueFlag(args, ref i, "--type", out var typeVal, out var typeErr))
            {
                if (typeErr is not null)
                {
                    await _stderr.WriteLineAsync(typeErr.AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                var normalizedType = typeVal.Trim().Replace("_", string.Empty, StringComparison.Ordinal);
                if (!Enum.TryParse<NoteType>(normalizedType, ignoreCase: true, out var parsedType) || !Enum.IsDefined(parsedType))
                {
                    await _stderr.WriteLineAsync($"Invalid NoteType '{typeVal}' for '--type'.".AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                noteType = parsedType;
                continue;
            }

            if (!endOfOptions && TryParseValueFlag(args, ref i, "--prefix", out var prefixVal, out var prefixErr))
            {
                if (prefixErr is not null)
                {
                    await _stderr.WriteLineAsync(prefixErr.AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                prefix = string.IsNullOrWhiteSpace(prefixVal) ? null : prefixVal;
                continue;
            }

            if (!endOfOptions && arg.StartsWith('-') && arg.Length > 1)
            {
                await _stderr.WriteLineAsync($"Unknown option '{arg}' for 'search'.".AsMemory(), ct).ConfigureAwait(false);
                return 1;
            }

            queryParts.Add(arg);
        }

        if (helpRequested)
        {
            await WriteHelpAsync(ct).ConfigureAwait(false);
            return 0;
        }

        var queryText = string.Join(' ', queryParts);
        if (string.IsNullOrWhiteSpace(queryText))
        {
            await _stderr.WriteLineAsync("Missing required argument <query> for 'search'.".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        if (!TryResolveVaultPath(vaultOption, vaultProvided, out var fullVaultPath, out var pathError))
        {
            await _stderr.WriteLineAsync((pathError ?? "Invalid vault path.").AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        if (!Directory.Exists(fullVaultPath))
        {
            await _stderr.WriteLineAsync($"Vault directory does not exist: {fullVaultPath}".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        var vaultStore = new FileSystemVaultStore(fullVaultPath);
        using var index = new SqliteVaultIndex(fullVaultPath, vaultStore, _timeProvider);
        await index.EnsureInitializedAsync(ct).ConfigureAwait(false);

        var result = await index.SearchAsync(
            new SearchQuery(
                Text: queryText,
                Limit: limit,
                Tags: tags,
                PathPrefix: prefix,
                Type: noteType),
            ct).ConfigureAwait(false);

        if (json)
        {
            await WriteJsonAsync(
                new SearchOutput(
                    Query: queryText,
                    Hits: result.Hits,
                    LexicalMs: result.Timing.LexicalMs,
                    VectorMs: result.Timing.VectorMs,
                    TotalMs: result.Timing.TotalMs),
                ct).ConfigureAwait(false);
        }
        else
        {
            if (result.Hits.Count == 0)
            {
                await _stdout.WriteLineAsync("No results found.".AsMemory(), ct).ConfigureAwait(false);
            }
            else
            {
                for (var i = 0; i < result.Hits.Count; i++)
                {
                    var hit = result.Hits[i];
                    var rank = i + 1;
                    var scoreText = hit.Score.ToString("0.000", CultureInfo.InvariantCulture);
                    await _stdout.WriteLineAsync($"#{rank.ToString(CultureInfo.InvariantCulture)} [{scoreText}] {hit.Path} | {hit.Title}".AsMemory(), ct).ConfigureAwait(false);

                    if (!string.IsNullOrWhiteSpace(hit.Header))
                    {
                        await _stdout.WriteLineAsync($"  header: {hit.Header}".AsMemory(), ct).ConfigureAwait(false);
                    }

                    if (!string.IsNullOrWhiteSpace(hit.Snippet))
                    {
                        await _stdout.WriteLineAsync($"  snippet: {hit.Snippet}".AsMemory(), ct).ConfigureAwait(false);
                    }
                }
            }

            await _stdout.WriteLineAsync($"LexicalMs: {result.Timing.LexicalMs.ToString("0.00", CultureInfo.InvariantCulture)}".AsMemory(), ct).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> RunShowAsync(
        string[] args,
        int startIndex,
        string? vaultOption,
        bool vaultProvided,
        bool json,
        bool helpRequested,
        CancellationToken ct)
    {
        var endOfOptions = false;
        var paths = new List<string>();

        for (var i = startIndex; i < args.Length; i++)
        {
            var arg = args[i];
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && arg is "--help" or "-h")
            {
                helpRequested = true;
                continue;
            }

            if (!endOfOptions && TryParseGlobalFlag(args, ref i, ref vaultOption, ref vaultProvided, ref json, out var flagError))
            {
                if (flagError is not null)
                {
                    await _stderr.WriteLineAsync(flagError.AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                continue;
            }

            if (!endOfOptions && arg.StartsWith('-') && arg.Length > 1)
            {
                await _stderr.WriteLineAsync($"Unknown option '{arg}' for 'show'.".AsMemory(), ct).ConfigureAwait(false);
                return 1;
            }

            paths.Add(arg);
        }

        if (helpRequested)
        {
            await WriteHelpAsync(ct).ConfigureAwait(false);
            return 0;
        }

        if (paths.Count == 0 || string.IsNullOrWhiteSpace(paths[0]))
        {
            await _stderr.WriteLineAsync("Missing required argument <relativePath> for 'show'.".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        if (paths.Count > 1)
        {
            await _stderr.WriteLineAsync($"Unexpected argument '{paths[1]}' for 'show'.".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        if (!TryResolveVaultPath(vaultOption, vaultProvided, out var fullVaultPath, out var pathError))
        {
            await _stderr.WriteLineAsync((pathError ?? "Invalid vault path.").AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        var relativePath = paths[0];
        var vaultStore = new FileSystemVaultStore(fullVaultPath);

        NoteDocument? doc;
        try
        {
            doc = await vaultStore.ReadAsync(relativePath, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or UnauthorizedAccessException or IOException)
        {
            await _stderr.WriteLineAsync($"Failed to read note '{relativePath}': {ex.Message}".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        if (doc is null)
        {
            await _stderr.WriteLineAsync($"Note not found: {relativePath}".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        if (json)
        {
            await WriteJsonAsync(
                new ShowOutput(
                    Path: doc.Path,
                    Id: doc.Metadata.Id,
                    Title: doc.Metadata.Title,
                    Type: doc.Metadata.Type.ToString(),
                    Status: doc.Metadata.Status.ToString(),
                    Tags: doc.Metadata.Tags,
                    Up: doc.Metadata.Up,
                    Created: doc.Metadata.Created,
                    Updated: doc.Metadata.Updated,
                    Body: doc.Body),
                ct).ConfigureAwait(false);
        }
        else
        {
            await _stdout.WriteLineAsync($"Id: {doc.Metadata.Id}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync($"Title: {doc.Metadata.Title}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync($"Type: {doc.Metadata.Type}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync($"Status: {doc.Metadata.Status}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync($"Tags: {string.Join(", ", doc.Metadata.Tags)}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync($"Up: {string.Join(", ", doc.Metadata.Up)}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync($"Created: {FormatTimestamp(doc.Metadata.Created)}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync($"Updated: {FormatTimestamp(doc.Metadata.Updated)}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync("Body:".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync(doc.Body.AsMemory(), ct).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> RunRebuildIndexAsync(
        string[] args,
        int startIndex,
        string? vaultOption,
        bool vaultProvided,
        bool json,
        bool helpRequested,
        CancellationToken ct)
    {
        var endOfOptions = false;
        for (var i = startIndex; i < args.Length; i++)
        {
            var arg = args[i];
            if (!endOfOptions && arg == "--")
            {
                endOfOptions = true;
                continue;
            }

            if (!endOfOptions && arg is "--help" or "-h")
            {
                helpRequested = true;
                continue;
            }

            if (!endOfOptions && TryParseGlobalFlag(args, ref i, ref vaultOption, ref vaultProvided, ref json, out var flagError))
            {
                if (flagError is not null)
                {
                    await _stderr.WriteLineAsync(flagError.AsMemory(), ct).ConfigureAwait(false);
                    return 1;
                }

                continue;
            }

            if (!endOfOptions && arg.StartsWith('-') && arg.Length > 1)
            {
                await _stderr.WriteLineAsync($"Unknown option '{arg}' for 'rebuild-index'.".AsMemory(), ct).ConfigureAwait(false);
                return 1;
            }

            await _stderr.WriteLineAsync($"Unexpected argument '{arg}' for 'rebuild-index'.".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        if (helpRequested)
        {
            await WriteHelpAsync(ct).ConfigureAwait(false);
            return 0;
        }

        if (!TryResolveVaultPath(vaultOption, vaultProvided, out var fullVaultPath, out var pathError))
        {
            await _stderr.WriteLineAsync((pathError ?? "Invalid vault path.").AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        if (!Directory.Exists(fullVaultPath))
        {
            await _stderr.WriteLineAsync($"Vault directory does not exist: {fullVaultPath}".AsMemory(), ct).ConfigureAwait(false);
            return 1;
        }

        var vaultStore = new FileSystemVaultStore(fullVaultPath);
        using var index = new SqliteVaultIndex(fullVaultPath, vaultStore, _timeProvider);
        var stats = await index.RebuildAsync(ct).ConfigureAwait(false);

        if (json)
        {
            await WriteJsonAsync(
                new RebuildIndexOutput(Chunks: stats.Chunks, BuiltAt: stats.BuiltAt),
                ct).ConfigureAwait(false);
        }
        else
        {
            await _stdout.WriteLineAsync($"Chunks: {stats.Chunks.ToString(CultureInfo.InvariantCulture)}".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync($"BuiltAt: {stats.BuiltAt.ToString("O", CultureInfo.InvariantCulture)}".AsMemory(), ct).ConfigureAwait(false);
        }

        return 0;
    }

    private async Task<int> UnknownCommandAsync(string command, CancellationToken ct)
    {
        await _stderr.WriteLineAsync($"Unknown command '{command}'. Run 'help' to see available commands.".AsMemory(), ct).ConfigureAwait(false);
        return 1;
    }

    private async Task WriteCaptureResultAsync(CaptureExecutionResult result, bool json, CancellationToken ct)
    {
        var mode = result.Applied ? "APPLIED" : "DRY-RUN";
        if (json)
        {
            await WriteJsonAsync(
                new CaptureOutput(
                    Mode: mode,
                    Applied: result.Applied,
                    Success: result.Success,
                    Path: result.Path,
                    Diff: result.Diff),
                ct).ConfigureAwait(false);
            return;
        }

        await _stdout.WriteLineAsync($"Mode: {mode}".AsMemory(), ct).ConfigureAwait(false);
        await _stdout.WriteLineAsync($"Path: {result.Path}".AsMemory(), ct).ConfigureAwait(false);
        if (!string.IsNullOrEmpty(result.Diff))
        {
            await _stdout.WriteLineAsync("Diff:".AsMemory(), ct).ConfigureAwait(false);
            await _stdout.WriteLineAsync(result.Diff.AsMemory(), ct).ConfigureAwait(false);
        }
    }

    private async Task WriteHelpAsync(CancellationToken ct)
    {
        const string helpText = """
            SecondBrain CLI

            Usage:
              secondbrain [--vault <path>] [--json] <command> [options] [arguments]

            Commands:
              help                          Show this help message
              doctor                        Validate vault directory and show index stats
              capture <text...>             Capture a note (--title <t>, --apply, --dry-run)
              diary [<text...>]             Append diary entry (--stats, --dry-run, --apply)
              search <query...>             Search notes (--limit <n>, --tags <csv>, --type <NoteType>, --prefix <path>)
              show <relativePath>           Print note frontmatter and body
              rebuild-index                 Rebuild SQLite index from vault notes

            Common Options:
              --vault <path>                Vault directory (default: SECONDBRAIN_VAULT or current directory)
              --json                        Output compact JSON
              -h, --help                    Show help
            """;

        await _stdout.WriteLineAsync(helpText.AsMemory(), ct).ConfigureAwait(false);
    }

    private async Task WriteJsonAsync<T>(T value, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(value, JsonOptions);
        await _stdout.WriteLineAsync(json.AsMemory(), ct).ConfigureAwait(false);
    }

    private static bool TryParseGlobalFlag(
        string[] args,
        ref int index,
        ref string? vaultOption,
        ref bool vaultProvided,
        ref bool json,
        out string? error)
    {
        error = null;

        var arg = args[index];
        if (arg == "--json")
        {
            json = true;
            return true;
        }

        if (arg == "--vault")
        {
            if (index + 1 >= args.Length)
            {
                error = "Option '--vault' requires a value.";
                return true;
            }

            vaultOption = args[++index];
            vaultProvided = true;
            return true;
        }

        if (arg.StartsWith("--vault=", StringComparison.Ordinal))
        {
            vaultOption = arg["--vault=".Length..];
            vaultProvided = true;
            return true;
        }

        return false;
    }

    private static bool TryParseValueFlag(
        string[] args,
        ref int index,
        string flagName,
        out string value,
        out string? error)
    {
        value = string.Empty;
        error = null;

        var arg = args[index];
        if (arg == flagName)
        {
            if (index + 1 >= args.Length)
            {
                error = $"Option '{flagName}' requires a value.";
                return true;
            }

            value = args[++index];
            return true;
        }

        var prefix = flagName + "=";
        if (arg.StartsWith(prefix, StringComparison.Ordinal))
        {
            value = arg[prefix.Length..];
            return true;
        }

        return false;
    }

    private static bool TryResolveVaultPath(
        string? vaultOption,
        bool vaultProvided,
        out string fullVaultPath,
        out string? error)
    {
        fullVaultPath = string.Empty;
        error = null;

        string? rawPath;
        if (vaultProvided)
        {
            if (string.IsNullOrWhiteSpace(vaultOption))
            {
                error = "Option '--vault' requires a non-empty path.";
                return false;
            }

            rawPath = vaultOption;
        }
        else
        {
            rawPath = Environment.GetEnvironmentVariable("SECONDBRAIN_VAULT");
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                rawPath = Directory.GetCurrentDirectory();
            }
        }

        try
        {
            fullVaultPath = Path.GetFullPath(rawPath);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            error = $"Invalid vault path '{rawPath}': {ex.Message}";
            return false;
        }
    }

    private static string FormatTimestamp(DateTimeOffset? value)
        => value?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty;

    private sealed record DoctorOutput(
        string VaultPath,
        int NotesCount,
        int Chunks,
        int Vectors,
        string EmbeddingModel);

    private sealed record CaptureOutput(
        string Mode,
        bool Applied,
        bool Success,
        string Path,
        string? Diff);

    private sealed record DiaryStatsOutput(
        int FilesCount,
        int EntriesCount);

    private sealed record SearchOutput(
        string Query,
        IReadOnlyList<SearchHit> Hits,
        double LexicalMs,
        double VectorMs,
        double TotalMs);

    private sealed record ShowOutput(
        string Path,
        string Id,
        string Title,
        string Type,
        string Status,
        IEnumerable<string> Tags,
        IEnumerable<string> Up,
        DateTimeOffset? Created,
        DateTimeOffset? Updated,
        string Body);

    private sealed record RebuildIndexOutput(
        int Chunks,
        DateTimeOffset BuiltAt);
}