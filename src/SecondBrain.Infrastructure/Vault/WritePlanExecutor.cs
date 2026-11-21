using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using SecondBrain.Core.Linking;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;

namespace SecondBrain.Infrastructure.Vault;

public sealed record WritePlanExecutionResult(
    bool Success,
    string RunId,
    IReadOnlyList<WriteResult> Results,
    IReadOnlyList<Guid> CommittedOperationIds,
    string? Error);

public sealed record ManagedBlockPayload(
    [property: JsonPropertyName("block_id")] string BlockId,
    [property: JsonPropertyName("content")] string Content);

public sealed class WritePlanExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IVaultStore _vaultStore;
    private readonly JsonlWriteJournal _journal;
    private readonly TimeProvider _timeProvider;

    public WritePlanExecutor(
        IVaultStore vaultStore,
        JsonlWriteJournal journal,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(vaultStore);
        ArgumentNullException.ThrowIfNull(journal);

        _vaultStore = vaultStore;
        _journal = journal;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<WritePlanExecutionResult> ExecuteAsync(
        WritePlan plan,
        bool dryRun = false,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ct.ThrowIfCancellationRequested();

        var results = new List<WriteResult>(plan.Operations.Count);

        if (dryRun)
        {
            foreach (var op in plan.Operations)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var dryResult = await ExecuteOperationAsync(op, dryRun: true, ct).ConfigureAwait(false);
                    results.Add(dryResult);

                    if (!dryResult.Success)
                    {
                        return new WritePlanExecutionResult(
                            Success: false,
                            RunId: plan.RunId,
                            Results: results,
                            CommittedOperationIds: [],
                            Error: dryResult.Error ?? $"Dry-run failed for '{op.Kind}' on '{op.Path}'.");
                    }
                }
                catch (Exception ex)
                {
                    return new WritePlanExecutionResult(
                        Success: false,
                        RunId: plan.RunId,
                        Results: results,
                        CommittedOperationIds: [],
                        Error: ex.Message);
                }
            }

            return new WritePlanExecutionResult(
                Success: true,
                RunId: plan.RunId,
                Results: results,
                CommittedOperationIds: [],
                Error: null);
        }

        var startedOperationIds = new List<Guid>(plan.Operations.Count);
        var committedOperationIds = new List<Guid>(plan.Operations.Count);

        foreach (var op in plan.Operations)
        {
            ct.ThrowIfCancellationRequested();

            JournalEntry entry;
            try
            {
                entry = await _journal.BeginAsync(op, plan.RunId, ct).ConfigureAwait(false);
                startedOperationIds.Add(entry.OperationId);
            }
            catch (Exception ex)
            {
                var rollbackError = await RollbackLifoAsync(startedOperationIds).ConfigureAwait(false);
                var message = rollbackError is null ? ex.Message : $"{ex.Message} (Rollback errors: {rollbackError})";
                return new WritePlanExecutionResult(false, plan.RunId, results, [], message);
            }

            try
            {
                var writeResult = await ExecuteOperationAsync(op, dryRun: false, ct).ConfigureAwait(false);
                results.Add(writeResult);

                if (!writeResult.Success)
                {
                    var rollbackError = await RollbackLifoAsync(startedOperationIds).ConfigureAwait(false);
                    var baseError = writeResult.Error ?? $"Operation '{op.Kind}' on '{op.Path}' failed.";
                    var message = rollbackError is null ? baseError : $"{baseError} (Rollback errors: {rollbackError})";
                    return new WritePlanExecutionResult(false, plan.RunId, results, [], message);
                }

                await _journal.CommitAsync(entry.OperationId, writeResult, ct).ConfigureAwait(false);
                committedOperationIds.Add(entry.OperationId);
            }
            catch (Exception ex)
            {
                var rollbackError = await RollbackLifoAsync(startedOperationIds).ConfigureAwait(false);
                var message = rollbackError is null ? ex.Message : $"{ex.Message} (Rollback errors: {rollbackError})";
                return new WritePlanExecutionResult(false, plan.RunId, results, [], message);
            }
        }

        return new WritePlanExecutionResult(
            Success: true,
            RunId: plan.RunId,
            Results: results,
            CommittedOperationIds: committedOperationIds,
            Error: null);
    }

    private async Task<string?> RollbackLifoAsync(List<Guid> startedOperationIds)
    {
        List<string>? errors = null;

        for (var i = startedOperationIds.Count - 1; i >= 0; i--)
        {
            try
            {
                await _journal.RollbackAsync(startedOperationIds[i], CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errors ??= [];
                errors.Add($"[{startedOperationIds[i]}] {ex.Message}");
            }
        }

        return errors is null ? null : string.Join("; ", errors);
    }

    private Task<WriteResult> ExecuteOperationAsync(WriteOperation op, bool dryRun, CancellationToken ct)
    {
        return op.Kind switch
        {
            "create" => ExecuteCreateAsync(op, dryRun, ct),
            "append_entry" => ExecuteAppendEntryAsync(op, dryRun, ct),
            "replace_managed_block" => ExecuteReplaceManagedBlockAsync(op, dryRun, ct),
            "patch_frontmatter" => ExecutePatchFrontmatterAsync(op, dryRun, ct),
            _ => throw new NotSupportedException($"Unsupported write operation kind '{op.Kind}'.")
        };
    }

    private Task<WriteResult> ExecuteCreateAsync(WriteOperation op, bool dryRun, CancellationToken ct)
    {
        var doc = FrontmatterCodec.Parse(op.Path, op.Payload, _timeProvider.GetUtcNow());
        var expectedHash = op.ExpectedHash ?? string.Empty;
        return _vaultStore.WriteAsync(
            doc,
            new WriteOptions(ExpectedHash: expectedHash, DryRun: dryRun, Reason: op.Reason),
            ct);
    }

    private async Task<WriteResult> ExecuteAppendEntryAsync(WriteOperation op, bool dryRun, CancellationToken ct)
    {
        var now = _timeProvider.GetUtcNow();
        var existing = await _vaultStore.ReadAsync(op.Path, ct).ConfigureAwait(false);

        if (existing is not null)
        {
            var newBody = existing.Body.Length == 0 || existing.Body.EndsWith('\n')
                ? existing.Body + op.Payload
                : existing.Body + "\n" + op.Payload;

            var updated = existing with
            {
                Metadata = existing.Metadata with { Updated = now },
                Body = newBody
            };

            return await _vaultStore.WriteAsync(
                updated,
                new WriteOptions(
                    ExpectedHash: op.ExpectedHash ?? existing.ContentHash,
                    DryRun: dryRun,
                    Reason: op.Reason),
                ct).ConfigureAwait(false);
        }

        var created = FrontmatterCodec.Parse(op.Path, op.Payload, now);
        return await _vaultStore.WriteAsync(
            created,
            new WriteOptions(
                ExpectedHash: op.ExpectedHash ?? string.Empty,
                DryRun: dryRun,
                Reason: op.Reason),
            ct).ConfigureAwait(false);
    }

    private async Task<WriteResult> ExecuteReplaceManagedBlockAsync(WriteOperation op, bool dryRun, CancellationToken ct)
    {
        var payload = JsonSerializer.Deserialize<ManagedBlockPayload>(op.Payload, JsonOptions)
            ?? throw new FormatException($"Invalid managed block JSON payload for '{op.Path}'.");

        if (string.IsNullOrWhiteSpace(payload.BlockId) || payload.Content is null)
        {
            throw new FormatException($"Managed block payload for '{op.Path}' must specify non-empty 'block_id' and 'content'.");
        }

        var existing = await _vaultStore.ReadAsync(op.Path, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Target note '{op.Path}' does not exist for replace_managed_block.", op.Path);

        var updatedBody = ManagedBlockCodec.UpsertBlock(existing.Body, payload.BlockId, payload.Content);
        var updatedDoc = existing with { Body = updatedBody };

        return await _vaultStore.WriteAsync(
            updatedDoc,
            new WriteOptions(
                ExpectedHash: op.ExpectedHash ?? existing.ContentHash,
                DryRun: dryRun,
                Reason: op.Reason),
            ct).ConfigureAwait(false);
    }

    private async Task<WriteResult> ExecutePatchFrontmatterAsync(WriteOperation op, bool dryRun, CancellationToken ct)
    {
        var existing = await _vaultStore.ReadAsync(op.Path, ct).ConfigureAwait(false)
            ?? throw new FileNotFoundException($"Target note '{op.Path}' does not exist for patch_frontmatter.", op.Path);

        using var jsonDoc = JsonDocument.Parse(op.Payload);
        if (jsonDoc.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"Patch frontmatter payload for '{op.Path}' must be a JSON object.");
        }

        var patchedMetadata = ApplyFrontmatterPatch(existing.Metadata, jsonDoc.RootElement);
        var updatedDoc = existing with { Metadata = patchedMetadata };

        return await _vaultStore.WriteAsync(
            updatedDoc,
            new WriteOptions(
                ExpectedHash: op.ExpectedHash ?? existing.ContentHash,
                DryRun: dryRun,
                Reason: op.Reason),
            ct).ConfigureAwait(false);
    }

    private static NoteMetadata ApplyFrontmatterPatch(NoteMetadata metadata, JsonElement patchObject)
    {
        var unknownKeys = new Dictionary<string, object?>(metadata.UnknownKeys, StringComparer.Ordinal);
        var result = metadata;

        foreach (var prop in patchObject.EnumerateObject())
        {
            var val = prop.Value;
            switch (prop.Name)
            {
                case "title":
                    result = result with { Title = RequireString(val, prop.Name) };
                    break;
                case "type":
                    result = result with { Type = ParseEnum<NoteType>(RequireString(val, prop.Name), prop.Name) };
                    break;
                case "usecase":
                    result = result with { Usecase = RequireString(val, prop.Name) };
                    break;
                case "status":
                    result = result with { Status = ParseEnum<NoteStatus>(RequireString(val, prop.Name), prop.Name) };
                    break;
                case "tags":
                    result = result with { Tags = ParseStringArray(val, prop.Name) };
                    break;
                case "tags_proposed":
                    result = result with { TagsProposed = ParseStringArray(val, prop.Name) };
                    break;
                case "aliases":
                    result = result with { Aliases = ParseStringArray(val, prop.Name) };
                    break;
                case "related":
                    result = result with { Related = ParseStringArray(val, prop.Name) };
                    break;
                case "up":
                    result = result with { Up = ParseStringArray(val, prop.Name) };
                    break;
                case "summary":
                    result = result with { Summary = GetOptionalString(val, prop.Name) };
                    break;
                case "lang":
                    result = result with { Lang = RequireString(val, prop.Name) };
                    break;
                case "created":
                    result = result with { Created = ParseDateTimeOffset(val, prop.Name) };
                    break;
                case "updated":
                    result = result with { Updated = ParseDateTimeOffset(val, prop.Name) };
                    break;
                case "schema_version":
                    result = result with { SchemaVersion = val.GetInt32() };
                    break;
                case "source":
                    result = result with { Source = GetOptionalString(val, prop.Name) };
                    break;
                case "source_url":
                    result = result with { SourceUrl = GetOptionalString(val, prop.Name) };
                    break;
                case "content_hash":
                    result = result with { ContentHash = GetOptionalString(val, prop.Name) };
                    break;
                case "mentions_raw":
                    result = result with { MentionsRaw = ParseStringArray(val, prop.Name) };
                    break;
                case "links_origin":
                    result = result with { LinksOrigin = ParseStringDictionary(val, prop.Name) };
                    break;
                case "processed":
                    result = result with { Processed = val.ValueKind == JsonValueKind.Null ? null : val.GetBoolean() };
                    break;
                case "generated_notes":
                    result = result with { GeneratedNotes = ParseStringArray(val, prop.Name) };
                    break;
                case "stub":
                    result = result with { Stub = val.GetBoolean() };
                    break;
                case "archived_at":
                    result = result with { ArchivedAt = ParseOptionalDateTimeOffset(val, prop.Name) };
                    break;
                case "review_date":
                    result = result with { ReviewDate = ParseOptionalDateTimeOffset(val, prop.Name) };
                    break;
                case "reviewed_at":
                    result = result with { ReviewedAt = ParseOptionalDateTimeOffset(val, prop.Name) };
                    break;
                case "confidence":
                    result = result with { Confidence = val.ValueKind == JsonValueKind.Null ? null : val.GetDouble() };
                    break;
                case "slots":
                    result = result with { Slots = ParseObjectDictionary(val, prop.Name) };
                    break;
                case "decision_log":
                    result = result with { DecisionLog = ParseObjectDictionary(val, prop.Name) };
                    break;
                case "project":
                    result = result with { Project = ParseObjectDictionary(val, prop.Name) };
                    break;
                case "extra":
                    result = result with { Extra = ParseObjectDictionary(val, prop.Name) };
                    break;
                case "relations":
                    result = result with { Relations = ParseRelations(val, prop.Name) };
                    break;
                case "id":
                    result = result with { Id = RequireString(val, prop.Name) };
                    break;
                default:
                    if (val.ValueKind == JsonValueKind.Null)
                    {
                        unknownKeys.Remove(prop.Name);
                    }
                    else
                    {
                        unknownKeys[prop.Name] = ConvertJsonElement(val);
                    }
                    break;
            }
        }

        return result with { UnknownKeys = unknownKeys };
    }

    private static string RequireString(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.String)
        {
            throw new FormatException($"Frontmatter patch field '{fieldName}' must be a JSON string.");
        }

        return element.GetString() ?? throw new FormatException($"Frontmatter patch field '{fieldName}' cannot be null.");
    }

    private static string? GetOptionalString(JsonElement element, string fieldName)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String => element.GetString(),
            _ => throw new FormatException($"Frontmatter patch field '{fieldName}' must be a JSON string or null.")
        };
    }

    private static TEnum ParseEnum<TEnum>(string raw, string fieldName) where TEnum : struct, Enum
    {
        var normalized = raw.Replace("_", string.Empty, StringComparison.Ordinal);
        if (!Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var value) || !Enum.IsDefined(value))
        {
            throw new FormatException($"Invalid enum value '{raw}' for field '{fieldName}'.");
        }

        return value;
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"Frontmatter patch field '{fieldName}' must be a JSON array of strings.");
        }

        var list = new List<string>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            list.Add(RequireString(item, fieldName));
        }

        return list;
    }

    private static IReadOnlyDictionary<string, string> ParseStringDictionary(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"Frontmatter patch field '{fieldName}' must be a JSON object.");
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = RequireString(prop.Value, $"{fieldName}.{prop.Name}");
        }

        return dict;
    }

    private static IReadOnlyDictionary<string, object?> ParseObjectDictionary(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException($"Frontmatter patch field '{fieldName}' must be a JSON object.");
        }

        return ConvertJsonObject(element);
    }

    private static IReadOnlyList<ResolvedRelation> ParseRelations(JsonElement element, string fieldName)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new FormatException($"Frontmatter patch field '{fieldName}' must be a JSON array.");
        }

        var list = new List<ResolvedRelation>(element.GetArrayLength());
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                throw new FormatException($"Each element of '{fieldName}' must be a JSON object.");
            }

            var srcPath = item.TryGetProperty("src_path", out var srcEl) ? (srcEl.GetString() ?? string.Empty) : string.Empty;
            var dstPath = item.TryGetProperty("dst_path", out var dstEl) ? (dstEl.GetString() ?? string.Empty) : string.Empty;
            var kindRaw = item.TryGetProperty("kind", out var kindEl) ? (kindEl.GetString() ?? "related") : "related";
            var kind = ParseEnum<RelationKind>(kindRaw, $"{fieldName}.kind");
            var weight = item.TryGetProperty("weight", out var weightEl) && weightEl.ValueKind == JsonValueKind.Number
                ? weightEl.GetDouble()
                : 0.0;
            var origin = item.TryGetProperty("origin", out var originEl) ? (originEl.GetString() ?? string.Empty) : string.Empty;
            var reason = item.TryGetProperty("reason", out var reasonEl) ? (reasonEl.GetString() ?? string.Empty) : string.Empty;

            list.Add(new ResolvedRelation(srcPath, dstPath, kind, weight, origin, reason));
        }

        return list;
    }

    private static DateTimeOffset ParseDateTimeOffset(JsonElement element, string fieldName)
    {
        var raw = RequireString(element, fieldName);
        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            throw new FormatException($"Invalid DateTimeOffset '{raw}' for field '{fieldName}'.");
        }

        return parsed;
    }

    private static DateTimeOffset? ParseOptionalDateTimeOffset(JsonElement element, string fieldName)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        return ParseDateTimeOffset(element, fieldName);
    }

    private static Dictionary<string, object?> ConvertJsonObject(JsonElement element)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var prop in element.EnumerateObject())
        {
            dict[prop.Name] = ConvertJsonElement(prop.Value);
        }

        return dict;
    }

    private static object? ConvertJsonElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
            JsonValueKind.Object => ConvertJsonObject(element),
            JsonValueKind.Array => element.EnumerateArray().Select(ConvertJsonElement).ToList(),
            _ => element.GetRawText()
        };
    }
}