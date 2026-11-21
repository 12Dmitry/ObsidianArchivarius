using System.Text.Json;
using Microsoft.Data.Sqlite;
using SecondBrain.Cli;
using Xunit;

namespace SecondBrain.Tests.Cli;

public sealed class CliSmokeTests : IDisposable
{
    private readonly string _vaultPath;
    private readonly FixedTimeProvider _timeProvider = new(
        new DateTimeOffset(2026, 11, 21, 10, 30, 0, TimeSpan.Zero));

    public CliSmokeTests()
    {
        _vaultPath = Path.Combine(
            Path.GetTempPath(),
            $"secondbrain-cli-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_vaultPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(_vaultPath))
        {
            Directory.Delete(_vaultPath, recursive: true);
        }
    }

    [Fact]
    public async Task Doctor_OnEmptyVault_ReturnsZeroAndPrintsNotesCount()
    {
        var (exitCode, stdout, stderr) = await RunCliAsync("doctor", "--vault", _vaultPath);

        Assert.Equal(0, exitCode);
        Assert.Equal(string.Empty, stderr);
        Assert.Contains("NotesCount: 0", stdout, StringComparison.Ordinal);
        Assert.Contains("Chunks: 0", stdout, StringComparison.Ordinal);
        Assert.Contains("Vectors: 0", stdout, StringComparison.Ordinal);
        Assert.Contains("EmbeddingModel:", stdout, StringComparison.Ordinal);
        Assert.Contains(Path.GetFullPath(_vaultPath), stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_OnMissingVault_ReturnsOneAndWritesError()
    {
        var missingPath = Path.Combine(_vaultPath, "missing-vault");

        var (exitCode, _, stderr) = await RunCliAsync("doctor", "--vault", missingPath);

        Assert.Equal(1, exitCode);
        Assert.False(string.IsNullOrWhiteSpace(stderr));
    }

    [Fact]
    public async Task Capture_WithoutApply_IsDryRun_AndWithApply_CreatesInsightNote()
    {
        const string noteText = ">> type: insight\n# Векторный поиск\n\nЛокальный гибридный поиск по заметкам.";

        var dryRun = await RunCliAsync("capture", "--vault", _vaultPath, noteText);

        Assert.Equal(0, dryRun.ExitCode);
        Assert.Equal(string.Empty, dryRun.Stderr);
        Assert.Contains("DRY-RUN", dryRun.Stdout, StringComparison.Ordinal);
        Assert.Contains("Diff:", dryRun.Stdout, StringComparison.Ordinal);
        Assert.Contains("40_Insights/", dryRun.Stdout, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_vaultPath, "*.md", SearchOption.AllDirectories));

        var applied = await RunCliAsync("capture", "--vault", _vaultPath, "--apply", noteText);

        Assert.Equal(0, applied.ExitCode);
        Assert.Equal(string.Empty, applied.Stderr);
        Assert.Contains("APPLIED", applied.Stdout, StringComparison.Ordinal);
        Assert.Contains("40_Insights/", applied.Stdout, StringComparison.Ordinal);

        var insightsDir = Path.Combine(_vaultPath, "40_Insights");
        Assert.True(Directory.Exists(insightsDir));
        var createdFiles = Directory.GetFiles(insightsDir, "*.md");
        Assert.Single(createdFiles);
    }

    [Fact]
    public async Task Diary_AppliesByDefault_AndStatsReportsOneFileAndOneEntry()
    {
        var append = await RunCliAsync(
            "diary",
            "--vault",
            _vaultPath,
            "Сегодня настроил локальный дневник и проверил индекс");

        Assert.Equal(0, append.ExitCode);
        Assert.Equal(string.Empty, append.Stderr);
        Assert.Contains("APPLIED", append.Stdout, StringComparison.Ordinal);
        Assert.Contains("20_Areas/Diary/", append.Stdout, StringComparison.Ordinal);

        var dryRun = await RunCliAsync(
            "diary",
            "--vault",
            _vaultPath,
            "--dry-run",
            "Черновая запись не должна попасть в файл");

        Assert.Equal(0, dryRun.ExitCode);
        Assert.Contains("DRY-RUN", dryRun.Stdout, StringComparison.Ordinal);

        var stats = await RunCliAsync("diary", "--vault", _vaultPath, "--stats");

        Assert.Equal(0, stats.ExitCode);
        Assert.Equal(string.Empty, stats.Stderr);
        Assert.Contains("FilesCount: 1", stats.Stdout, StringComparison.Ordinal);
        Assert.Contains("EntriesCount: 1", stats.Stdout, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_Show_And_RebuildIndex_WorkEndToEnd()
    {
        var capture = await RunCliAsync(
            "capture",
            "--vault",
            _vaultPath,
            "--apply",
            "--json",
            ">> type: insight\n# Архитектура поиска\n\nТриграммный индекс ускоряет локальный поиск.");

        Assert.Equal(0, capture.ExitCode);
        Assert.Equal(string.Empty, capture.Stderr);

        using var captureDoc = JsonDocument.Parse(capture.Stdout);
        var insightPath = captureDoc.RootElement.GetProperty("Path").GetString() ?? string.Empty;
        Assert.StartsWith("40_Insights/", insightPath, StringComparison.Ordinal);

        var diary = await RunCliAsync(
            "diary",
            "--vault",
            _vaultPath,
            "Вечерняя рефлексия про маршрутизатор и тесты CLI");

        Assert.Equal(0, diary.ExitCode);
        Assert.Equal(string.Empty, diary.Stderr);

        var searchInsight = await RunCliAsync(
            "search",
            "--vault",
            _vaultPath,
            "--type",
            "insight",
            "триграммный");

        Assert.Equal(0, searchInsight.ExitCode);
        Assert.Equal(string.Empty, searchInsight.Stderr);
        Assert.Contains(insightPath, searchInsight.Stdout, StringComparison.Ordinal);
        Assert.Contains("LexicalMs:", searchInsight.Stdout, StringComparison.Ordinal);

        var searchDiary = await RunCliAsync(
            "search",
            "--vault",
            _vaultPath,
            "маршрутизатор");

        Assert.Equal(0, searchDiary.ExitCode);
        Assert.Equal(string.Empty, searchDiary.Stderr);
        Assert.Contains("20_Areas/Diary/", searchDiary.Stdout, StringComparison.Ordinal);
        Assert.Contains("LexicalMs:", searchDiary.Stdout, StringComparison.Ordinal);

        var show = await RunCliAsync("show", "--vault", _vaultPath, insightPath);

        Assert.Equal(0, show.ExitCode);
        Assert.Equal(string.Empty, show.Stderr);
        Assert.Contains("Id:", show.Stdout, StringComparison.Ordinal);
        Assert.Contains("Title: Архитектура поиска", show.Stdout, StringComparison.Ordinal);
        Assert.Contains("Type: Insight", show.Stdout, StringComparison.Ordinal);
        Assert.Contains("Status:", show.Stdout, StringComparison.Ordinal);
        Assert.Contains("Tags:", show.Stdout, StringComparison.Ordinal);
        Assert.Contains("Up:", show.Stdout, StringComparison.Ordinal);
        Assert.Contains("Created:", show.Stdout, StringComparison.Ordinal);
        Assert.Contains("Updated:", show.Stdout, StringComparison.Ordinal);
        Assert.Contains("Триграммный индекс ускоряет локальный поиск.", show.Stdout, StringComparison.Ordinal);

        var showMissing = await RunCliAsync("show", "--vault", _vaultPath, "40_Insights/missing.md");
        Assert.Equal(1, showMissing.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(showMissing.Stderr));

        var rebuild = await RunCliAsync("rebuild-index", "--vault", _vaultPath, "--json");

        Assert.Equal(0, rebuild.ExitCode);
        Assert.Equal(string.Empty, rebuild.Stderr);

        using var rebuildDoc = JsonDocument.Parse(rebuild.Stdout);
        var chunks = rebuildDoc.RootElement.GetProperty("Chunks").GetInt32();
        Assert.True(chunks >= 2);
        Assert.True(rebuildDoc.RootElement.TryGetProperty("BuiltAt", out _));
    }

    [Fact]
    public async Task Help_And_ErrorHandling_ReturnExpectedExitCodes()
    {
        var empty = await RunCliAsync();
        Assert.Equal(0, empty.ExitCode);
        Assert.Contains("doctor", empty.Stdout, StringComparison.Ordinal);
        Assert.Contains("rebuild-index", empty.Stdout, StringComparison.Ordinal);

        var help = await RunCliAsync("--help");
        Assert.Equal(0, help.ExitCode);

        var unknown = await RunCliAsync("unknown-command", "--vault", _vaultPath);
        Assert.Equal(1, unknown.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(unknown.Stderr));

        var missingCaptureText = await RunCliAsync("capture", "--vault", _vaultPath);
        Assert.Equal(1, missingCaptureText.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(missingCaptureText.Stderr));

        var missingSearchQuery = await RunCliAsync("search", "--vault", _vaultPath);
        Assert.Equal(1, missingSearchQuery.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(missingSearchQuery.Stderr));

        var missingShowPath = await RunCliAsync("show", "--vault", _vaultPath);
        Assert.Equal(1, missingShowPath.ExitCode);
        Assert.False(string.IsNullOrWhiteSpace(missingShowPath.Stderr));
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(params string[] args)
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        var app = new CliApp(stdout, stderr, _timeProvider);
        var exitCode = await app.RunAsync(args);
        return (exitCode, stdout.ToString(), stderr.ToString());
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}