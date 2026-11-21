using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;

namespace SecondBrain.Infrastructure.Vault;

public sealed class FileSystemVaultStore : IVaultStore
{
    private const int DiffContextLines = 3;
    private const int MaxDpCells = 250_000;

    private readonly string _vaultRoot;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _pathLocks;

    public FileSystemVaultStore(string vaultRoot, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        if (vaultRoot.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Vault root cannot contain null bytes.", nameof(vaultRoot));
        }

        _vaultRoot = Path.GetFullPath(vaultRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pathLocks = new ConcurrentDictionary<string, SemaphoreSlim>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<VaultEntry>> ListAsync(string? prefix = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();

        var normalizedPrefix = VaultPathPolicy.NormalizePrefix(prefix);
        if (!Directory.Exists(_vaultRoot))
        {
            return [];
        }

        var entries = new List<VaultEntry>();
        var pendingDirectories = new Stack<string>();
        pendingDirectories.Push(_vaultRoot);

        while (pendingDirectories.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var currentDirectory = pendingDirectories.Pop();

            foreach (var subDirectory in Directory.EnumerateDirectories(currentDirectory))
            {
                ct.ThrowIfCancellationRequested();
                var name = Path.GetFileName(subDirectory);
                if (!VaultPathPolicy.IsForbiddenSystemSegment(name))
                {
                    pendingDirectories.Push(subDirectory);
                }
            }

            foreach (var filePath in Directory.EnumerateFiles(currentDirectory, "*.md"))
            {
                ct.ThrowIfCancellationRequested();

                var relativePath = Path.GetRelativePath(_vaultRoot, filePath).Replace('\\', '/');
                if (normalizedPrefix is not null &&
                    !relativePath.StartsWith(normalizedPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                var fileInfo = new FileInfo(filePath);
                var modifiedAt = ToUtcOffset(fileInfo.LastWriteTimeUtc);
                var sizeBytes = fileInfo.Length;
                var rawContent = await File.ReadAllTextAsync(filePath, Encoding.UTF8, ct).ConfigureAwait(false);

                if (FrontmatterCodec.TryParse(relativePath, rawContent, modifiedAt, out var document, out _) &&
                    document is not null)
                {
                    entries.Add(new VaultEntry(
                        relativePath,
                        document.Metadata.Type,
                        document.Metadata.Title,
                        modifiedAt,
                        sizeBytes));
                }
                else
                {
                    entries.Add(new VaultEntry(
                        relativePath,
                        null,
                        Path.GetFileNameWithoutExtension(relativePath),
                        modifiedAt,
                        sizeBytes));
                }
            }
        }

        entries.Sort(static (a, b) => StringComparer.Ordinal.Compare(a.Path, b.Path));
        return entries;
    }

    public async Task<NoteDocument?> ReadAsync(string path, CancellationToken ct = default)
    {
        var (normalizedPath, fullPath) = VaultPathPolicy.NormalizeAndResolve(_vaultRoot, path);
        var pathLock = GetPathLock(normalizedPath);

        await pathLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(fullPath))
            {
                return null;
            }

            var rawContent = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct).ConfigureAwait(false);
            var modifiedAt = ToUtcOffset(File.GetLastWriteTimeUtc(fullPath));

            if (!FrontmatterCodec.TryParse(normalizedPath, rawContent, modifiedAt, out var document, out var error) ||
                document is null)
            {
                throw new FormatException($"Invalid frontmatter in '{normalizedPath}': {error}");
            }

            return document;
        }
        finally
        {
            pathLock.Release();
        }
    }

    public async Task<WriteResult> WriteAsync(NoteDocument doc, WriteOptions options, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ArgumentNullException.ThrowIfNull(options);

        var (normalizedPath, fullPath) = VaultPathPolicy.NormalizeAndResolve(_vaultRoot, doc.Path);
        var pathLock = GetPathLock(normalizedPath);

        await pathLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var exists = File.Exists(fullPath);
            string existingContent;
            string? currentHash;
            string lineEnding;

            if (exists)
            {
                existingContent = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct).ConfigureAwait(false);
                currentHash = ComputeSha256Hex(existingContent);
                lineEnding = DetectLineEnding(existingContent);

                if (options.ExpectedHash is not null)
                {
                    if (options.ExpectedHash.Length == 0 ||
                        !string.Equals(currentHash, options.ExpectedHash, StringComparison.OrdinalIgnoreCase))
                    {
                        return new WriteResult(
                            false,
                            currentHash,
                            null,
                            $"Hash conflict for '{normalizedPath}': expected '{options.ExpectedHash}', actual '{currentHash}'.");
                    }
                }
            }
            else
            {
                existingContent = string.Empty;
                lineEnding = "\n";

                if (!string.IsNullOrEmpty(options.ExpectedHash))
                {
                    return new WriteResult(false, null, null, "Conflict: target file does not exist.");
                }
            }

            var serialized = FrontmatterCodec.Serialize(doc with { Path = normalizedPath }, lineEnding);
            var newHash = ComputeSha256Hex(serialized);
            var diff = BuildUnifiedDiff(normalizedPath, existingContent, serialized, isNewFile: !exists);

            if (options.DryRun)
            {
                return new WriteResult(true, newHash, diff, null);
            }

            await AtomicWriter.WriteAllTextAtomicAsync(fullPath, serialized, ct).ConfigureAwait(false);
            return new WriteResult(true, newHash, diff, null);
        }
        finally
        {
            pathLock.Release();
        }
    }

    public async Task<bool> DeleteAsync(string path, string expectedHash, CancellationToken ct = default)
    {
        var (normalizedPath, fullPath) = VaultPathPolicy.NormalizeAndResolve(_vaultRoot, path);
        if (string.IsNullOrWhiteSpace(expectedHash))
        {
            return false;
        }

        var pathLock = GetPathLock(normalizedPath);

        await pathLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!File.Exists(fullPath))
            {
                return false;
            }

            var existingContent = await File.ReadAllTextAsync(fullPath, Encoding.UTF8, ct).ConfigureAwait(false);
            var currentHash = ComputeSha256Hex(existingContent);

            if (!string.Equals(currentHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            File.Delete(fullPath);
            return true;
        }
        finally
        {
            pathLock.Release();
        }
    }

    private SemaphoreSlim GetPathLock(string normalizedPath)
        => _pathLocks.GetOrAdd(normalizedPath, static _ => new SemaphoreSlim(1, 1));

    private DateTimeOffset ToUtcOffset(DateTime utcDateTime)
        => utcDateTime == DateTime.MinValue || utcDateTime.Year <= 1601
            ? _timeProvider.GetUtcNow()
            : new DateTimeOffset(DateTime.SpecifyKind(utcDateTime, DateTimeKind.Utc));

    private static string ComputeSha256Hex(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static string DetectLineEnding(string content)
        => content.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";

    private static string BuildUnifiedDiff(string normalizedPath, string oldText, string newText, bool isNewFile)
    {
        if (!isNewFile && string.Equals(oldText, newText, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var oldLines = isNewFile && oldText.Length == 0 ? Array.Empty<string>() : SplitLines(oldText);
        var newLines = SplitLines(newText);

        var prefix = 0;
        while (prefix < oldLines.Length &&
               prefix < newLines.Length &&
               string.Equals(oldLines[prefix], newLines[prefix], StringComparison.Ordinal))
        {
            prefix++;
        }

        var suffix = 0;
        while (suffix < oldLines.Length - prefix &&
               suffix < newLines.Length - prefix &&
               string.Equals(oldLines[oldLines.Length - 1 - suffix], newLines[newLines.Length - 1 - suffix], StringComparison.Ordinal))
        {
            suffix++;
        }

        var oldMidLen = oldLines.Length - prefix - suffix;
        var newMidLen = newLines.Length - prefix - suffix;

        var ops = new List<DiffOp>(oldLines.Length + newLines.Length);
        for (var k = 0; k < prefix; k++)
        {
            ops.Add(new DiffOp(' ', oldLines[k], k + 1, k + 1));
        }

        if ((long)oldMidLen * newMidLen <= MaxDpCells)
        {
            AppendLcsOps(ops, oldLines, newLines, prefix, oldMidLen, newMidLen);
        }
        else
        {
            for (var i = 0; i < oldMidLen; i++)
            {
                ops.Add(new DiffOp('-', oldLines[prefix + i], prefix + i + 1, prefix + 1));
            }

            for (var j = 0; j < newMidLen; j++)
            {
                ops.Add(new DiffOp('+', newLines[prefix + j], prefix + oldMidLen + 1, prefix + j + 1));
            }
        }

        for (var k = 0; k < suffix; k++)
        {
            var oi = oldLines.Length - suffix + k;
            var ni = newLines.Length - suffix + k;
            ops.Add(new DiffOp(' ', oldLines[oi], oi + 1, ni + 1));
        }

        var changeIndices = new List<int>();
        for (var idx = 0; idx < ops.Count; idx++)
        {
            if (ops[idx].Kind != ' ')
            {
                changeIndices.Add(idx);
            }
        }

        if (changeIndices.Count == 0)
        {
            return string.Empty;
        }

        var hunks = new List<(int Start, int End)>();
        var hunkStart = Math.Max(0, changeIndices[0] - DiffContextLines);
        var hunkEnd = Math.Min(ops.Count - 1, changeIndices[0] + DiffContextLines);

        for (var c = 1; c < changeIndices.Count; c++)
        {
            var idx = changeIndices[c];
            if (idx - DiffContextLines <= hunkEnd + 1)
            {
                hunkEnd = Math.Min(ops.Count - 1, idx + DiffContextLines);
            }
            else
            {
                hunks.Add((hunkStart, hunkEnd));
                hunkStart = Math.Max(0, idx - DiffContextLines);
                hunkEnd = Math.Min(ops.Count - 1, idx + DiffContextLines);
            }
        }

        hunks.Add((hunkStart, hunkEnd));

        var sb = new StringBuilder();
        sb.Append(isNewFile ? "--- /dev/null" : $"--- a/{normalizedPath}").Append('\n');
        sb.Append($"+++ b/{normalizedPath}").Append('\n');

        foreach (var (start, end) in hunks)
        {
            var oldCount = 0;
            var newCount = 0;
            for (var idx = start; idx <= end; idx++)
            {
                if (ops[idx].Kind is ' ' or '-')
                {
                    oldCount++;
                }

                if (ops[idx].Kind is ' ' or '+')
                {
                    newCount++;
                }
            }

            var oldStart = isNewFile ? 0 : (oldCount > 0 ? ops[start].OldLine : Math.Max(0, ops[start].OldLine - 1));
            var newStart = newCount > 0 ? ops[start].NewLine : 0;

            sb.Append($"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@").Append('\n');
            for (var idx = start; idx <= end; idx++)
            {
                sb.Append(ops[idx].Kind).Append(ops[idx].Line).Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\n');
    }

    private static void AppendLcsOps(
        List<DiffOp> ops,
        string[] oldLines,
        string[] newLines,
        int prefix,
        int oldMidLen,
        int newMidLen)
    {
        var dp = new int[oldMidLen + 1, newMidLen + 1];

        for (var i = oldMidLen - 1; i >= 0; i--)
        {
            for (var j = newMidLen - 1; j >= 0; j--)
            {
                dp[i, j] = string.Equals(oldLines[prefix + i], newLines[prefix + j], StringComparison.Ordinal)
                    ? dp[i + 1, j + 1] + 1
                    : Math.Max(dp[i + 1, j], dp[i, j + 1]);
            }
        }

        var oi = 0;
        var nj = 0;
        while (oi < oldMidLen && nj < newMidLen)
        {
            if (string.Equals(oldLines[prefix + oi], newLines[prefix + nj], StringComparison.Ordinal))
            {
                ops.Add(new DiffOp(' ', oldLines[prefix + oi], prefix + oi + 1, prefix + nj + 1));
                oi++;
                nj++;
            }
            else if (dp[oi + 1, nj] >= dp[oi, nj + 1])
            {
                ops.Add(new DiffOp('-', oldLines[prefix + oi], prefix + oi + 1, prefix + nj + 1));
                oi++;
            }
            else
            {
                ops.Add(new DiffOp('+', newLines[prefix + nj], prefix + oi + 1, prefix + nj + 1));
                nj++;
            }
        }

        while (oi < oldMidLen)
        {
            ops.Add(new DiffOp('-', oldLines[prefix + oi], prefix + oi + 1, prefix + nj + 1));
            oi++;
        }

        while (nj < newMidLen)
        {
            ops.Add(new DiffOp('+', newLines[prefix + nj], prefix + oi + 1, prefix + nj + 1));
            nj++;
        }
    }

    private static string[] SplitLines(string text)
    {
        if (text.Length == 0)
        {
            return [];
        }

        var normalized = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        var rawLines = normalized.Split('\n');
        return rawLines.Length > 0 && rawLines[^1].Length == 0
            ? rawLines[..^1]
            : rawLines;
    }

    private readonly record struct DiffOp(char Kind, string Line, int OldLine, int NewLine);
}