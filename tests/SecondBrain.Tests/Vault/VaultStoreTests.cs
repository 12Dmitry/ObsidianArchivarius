using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using SecondBrain.Core.Model;
using SecondBrain.Infrastructure.Vault;
using Xunit;

namespace SecondBrain.Tests.Vault;

public sealed class VaultStoreTests : IDisposable
{
    private static readonly DateTimeOffset FixedTimestamp = new(2026, 11, 21, 12, 0, 0, TimeSpan.Zero);

    private readonly string _vaultRoot;
    private readonly FileSystemVaultStore _store;

    public VaultStoreTests()
    {
        _vaultRoot = Path.Combine(Path.GetTempPath(), $"secondbrain-vault-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_vaultRoot);
        _store = new FileSystemVaultStore(_vaultRoot, TimeProvider.System);
    }

    public void Dispose()
    {
        if (Directory.Exists(_vaultRoot))
        {
            Directory.Delete(_vaultRoot, recursive: true);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("notes\0/a.md")]
    [InlineData("..")]
    [InlineData("../outside.md")]
    [InlineData("notes/../../outside.md")]
    [InlineData(".")]
    [InlineData("./notes/a.md")]
    [InlineData("notes/./a.md")]
    [InlineData("/etc/passwd")]
    [InlineData("\\Windows\\System32\\drivers\\etc\\hosts")]
    [InlineData("C:\\vault\\note.md")]
    [InlineData("C:/vault/note.md")]
    [InlineData("C:note.md")]
    [InlineData("\\\\server\\share\\note.md")]
    [InlineData("notes//a.md")]
    [InlineData("notes/")]
    public void VaultPathPolicy_Rejects_Invalid_And_Escaping_Paths(string invalidPath)
    {
        var act = () => VaultPathPolicy.NormalizeAndResolve(_vaultRoot, invalidPath);
        act.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(".brain/index.md")]
    [InlineData(".BRAIN/cache/doc.md")]
    [InlineData(".obsidian/workspace.md")]
    [InlineData(".git/HEAD")]
    [InlineData("sub/.brain/meta.md")]
    [InlineData("sub/.git/config")]
    public void VaultPathPolicy_Rejects_Forbidden_System_Directories(string forbiddenPath)
    {
        var act = () => VaultPathPolicy.NormalizeAndResolve(_vaultRoot, forbiddenPath);
        act.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void VaultPathPolicy_Normalizes_Backslashes_And_Resolves_Inside_Root()
    {
        var (normalized, fullPath) = VaultPathPolicy.NormalizeAndResolve(_vaultRoot, @"notes\2026\idea.md");

        normalized.Should().Be("notes/2026/idea.md");
        var rootPrefix = Path.GetFullPath(_vaultRoot) + Path.DirectorySeparatorChar;
        fullPath.Should().StartWith(rootPrefix);
        fullPath.Should().EndWith(Path.Combine("notes", "2026", "idea.md"));
    }

    [Fact]
    public async Task WriteAsync_DryRun_On_New_File_Returns_Hash_And_Diff_Without_Creating_File()
    {
        var doc = CreateSampleDocument("inbox/dry-run.md", "id-dry-1", "Dry Run Title", "Body line 1\n");

        var result = await _store.WriteAsync(doc, new WriteOptions(DryRun: true, Reason: "preview"));

        result.Success.Should().BeTrue();
        result.Error.Should().BeNull();
        result.Hash.Should().NotBeNullOrEmpty();
        result.Diff.Should().NotBeNullOrEmpty();
        result.Diff.Should().Contain("+++ b/inbox/dry-run.md");

        var fullPath = Path.Combine(_vaultRoot, "inbox", "dry-run.md");
        File.Exists(fullPath).Should().BeFalse();
        Directory.Exists(Path.Combine(_vaultRoot, "inbox")).Should().BeFalse();
    }

    [Fact]
    public async Task WriteAsync_DryRun_On_Existing_File_Does_Not_Modify_Disk_Content()
    {
        var initial = CreateSampleDocument("notes/existing.md", "id-1", "Initial", "Line 1\n");
        var write1 = await _store.WriteAsync(initial, new WriteOptions());
        write1.Success.Should().BeTrue();

        var fullPath = Path.Combine(_vaultRoot, "notes", "existing.md");
        var bytesBefore = await File.ReadAllBytesAsync(fullPath);

        var updated = initial with
        {
            Metadata = initial.Metadata with { Title = "Updated Title" },
            Body = "Line 1\nLine 2\n"
        };

        var dryResult = await _store.WriteAsync(updated, new WriteOptions(ExpectedHash: write1.Hash, DryRun: true));

        dryResult.Success.Should().BeTrue();
        dryResult.Hash.Should().NotBeNullOrEmpty().And.NotBe(write1.Hash);
        dryResult.Diff.Should().NotBeNullOrEmpty();
        dryResult.Diff.Should().Contain("-title: Initial");
        dryResult.Diff.Should().Contain("+title: Updated Title");

        var bytesAfter = await File.ReadAllBytesAsync(fullPath);
        bytesAfter.Should().Equal(bytesBefore);
    }

    [Fact]
    public async Task WriteAsync_Then_ReadAsync_RoundTrips_Document_And_ContentHash()
    {
        var doc = new NoteDocument(
            @"projects\alpha\decision.md",
            new NoteMetadata
            {
                Id = "dec-001",
                Title = "Use Atomic File Storage",
                Type = NoteType.Decision,
                Usecase = "architecture",
                Status = NoteStatus.Active,
                Tags = ["storage", "atomic"],
                Aliases = ["atomic-storage"],
                Summary = "Atomic replacement guarantees no torn reads.",
                Created = FixedTimestamp,
                Updated = FixedTimestamp.AddMinutes(5)
            },
            "# Decision\n\nWrite to temp file in same directory and replace atomically.\n",
            string.Empty,
            FixedTimestamp);

        var writeResult = await _store.WriteAsync(doc, new WriteOptions(ExpectedHash: "", Reason: "create"));

        writeResult.Success.Should().BeTrue();
        writeResult.Hash.Should().NotBeNullOrEmpty();

        var loaded = await _store.ReadAsync("projects/alpha/decision.md");

        loaded.Should().NotBeNull();
        loaded!.Path.Should().Be("projects/alpha/decision.md");
        loaded.ContentHash.Should().Be(writeResult.Hash);
        loaded.Metadata.Id.Should().Be("dec-001");
        loaded.Metadata.Title.Should().Be("Use Atomic File Storage");
        loaded.Metadata.Type.Should().Be(NoteType.Decision);
        loaded.Metadata.Tags.Should().Equal("storage", "atomic");
        loaded.Metadata.Aliases.Should().Equal("atomic-storage");
        loaded.Body.Should().Be(doc.Body);
    }

    [Fact]
    public async Task ReadAsync_Returns_Null_When_Missing_And_Throws_FormatException_When_Invalid()
    {
        var missing = await _store.ReadAsync("missing/note.md");
        missing.Should().BeNull();

        var invalidPath = Path.Combine(_vaultRoot, "broken.md");
        await AtomicWriter.WriteAllTextAtomicAsync(invalidPath, "# Just markdown without frontmatter\n");

        var act = async () => await _store.ReadAsync("broken.md");
        var ex = await act.Should().ThrowAsync<FormatException>();
        ex.Which.Message.Should().Contain("broken.md");
    }

    [Fact]
    public async Task WriteAsync_Preserves_Crlf_LineEndings_On_Existing_File()
    {
        var crlfDoc = CreateSampleDocument("notes/crlf.md", "crlf-1", "CRLF Note", "Body\r\n");
        var serializedCrlf = FrontmatterCodec.Serialize(crlfDoc, "\r\n");
        var fullPath = Path.Combine(_vaultRoot, "notes", "crlf.md");
        await AtomicWriter.WriteAllTextAtomicAsync(fullPath, serializedCrlf);

        var loaded = await _store.ReadAsync("notes/crlf.md");
        loaded.Should().NotBeNull();

        var updated = loaded! with { Body = "Updated Body\r\n" };
        var writeResult = await _store.WriteAsync(updated, new WriteOptions(ExpectedHash: loaded.ContentHash));
        writeResult.Success.Should().BeTrue();

        var diskContent = await File.ReadAllTextAsync(fullPath, Encoding.UTF8);
        diskContent.Should().Contain("\r\n");
        diskContent.Replace("\r\n", string.Empty, StringComparison.Ordinal).Should().NotContain("\n");
    }

    [Fact]
    public async Task WriteAsync_Enforces_Optimistic_Concurrency_And_Hash_Conflicts()
    {
        var v1 = CreateSampleDocument("notes/concurrent.md", "c-1", "Version 1", "Body v1\n");
        var r1 = await _store.WriteAsync(v1, new WriteOptions());
        r1.Success.Should().BeTrue();
        var h1 = r1.Hash!;

        var v2 = v1 with
        {
            Metadata = v1.Metadata with { Title = "Version 2" },
            Body = "Body v2\n"
        };
        var r2 = await _store.WriteAsync(v2, new WriteOptions(ExpectedHash: h1));
        r2.Success.Should().BeTrue();
        var h2 = r2.Hash!;
        h2.Should().NotBe(h1);

        var stale = v1 with
        {
            Metadata = v1.Metadata with { Title = "Stale Write" },
            Body = "Should not overwrite\n"
        };
        var staleResult = await _store.WriteAsync(stale, new WriteOptions(ExpectedHash: h1));

        staleResult.Success.Should().BeFalse();
        staleResult.Hash.Should().Be(h2);
        staleResult.Diff.Should().BeNull();
        staleResult.Error.Should().NotBeNullOrEmpty();

        var emptyHashOnExisting = await _store.WriteAsync(stale, new WriteOptions(ExpectedHash: ""));
        emptyHashOnExisting.Success.Should().BeFalse();
        emptyHashOnExisting.Hash.Should().Be(h2);
        emptyHashOnExisting.Error.Should().NotBeNullOrEmpty();

        var missingWithHash = await _store.WriteAsync(
            CreateSampleDocument("notes/does-not-exist.md", "c-2", "Missing", "Body\n"),
            new WriteOptions(ExpectedHash: h1));
        missingWithHash.Success.Should().BeFalse();
        missingWithHash.Hash.Should().BeNull();
        missingWithHash.Error.Should().NotBeNullOrEmpty();

        var current = await _store.ReadAsync("notes/concurrent.md");
        current.Should().NotBeNull();
        current!.ContentHash.Should().Be(h2);
        current.Metadata.Title.Should().Be("Version 2");
    }

    [Fact]
    public async Task DeleteAsync_Checks_ExpectedHash_Before_Deleting()
    {
        var doc = CreateSampleDocument("notes/to-delete.md", "del-1", "Delete Me", "Content\n");
        var writeResult = await _store.WriteAsync(doc, new WriteOptions());
        writeResult.Success.Should().BeTrue();
        var hash = writeResult.Hash!;

        var wrongHashDelete = await _store.DeleteAsync("notes/to-delete.md", "0000000000000000");
        wrongHashDelete.Should().BeFalse();
        (await _store.ReadAsync("notes/to-delete.md")).Should().NotBeNull();

        var missingDelete = await _store.DeleteAsync("notes/not-here.md", hash);
        missingDelete.Should().BeFalse();

        var rightHashDelete = await _store.DeleteAsync("notes/to-delete.md", hash);
        rightHashDelete.Should().BeTrue();
        (await _store.ReadAsync("notes/to-delete.md")).Should().BeNull();
    }

    [Fact]
    public async Task ListAsync_Filters_By_Prefix_Ignores_System_Folders_And_Handles_Raw_Markdown()
    {
        await _store.WriteAsync(
            CreateSampleDocument("projects/a.md", "p-1", "Project A", "Alpha\n"),
            new WriteOptions());
        await _store.WriteAsync(
            CreateSampleDocument("projects/sub/b.md", "p-2", "Project B", "Beta\n"),
            new WriteOptions());
        await _store.WriteAsync(
            CreateSampleDocument("inbox/note.md", "i-1", "Inbox Note", "Gamma\n"),
            new WriteOptions());

        // Raw markdown file without frontmatter
        var rawInboxPath = Path.Combine(_vaultRoot, "projects", "raw-capture.md");
        await AtomicWriter.WriteAllTextAtomicAsync(rawInboxPath, "Just a raw thought without YAML.\n");

        // System directories that must be ignored
        await AtomicWriter.WriteAllTextAtomicAsync(
            Path.Combine(_vaultRoot, ".brain", "state.md"),
            "---\nid: s1\ntitle: Internal\ntype: insight\n---\n");
        await AtomicWriter.WriteAllTextAtomicAsync(
            Path.Combine(_vaultRoot, ".obsidian", "plugins.md"),
            "obsidian config");
        await AtomicWriter.WriteAllTextAtomicAsync(
            Path.Combine(_vaultRoot, ".git", "COMMIT_EDITMSG.md"),
            "commit msg");

        var allEntries = await _store.ListAsync();
        allEntries.Select(e => e.Path).Should().Equal(
            "inbox/note.md",
            "projects/a.md",
            "projects/raw-capture.md",
            "projects/sub/b.md");

        var projectEntries = await _store.ListAsync("projects/");
        projectEntries.Should().HaveCount(3);
        projectEntries.Select(e => e.Path).Should().Equal(
            "projects/a.md",
            "projects/raw-capture.md",
            "projects/sub/b.md");

        var rawEntry = projectEntries.Single(e => e.Path == "projects/raw-capture.md");
        rawEntry.Type.Should().BeNull();
        rawEntry.Title.Should().Be("raw-capture");
        rawEntry.SizeBytes.Should().BeGreaterThan(0);

        var parsedEntry = projectEntries.Single(e => e.Path == "projects/a.md");
        parsedEntry.Type.Should().Be(NoteType.Insight);
        parsedEntry.Title.Should().Be("Project A");
    }

    [Fact]
    public async Task Stress_100_Sequential_Cycles_And_Concurrent_Writers_Leave_No_Tmp_Files()
    {
        const string notePath = "stress/counter.md";

        var initial = CreateSampleDocument(notePath, "stress-1", "Cycle 0", "Body 0\n");
        var created = await _store.WriteAsync(initial, new WriteOptions(ExpectedHash: ""));
        created.Success.Should().BeTrue();
        var currentHash = created.Hash!;

        for (var cycle = 1; cycle <= 100; cycle++)
        {
            var readDoc = await _store.ReadAsync(notePath);
            readDoc.Should().NotBeNull();
            readDoc!.ContentHash.Should().Be(currentHash);

            var nextDoc = readDoc with
            {
                Metadata = readDoc.Metadata with
                {
                    Title = $"Cycle {cycle}",
                    Updated = FixedTimestamp.AddSeconds(cycle)
                },
                Body = $"Body {cycle}\n"
            };

            var updateResult = await _store.WriteAsync(nextDoc, new WriteOptions(ExpectedHash: currentHash));
            updateResult.Success.Should().BeTrue();
            updateResult.Hash.Should().NotBeNullOrEmpty().And.NotBe(currentHash);
            currentHash = updateResult.Hash!;
        }

        // Concurrent writers racing with the same ExpectedHash: exactly 1 must succeed.
        var baseDoc = await _store.ReadAsync(notePath);
        baseDoc.Should().NotBeNull();

        const int writerCount = 16;
        var startGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var tasks = Enumerable.Range(0, writerCount).Select(async writerId =>
        {
            await startGate.Task;
            var candidate = baseDoc! with
            {
                Metadata = baseDoc.Metadata with { Title = $"Winner {writerId}" },
                Body = $"Concurrent body from writer {writerId}\n"
            };
            return await _store.WriteAsync(candidate, new WriteOptions(ExpectedHash: currentHash));
        }).ToArray();

        startGate.SetResult();
        var results = await Task.WhenAll(tasks);

        var winners = results.Where(r => r.Success).ToList();
        var conflicts = results.Where(r => !r.Success).ToList();

        winners.Should().ContainSingle();
        conflicts.Should().HaveCount(writerCount - 1);
        conflicts.Should().OnlyContain(c => c.Error != null && c.Hash == winners[0].Hash);

        var finalDoc = await _store.ReadAsync(notePath);
        finalDoc.Should().NotBeNull();
        finalDoc!.ContentHash.Should().Be(winners[0].Hash);

        var tmpFiles = Directory.GetFiles(_vaultRoot, "*.tmp", SearchOption.AllDirectories);
        tmpFiles.Should().BeEmpty();

        var finalBytes = await File.ReadAllBytesAsync(Path.Combine(_vaultRoot, "stress", "counter.md"));
        var diskHash = Convert.ToHexStringLower(SHA256.HashData(finalBytes));
        diskHash.Should().Be(winners[0].Hash);
    }

    private static NoteDocument CreateSampleDocument(string path, string id, string title, string body)
    {
        return new NoteDocument(
            path,
            new NoteMetadata
            {
                Id = id,
                Title = title,
                Type = NoteType.Insight,
                Created = FixedTimestamp,
                Updated = FixedTimestamp
            },
            body,
            string.Empty,
            FixedTimestamp);
    }
}