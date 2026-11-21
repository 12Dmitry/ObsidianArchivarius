using System.Text;
using System.Text.Json;
using FluentAssertions;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;
using SecondBrain.Infrastructure.Vault;
using Xunit;

namespace SecondBrain.Tests.Vault;

public sealed class WriteJournalTests : IDisposable
{
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _vaultRoot;
    private readonly FileSystemVaultStore _store;
    private readonly JsonlWriteJournal _journal;
    private readonly WritePlanExecutor _executor;

    public WriteJournalTests()
    {
        _vaultRoot = Path.Combine(Path.GetTempPath(), $"secondbrain-journal-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_vaultRoot);
        _store = new FileSystemVaultStore(_vaultRoot, TimeProvider.System);
        _journal = new JsonlWriteJournal(_vaultRoot, TimeProvider.System);
        _executor = new WritePlanExecutor(_store, _journal, TimeProvider.System);
    }

    public void Dispose()
    {
        if (Directory.Exists(_vaultRoot))
        {
            Directory.Delete(_vaultRoot, recursive: true);
        }
    }

    [Fact]
    public async Task RollbackAsync_After_Create_Deletes_Created_File_And_Is_Idempotent()
    {
        var noteMarkdown = SerializeSampleNote("notes/new-note.md", "id-new-1", "New Note", NoteType.Fleeting, "Created body\n");
        var op = new WriteOperation("create", "notes/new-note.md", "", noteMarkdown, "test create");

        var entry = await _journal.BeginAsync(op);
        var doc = FrontmatterCodec.Parse("notes/new-note.md", noteMarkdown, FixedTimestamp);
        var writeResult = await _store.WriteAsync(doc, new WriteOptions(ExpectedHash: ""));
        writeResult.Success.Should().BeTrue();
        await _journal.CommitAsync(entry.OperationId, writeResult);

        var fullPath = Path.Combine(_vaultRoot, "notes", "new-note.md");
        File.Exists(fullPath).Should().BeTrue();

        await _journal.RollbackAsync(entry.OperationId);
        File.Exists(fullPath).Should().BeFalse();
        (await _store.ReadAsync("notes/new-note.md")).Should().BeNull();

        // Idempotent second rollback
        await _journal.RollbackAsync(entry.OperationId);
        File.Exists(fullPath).Should().BeFalse();

        var missingAct = async () => await _journal.RollbackAsync(Guid.NewGuid());
        await missingAct.Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task RollbackAsync_Restores_Utf8Bom_And_Crlf_File_ByteForByte_After_ReplaceManagedBlock()
    {
        const string relativePath = "10_Projects/bom-crlf.md";
        var fullPath = Path.Combine(_vaultRoot, "10_Projects", "bom-crlf.md");

        var rawTextWithCrlf =
            "---\r\n" +
            "id: proj-bom-1\r\n" +
            "title: Project With BOM And CRLF\r\n" +
            "type: project\r\n" +
            "created: 2026-11-21T12:00:00.0000000+00:00\r\n" +
            "updated: 2026-11-21T12:00:00.0000000+00:00\r\n" +
            "---\r\n" +
            "# Manual Header (keep byte-for-byte)\r\n\r\n" +
            "Handwritten paragraph before marker.\r\n\r\n" +
            "<!-- brain:links:begin -->\r\n" +
            "- [[old-link]]\r\n" +
            "<!-- brain:links:end -->\r\n\r\n" +
            "Handwritten footer after marker — keep me!\r\n";

        var utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
        var preamble = utf8WithBom.GetPreamble();
        var bodyBytes = utf8WithBom.GetBytes(rawTextWithCrlf);
        var originalBytes = new byte[preamble.Length + bodyBytes.Length];
        Buffer.BlockCopy(preamble, 0, originalBytes, 0, preamble.Length);
        Buffer.BlockCopy(bodyBytes, 0, originalBytes, preamble.Length, bodyBytes.Length);

        await AtomicWriter.WriteAllBytesAtomicAsync(fullPath, originalBytes);

        var payload = JsonSerializer.Serialize(new ManagedBlockPayload("links", "- [[new-insight|New Insight]]"));
        var plan = new WritePlan(
            "run-bom-1",
            [new WriteOperation("replace_managed_block", relativePath, null, payload, "update links")],
            []);

        var execResult = await _executor.ExecuteAsync(plan);
        execResult.Success.Should().BeTrue();
        execResult.CommittedOperationIds.Should().ContainSingle();

        var modifiedDoc = await _store.ReadAsync(relativePath);
        modifiedDoc.Should().NotBeNull();
        ManagedBlockCodec.ExtractBlock(modifiedDoc!.Body, "links").Should().Be("- [[new-insight|New Insight]]");

        await _journal.RollbackAsync(execResult.CommittedOperationIds[0]);

        var restoredBytes = await File.ReadAllBytesAsync(fullPath);
        restoredBytes.SequenceEqual(originalBytes).Should().BeTrue();
    }

    [Fact]
    public async Task WritePlanExecutor_ReplaceManagedBlock_Preserves_Surrounding_Manual_Edits_ByteForByte()
    {
        const string relativePath = "10_Projects/manual-edits.md";
        const string beforeBlock =
            "# Project Overview\n\n" +
            "My manual notes with special spacing:   keep   me!\n" +
            "```markdown\n<!-- brain:links:begin -->\nfence example\n<!-- brain:links:end -->\n```\n\n";
        const string afterBlock =
            "\n## Personal Scratchpad\n- TODO item written by human\n  - nested bullet\n";

        var initialBody =
            beforeBlock +
            "<!-- brain:links:begin -->\n- [[old]]\n<!-- brain:links:end -->\n" +
            afterBlock;

        var initialMd = SerializeSampleNote(relativePath, "proj-manual", "Manual Edits", NoteType.Project, initialBody);
        await _store.WriteAsync(FrontmatterCodec.Parse(relativePath, initialMd, FixedTimestamp), new WriteOptions());

        var payload = JsonSerializer.Serialize(new ManagedBlockPayload("links", "- [[insight-1]]\n- [[insight-2]]"));
        var plan = new WritePlan(
            "run-preserve-1",
            [new WriteOperation("replace_managed_block", relativePath, null, payload, "sync links")],
            []);

        var result = await _executor.ExecuteAsync(plan);

        result.Success.Should().BeTrue();

        var updated = await _store.ReadAsync(relativePath);
        updated.Should().NotBeNull();
        updated!.Body.Should().StartWith(beforeBlock);
        updated.Body.Should().EndWith(afterBlock);
        ManagedBlockCodec.ExtractBlock(updated.Body, "links").Should().Be("- [[insight-1]]\n- [[insight-2]]");
    }

    [Fact]
    public async Task WritePlanExecutor_Rolls_Back_Op1_In_Lifo_Order_When_Op2_Fails_With_Wrong_ExpectedHash()
    {
        const string projectPath = "10_Projects/alpha.md";
        const string insightPath = "40_Insights/insight-1.md";

        var alphaBody =
            "# Alpha\n\nHuman notes.\n\n<!-- brain:links:begin -->\n- [[existing]]\n<!-- brain:links:end -->\n";
        var alphaMd = SerializeSampleNote(projectPath, "proj-alpha", "Alpha", NoteType.Project, alphaBody);
        await _store.WriteAsync(FrontmatterCodec.Parse(projectPath, alphaMd, FixedTimestamp), new WriteOptions());

        var alphaFullPath = Path.Combine(_vaultRoot, "10_Projects", "alpha.md");
        var alphaBytesBefore = await File.ReadAllBytesAsync(alphaFullPath);

        var insightMd = SerializeSampleNote(insightPath, "ins-1", "Insight 1", NoteType.Insight, "# Insight 1\n");
        var backlinkPayload = JsonSerializer.Serialize(new ManagedBlockPayload("links", "- [[existing]]\n- [[insight-1]]"));

        var plan = new WritePlan(
            "run-fail-mid",
            [
                new WriteOperation("create", insightPath, "", insightMd, "create insight"),
                new WriteOperation("replace_managed_block", projectPath, "wrong-stale-hash", backlinkPayload, "add backlink")
            ],
            []);

        var result = await _executor.ExecuteAsync(plan);

        result.Success.Should().BeFalse();
        result.CommittedOperationIds.Should().BeEmpty();
        result.Error.Should().NotBeNullOrEmpty();

        // Op 1 must have been rolled back
        File.Exists(Path.Combine(_vaultRoot, "40_Insights", "insight-1.md")).Should().BeFalse();
        (await _store.ReadAsync(insightPath)).Should().BeNull();

        // Op 2 target must remain unchanged
        var alphaBytesAfter = await File.ReadAllBytesAsync(alphaFullPath);
        alphaBytesAfter.SequenceEqual(alphaBytesBefore).Should().BeTrue();
    }

    [Fact]
    public async Task RollbackRunAsync_After_Successful_Plan_Deletes_New_Note_And_Restores_Backlink_ByteForByte()
    {
        const string projectPath = "10_Projects/alpha.md";
        const string insightPath = "40_Insights/insight-2.md";

        var alphaBody =
            "# Alpha\n\nManual section outside managed block.\n\n" +
            "<!-- brain:links:begin -->\n- [[seed]]\n<!-- brain:links:end -->\n\n" +
            "Trailing manual note.\n";
        var alphaMd = SerializeSampleNote(projectPath, "proj-alpha", "Alpha", NoteType.Project, alphaBody);
        await _store.WriteAsync(FrontmatterCodec.Parse(projectPath, alphaMd, FixedTimestamp), new WriteOptions());

        var alphaFullPath = Path.Combine(_vaultRoot, "10_Projects", "alpha.md");
        var alphaBytesBefore = await File.ReadAllBytesAsync(alphaFullPath);

        var insightMd = SerializeSampleNote(insightPath, "ins-2", "Insight 2", NoteType.Insight, "# Insight 2\n");
        var backlinkPayload = JsonSerializer.Serialize(new ManagedBlockPayload("links", "- [[seed]]\n- [[insight-2]]"));

        var plan = new WritePlan(
            "run-full-rollback",
            [
                new WriteOperation("create", insightPath, "", insightMd, "create insight 2"),
                new WriteOperation("replace_managed_block", projectPath, null, backlinkPayload, "link insight 2")
            ],
            []);

        var result = await _executor.ExecuteAsync(plan);
        result.Success.Should().BeTrue();
        result.CommittedOperationIds.Should().HaveCount(2);

        (await _store.ReadAsync(insightPath)).Should().NotBeNull();
        var updatedAlpha = await _store.ReadAsync(projectPath);
        ManagedBlockCodec.ExtractBlock(updatedAlpha!.Body, "links").Should().Be("- [[seed]]\n- [[insight-2]]");

        // Now rollback the entire run
        await _journal.RollbackRunAsync(plan.RunId);

        (await _store.ReadAsync(insightPath)).Should().BeNull();
        File.Exists(Path.Combine(_vaultRoot, "40_Insights", "insight-2.md")).Should().BeFalse();

        var alphaBytesAfter = await File.ReadAllBytesAsync(alphaFullPath);
        alphaBytesAfter.SequenceEqual(alphaBytesBefore).Should().BeTrue();
    }

    [Fact]
    public async Task New_JsonlWriteJournal_Instance_Recovers_And_Rolls_Back_Previous_Process_Operation()
    {
        const string notePath = "notes/crash-recovery.md";
        var fullPath = Path.Combine(_vaultRoot, "notes", "crash-recovery.md");

        var originalMd = SerializeSampleNote(notePath, "crash-1", "Before Crash", NoteType.Decision, "Stable content\n");
        await _store.WriteAsync(FrontmatterCodec.Parse(notePath, originalMd, FixedTimestamp), new WriteOptions());
        var originalBytes = await File.ReadAllBytesAsync(fullPath);

        // First journal instance starts an operation (simulates crash before CommitAsync)
        var journalBeforeCrash = new JsonlWriteJournal(_vaultRoot, TimeProvider.System);
        var entry = await journalBeforeCrash.BeginAsync(
            new WriteOperation("patch_frontmatter", notePath, null, """{"status":"done"}""", "crash test"),
            runId: "crash-run-1");

        // Simulate partial/uncommitted overwrite on disk before crash
        await AtomicWriter.WriteAllTextAtomicAsync(fullPath, "CORRUPTED_OR_PARTIAL_STATE\n");

        // New instance on the same vaultRoot after restart
        var journalAfterRestart = new JsonlWriteJournal(_vaultRoot, TimeProvider.System);
        await journalAfterRestart.RollbackAsync(entry.OperationId);

        var restoredBytes = await File.ReadAllBytesAsync(fullPath);
        restoredBytes.SequenceEqual(originalBytes).Should().BeTrue();
    }

    [Fact]
    public async Task WritePlanExecutor_Supports_PatchFrontmatter_AppendEntry_And_DryRun()
    {
        const string notePath = "notes/patch-and-append.md";
        var initialMd = SerializeSampleNote(notePath, "pa-1", "Initial", NoteType.Task, "Line 1\n");
        await _store.WriteAsync(FrontmatterCodec.Parse(notePath, initialMd, FixedTimestamp), new WriteOptions());

        var patchPayload = """
            {
              "status": "on_hold",
              "tags": ["p3", "journal"],
              "processed": true,
              "reviewed_at": "2026-11-21T15:30:00Z",
              "decision_log": { "owner": "system", "score": 9 }
            }
            """;

        // Dry run must not change disk state
        var dryPlan = new WritePlan(
            "run-dry",
            [new WriteOperation("patch_frontmatter", notePath, null, patchPayload, "dry patch")],
            []);
        var dryResult = await _executor.ExecuteAsync(dryPlan, dryRun: true);
        dryResult.Success.Should().BeTrue();
        dryResult.CommittedOperationIds.Should().BeEmpty();

        var afterDry = await _store.ReadAsync(notePath);
        afterDry!.Metadata.Status.Should().Be(NoteStatus.Active);
        afterDry.Metadata.Tags.Should().BeEmpty();

        // Real patch + append
        var realPlan = new WritePlan(
            "run-patch-append",
            [
                new WriteOperation("patch_frontmatter", notePath, null, patchPayload, "patch fields"),
                new WriteOperation("append_entry", notePath, null, "Appended line 2\n", "append text")
            ],
            []);

        var execResult = await _executor.ExecuteAsync(realPlan);
        execResult.Success.Should().BeTrue();
        execResult.CommittedOperationIds.Should().HaveCount(2);

        var finalDoc = await _store.ReadAsync(notePath);
        finalDoc.Should().NotBeNull();
        finalDoc!.Metadata.Status.Should().Be(NoteStatus.OnHold);
        finalDoc.Metadata.Tags.Should().Equal("p3", "journal");
        finalDoc.Metadata.Processed.Should().BeTrue();
        finalDoc.Metadata.ReviewedAt.Should().Be(new DateTimeOffset(2026, 11, 21, 15, 30, 0, TimeSpan.Zero));
        finalDoc.Metadata.DecisionLog.Should().ContainKey("owner");
        finalDoc.Body.Should().Be("Line 1\nAppended line 2\n");
    }

    private static string SerializeSampleNote(
        string path,
        string id,
        string title,
        NoteType type,
        string body)
    {
        var doc = new NoteDocument(
            path,
            new NoteMetadata
            {
                Id = id,
                Title = title,
                Type = type,
                Created = FixedTimestamp,
                Updated = FixedTimestamp
            },
            body,
            string.Empty,
            FixedTimestamp);

        return FrontmatterCodec.Serialize(doc);
    }
}