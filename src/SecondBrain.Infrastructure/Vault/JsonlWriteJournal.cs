using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;

namespace SecondBrain.Infrastructure.Vault;

public sealed class JsonlWriteJournal : IWriteJournal
{
    private const string EventBegin = "begin";
    private const string EventCommitted = "committed";
    private const string EventRolledBack = "rolled_back";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly string _vaultRoot;
    private readonly TimeProvider _timeProvider;
    private readonly string _journalDirectory;
    private readonly string _snapshotsDirectory;
    private readonly string _journalFilePath;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private readonly Dictionary<Guid, OperationRecord> _operations = new();
    private readonly List<Guid> _operationOrder = [];
    private bool _initialized;

    public JsonlWriteJournal(string vaultRoot, TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        if (vaultRoot.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Vault root cannot contain null bytes.", nameof(vaultRoot));
        }

        _vaultRoot = Path.GetFullPath(vaultRoot);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _journalDirectory = Path.Combine(_vaultRoot, ".brain", "journal");
        _snapshotsDirectory = Path.Combine(_journalDirectory, "snapshots");
        _journalFilePath = Path.Combine(_journalDirectory, "journal.jsonl");
    }

    public Task<JournalEntry> BeginAsync(WriteOperation op, CancellationToken ct = default)
        => BeginAsync(op, runId: null, ct);

    public async Task<JournalEntry> BeginAsync(WriteOperation op, string? runId, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(op);

        var (normalizedPath, targetFullPath) = VaultPathPolicy.NormalizeAndResolve(_vaultRoot, op.Path);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(ct).ConfigureAwait(false);

            var operationId = Guid.NewGuid();
            var effectiveRunId = string.IsNullOrWhiteSpace(runId)
                ? Guid.NewGuid().ToString("N")[..12]
                : runId;
            var startedAt = _timeProvider.GetUtcNow();

            var existedBefore = File.Exists(targetFullPath);
            var snapshotPath = GetSnapshotPath(operationId);

            if (existedBefore)
            {
                var originalBytes = await File.ReadAllBytesAsync(targetFullPath, ct).ConfigureAwait(false);
                await AtomicWriter.WriteAllBytesAtomicAsync(snapshotPath, originalBytes, ct).ConfigureAwait(false);
            }

            var beginEvent = new JournalEventDto(
                EventType: EventBegin,
                OperationId: operationId,
                RunId: effectiveRunId,
                Kind: op.Kind,
                Path: normalizedPath,
                ExpectedHash: op.ExpectedHash,
                Payload: op.Payload,
                Reason: op.Reason,
                StartedAt: startedAt,
                ExistedBefore: existedBefore,
                SnapshotPath: existedBefore ? snapshotPath : null,
                Success: null,
                Hash: null,
                Error: null,
                CommittedAt: null,
                RolledBackAt: null);

            await AppendEventCoreAsync(beginEvent, ct).ConfigureAwait(false);

            _operations[operationId] = new OperationRecord(
                operationId,
                effectiveRunId,
                normalizedPath,
                existedBefore,
                snapshotPath);
            _operationOrder.Add(operationId);

            return new JournalEntry(operationId, effectiveRunId, op, startedAt);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task CommitAsync(Guid operationId, WriteResult result, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(ct).ConfigureAwait(false);

            if (!_operations.TryGetValue(operationId, out var record))
            {
                throw new KeyNotFoundException($"Journal operation '{operationId}' was not found.");
            }

            var committedAt = _timeProvider.GetUtcNow();
            var commitEvent = new JournalEventDto(
                EventType: EventCommitted,
                OperationId: operationId,
                RunId: record.RunId,
                Kind: null,
                Path: null,
                ExpectedHash: null,
                Payload: null,
                Reason: null,
                StartedAt: null,
                ExistedBefore: null,
                SnapshotPath: null,
                Success: result.Success,
                Hash: result.Hash,
                Error: result.Error,
                CommittedAt: committedAt,
                RolledBackAt: null);

            await AppendEventCoreAsync(commitEvent, ct).ConfigureAwait(false);
            record.Committed = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RollbackAsync(Guid operationId, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(ct).ConfigureAwait(false);
            await RollbackCoreAsync(operationId, ct).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RollbackRunAsync(string runId, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedCoreAsync(ct).ConfigureAwait(false);

            for (var i = _operationOrder.Count - 1; i >= 0; i--)
            {
                var operationId = _operationOrder[i];
                if (_operations.TryGetValue(operationId, out var record) &&
                    string.Equals(record.RunId, runId, StringComparison.Ordinal) &&
                    !record.RolledBack)
                {
                    await RollbackCoreAsync(operationId, ct).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task EnsureLoadedCoreAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        _operations.Clear();
        _operationOrder.Clear();

        if (File.Exists(_journalFilePath))
        {
            await using var stream = new FileStream(
                _journalFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                bufferSize: 4096,
                useAsync: true);
            using var reader = new StreamReader(stream, Utf8NoBom);

            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line)
            {
                ct.ThrowIfCancellationRequested();

                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var dto = JsonSerializer.Deserialize<JournalEventDto>(line, JsonOptions)
                    ?? throw new FormatException($"Invalid journal line in '{_journalFilePath}'.");

                ApplyLoadedEvent(dto);
            }
        }

        _initialized = true;
    }

    private void ApplyLoadedEvent(JournalEventDto dto)
    {
        switch (dto.EventType)
        {
            case EventBegin:
                var normalizedPath = dto.Path
                    ?? throw new FormatException($"Journal 'begin' event for '{dto.OperationId}' is missing path.");
                var runId = dto.RunId ?? string.Empty;
                var existedBefore = dto.ExistedBefore ?? false;
                var snapshotPath = dto.SnapshotPath ?? GetSnapshotPath(dto.OperationId);

                if (!_operations.ContainsKey(dto.OperationId))
                {
                    _operations[dto.OperationId] = new OperationRecord(
                        dto.OperationId,
                        runId,
                        normalizedPath,
                        existedBefore,
                        snapshotPath);
                    _operationOrder.Add(dto.OperationId);
                }
                break;

            case EventCommitted:
                if (_operations.TryGetValue(dto.OperationId, out var committedRecord))
                {
                    committedRecord.Committed = true;
                }
                break;

            case EventRolledBack:
                if (_operations.TryGetValue(dto.OperationId, out var rolledBackRecord))
                {
                    rolledBackRecord.RolledBack = true;
                }
                break;
        }
    }

    private async Task RollbackCoreAsync(Guid operationId, CancellationToken ct)
    {
        if (!_operations.TryGetValue(operationId, out var record))
        {
            throw new KeyNotFoundException($"Journal operation '{operationId}' was not found.");
        }

        if (record.RolledBack)
        {
            return;
        }

        var (_, targetFullPath) = VaultPathPolicy.NormalizeAndResolve(_vaultRoot, record.NormalizedPath);

        if (record.ExistedBefore)
        {
            if (!File.Exists(record.SnapshotPath))
            {
                throw new FileNotFoundException(
                    $"Snapshot file '{record.SnapshotPath}' for operation '{operationId}' was not found.",
                    record.SnapshotPath);
            }

            var snapshotBytes = await File.ReadAllBytesAsync(record.SnapshotPath, ct).ConfigureAwait(false);
            await AtomicWriter.WriteAllBytesAtomicAsync(targetFullPath, snapshotBytes, ct).ConfigureAwait(false);
        }
        else if (File.Exists(targetFullPath))
        {
            File.Delete(targetFullPath);
        }

        var rolledBackAt = _timeProvider.GetUtcNow();
        var rollbackEvent = new JournalEventDto(
            EventType: EventRolledBack,
            OperationId: operationId,
            RunId: record.RunId,
            Kind: null,
            Path: null,
            ExpectedHash: null,
            Payload: null,
            Reason: null,
            StartedAt: null,
            ExistedBefore: null,
            SnapshotPath: null,
            Success: null,
            Hash: null,
            Error: null,
            CommittedAt: null,
            RolledBackAt: rolledBackAt);

        await AppendEventCoreAsync(rollbackEvent, ct).ConfigureAwait(false);
        record.RolledBack = true;
    }

    private async Task AppendEventCoreAsync(JournalEventDto dto, CancellationToken ct)
    {
        Directory.CreateDirectory(_journalDirectory);

        var jsonLine = JsonSerializer.Serialize(dto, JsonOptions) + "\n";
        var bytes = Utf8NoBom.GetBytes(jsonLine);

        await using var stream = new FileStream(
            _journalFilePath,
            FileMode.Append,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            useAsync: true);

        await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
        await stream.FlushAsync(ct).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private string GetSnapshotPath(Guid operationId)
        => Path.Combine(_snapshotsDirectory, $"{operationId:N}.bak");

    private sealed class OperationRecord(
        Guid operationId,
        string runId,
        string normalizedPath,
        bool existedBefore,
        string snapshotPath)
    {
        public Guid OperationId { get; } = operationId;
        public string RunId { get; } = runId;
        public string NormalizedPath { get; } = normalizedPath;
        public bool ExistedBefore { get; } = existedBefore;
        public string SnapshotPath { get; } = snapshotPath;
        public bool Committed { get; set; }
        public bool RolledBack { get; set; }
    }

    private sealed record JournalEventDto(
        string EventType,
        Guid OperationId,
        string? RunId,
        string? Kind,
        string? Path,
        string? ExpectedHash,
        string? Payload,
        string? Reason,
        DateTimeOffset? StartedAt,
        bool? ExistedBefore,
        string? SnapshotPath,
        bool? Success,
        string? Hash,
        string? Error,
        DateTimeOffset? CommittedAt,
        DateTimeOffset? RolledBackAt);
}