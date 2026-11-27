using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using SecondBrain.Cli;
using SecondBrain.Core.Eval;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;
using SecondBrain.Infrastructure.Eval;
using SecondBrain.Infrastructure.Vault;
using Xunit;

namespace SecondBrain.Tests.Eval;

public sealed class LinkEvalRunnerTests
{
    private static readonly DateTimeOffset BaseNow = new(2026, 11, 24, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task RunAsync_RunsFiveAblationConfigs_ReusesVectors_RejectsOutOfCandidateLinks_AndWritesReports()
    {
        using var tempVault = new TempDirectoryScope();
        IReadOnlyList<NoteDocument> corpus = BuildSyntheticCorpus();
        IReadOnlyList<LinkEvalCase> cases = BuildSyntheticCases();

        var embeddingClient = new DeterministicTopicEmbeddingClient();
        var capturedUserPrompts = new List<string>();
        var chatClient = Substitute.For<IChatClient>();
        var chatRouter = Substitute.For<IChatRouter>();
        chatRouter.Resolve("extract").Returns(chatClient);
        chatRouter.Describe("extract").Returns(new LlmProfile(
            Provider: "ollama",
            Model: "qwen-fake-7b",
            NumCtx: 8192,
            Temperature: 0.0,
            TopP: 1.0,
            TopK: 40,
            PresencePenalty: 0.0,
            MaxOutputTokens: 512,
            TimeoutSeconds: 60));

        chatClient.ExtractAsync<EvalLlmSelection>(
                Arg.Any<string>(),
                Arg.Do<string>(prompt => capturedUserPrompts.Add(prompt)),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new EvalLlmSelection(
                Related: ["C1", "C99"],
                Up: ["C99"],
                DuplicateOf: "C99",
                Action: "new"));

        var clock = new MutableTimeProvider(BaseNow);
        var runner = new LinkEvalRunner(tempVault.Path, corpus, embeddingClient, chatRouter, clock);

        IReadOnlyList<AblationResult> results = await runner.RunAsync(
            cases,
            LinkEvalRunner.StandardAblationConfigs,
            live: false,
            ct: CancellationToken.None);

        results.Should().HaveCount(5);
        results.Select(r => r.Config.Name).Should().ContainInOrder("L", "V", "H", "H+G", "H+G+LLM");

        // Corpus documents vectorized once for "title_header" and reused across V, H, H+G, H+G+LLM.
        embeddingClient.DocumentBatchCalls.Should().Be(1);

        AblationResult lexical = results[0];
        AblationResult vector = results[1];
        AblationResult hybrid = results[2];
        AblationResult hybridGraph = results[3];
        AblationResult hybridGraphLlm = results[4];

        // V and H outperform L on synonym query (zero lexical overlap).
        vector.Metrics.RecallAt10.Should().BeGreaterThan(lexical.Metrics.RecallAt10);
        vector.Metrics.Mrr.Should().BeGreaterThan(lexical.Metrics.Mrr);
        hybrid.Metrics.RecallAt10.Should().BeGreaterThan(lexical.Metrics.RecallAt10);
        hybrid.Metrics.Mrr.Should().BeGreaterThan(lexical.Metrics.Mrr);

        // H+G lifts 1-hop parent map into RetrievedTop10 and achieves higher RecallAt10 and UpMapAccuracy than H.
        hybridGraph.Metrics.RecallAt10.Should().BeGreaterThan(hybrid.Metrics.RecallAt10);
        hybridGraph.Metrics.UpMapAccuracy.Should().BeGreaterThan(hybrid.Metrics.UpMapAccuracy);

        // Dedup: paraphrase duplicate merged cleanly; Arbitrate without LLM safely yields "new".
        vector.Metrics.FalseMerges.Should().Be(0);
        vector.Metrics.FalseSplits.Should().Be(0);
        hybrid.Metrics.FalseMerges.Should().Be(0);

        // Closed-world linking: hallucinated "C99" is strictly rejected; OutOfCandidateLinks == 0.
        hybridGraphLlm.Metrics.OutOfCandidateLinks.Should().Be(0);
        hybridGraphLlm.Metrics.ArbitrationRate.Should().BeGreaterThan(0.0);
        hybridGraphLlm.Metrics.StabilityJaccard.Should().Be(1.0);
        hybridGraphLlm.PromptTokens.Should().BeGreaterThan(0);
        hybridGraphLlm.PromptHash.Should().HaveLength(64);
        capturedUserPrompts.Should().NotBeEmpty();
        capturedUserPrompts.Should().Contain(p => p.Contains("[type:map]", StringComparison.Ordinal));

        // Reports persisted atomically under .brain/eval/*.json and *.md.
        runner.LastReportJsonPath.Should().NotBeNullOrWhiteSpace();
        runner.LastReportMarkdownPath.Should().NotBeNullOrWhiteSpace();
        File.Exists(runner.LastReportJsonPath!).Should().BeTrue();
        File.Exists(runner.LastReportMarkdownPath!).Should().BeTrue();

        string evalDir = Path.Combine(tempVault.Path, ".brain", "eval");
        Directory.GetFiles(evalDir, "*.json").Should().ContainSingle();
        Directory.GetFiles(evalDir, "*.md").Should().ContainSingle();

        string jsonText = await File.ReadAllTextAsync(runner.LastReportJsonPath!);
        LinkEvalReport? report = JsonSerializer.Deserialize<LinkEvalReport>(
            jsonText,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        report.Should().NotBeNull();
        report!.PromptPack.Should().Be("link-eval-v2");
        report.PromptHash.Should().HaveLength(64);
        report.EmbedModel.Should().Be(embeddingClient.ModelId);
        report.Model.Should().Be("qwen-fake-7b");
        report.DedupStats.Should().NotBeNull();
        report.DedupStats!.MergeCount.Should().Be(1);
        report.DedupStats.NewCount.Should().Be(3);
        report.DedupSweep.Select(p => p.Threshold).Should().BeEquivalentTo(
            [0.74, 0.76, 0.78, 0.80, 0.82, 0.84, 0.86, 0.88, 0.90, 0.92, 0.94, 0.96]);
        report.Results.Single(r => r.Config.Name == "H+G+LLM").RejectedHallucinations.Should().BeGreaterThan(0);

        string mdText = await File.ReadAllTextAsync(runner.LastReportMarkdownPath!);
        mdText.Should().Contain(report.PromptHash);
        mdText.Should().Contain("## DedupSweep");
        mdText.Should().Contain("| 0.92 |");
    }

    [Fact]
    public async Task RunAsync_ResolvesInflectedPersonAndTechnicalSubAliasMentions()
    {
        using var tempVault = new TempDirectoryScope();
        NoteType personType = ResolveNoteType("Person", "Note", "Insight");
        NoteType decisionType = ResolveNoteType("Decision", "Note", "Insight");
        NoteType resourceType = ResolveNoteType("Resource", "Note", "Insight");
        NoteType projectType = ResolveNoteType("Project", "Note", "Insight");
        NoteType mapType = ResolveNoteType("Map", "Moc");

        IReadOnlyList<NoteDocument> corpus =
        [
            CreateNote(
                "50_People/ivan-petrov.md",
                "p-ivan",
                "Иван Петров",
                personType,
                ["people"],
                [],
                "Технический лид команды разработки."),
            CreateNote(
                "80_Decisions/vybor-bge-m3.md",
                "d-bge",
                "Выбор bge-m3 как основного мультиязычного эмбеддера",
                decisionType,
                ["rag"],
                [],
                "Модель bge-m3 выбрана для русского языка."),
            CreateNote(
                "10_Projects/second-brain-engine.md",
                "pr-sb",
                "Движок SecondBrain",
                projectType,
                ["project"],
                [],
                "Локальная система управления знаниями."),
            CreateNote(
                "90_Meta/Maps/arkhitektura-secondbrain.md",
                "m-sb",
                "Карта архитектуры SecondBrain",
                mapType,
                ["map"],
                [],
                "Обзор архитектуры SecondBrain."),
            CreateNote(
                "30_Resources/vitgenshteyn-ranniy.md",
                "r-witt-early",
                "Ранний Витгенштейн и логико-философский трактат",
                resourceType,
                ["philosophy"],
                [],
                "Логико-философский трактат про атомарные факты и пропозиции."),
            CreateNote(
                "30_Resources/vitgenshteyn-pozdniy.md",
                "r-witt-late",
                "Поздний Витгенштейн и языковые игры",
                resourceType,
                ["philosophy"],
                [],
                "Философские исследования, поздний период, языковые игры и прагматика употребления.")
        ];

        IReadOnlyList<LinkEvalCase> cases =
        [
            new LinkEvalCase(
                Id: "m-person-tech",
                Input: "Обсудили с Иваном переход SecondBrain на эмбеддер bge-m3 для русского поиска.",
                RecipeId: "insight",
                Gold: new LinkGold(
                    Related: ["50_People/ivan-petrov.md", "10_Projects/second-brain-engine.md", "80_Decisions/vybor-bge-m3.md"],
                    Up: [],
                    HardNegatives: [],
                    DuplicateOf: null,
                    Mentions: new Dictionary<string, string>
                    {
                        ["Иваном"] = "50_People/ivan-petrov.md",
                        ["SecondBrain"] = "10_Projects/second-brain-engine.md",
                        ["bge-m3"] = "80_Decisions/vybor-bge-m3.md"
                    },
                    Action: "new")),
            new LinkEvalCase(
                Id: "m-witt-late",
                Input: "Разбирая поздний период Витгенштейна и языковые игры, фиксируем роль употребления.",
                RecipeId: "insight",
                Gold: new LinkGold(
                    Related: ["30_Resources/vitgenshteyn-pozdniy.md"],
                    Up: [],
                    HardNegatives: ["30_Resources/vitgenshteyn-ranniy.md"],
                    DuplicateOf: null,
                    Mentions: new Dictionary<string, string>
                    {
                        ["Витгенштейна"] = "30_Resources/vitgenshteyn-pozdniy.md"
                    },
                    Action: "new"))
        ];

        var runner = new LinkEvalRunner(tempVault.Path, corpus, timeProvider: new MutableTimeProvider(BaseNow));
        IReadOnlyList<AblationResult> results = await runner.RunAsync(
            cases,
            [LinkEvalRunner.StandardAblationConfigs[0]],
            live: false);

        results.Should().ContainSingle();
        results[0].Metrics.MentionAccuracy.Should().Be(1.0);
    }

    [Fact]
    public async Task RunAsync_GreyZoneMerge_AllowsLlmToResolveParaphraseDuplicate()
    {
        using var tempVault = new TempDirectoryScope();
        NoteType regularType = ResolveNoteType("Note", "Atomic", "Concept", "Permanent", "Fleeting");

        IReadOnlyList<NoteDocument> corpus =
        [
            CreateNote(
                "10_Notes/Target.md",
                "n-target",
                "Исключение тегов из чанка",
                regularType,
                ["rag"],
                [],
                "Теги и метаданные нельзя смешивать с текстом чанка при векторизации."),
            CreateNote(
                "10_Notes/Related.md",
                "n-rel",
                "Нарезка Markdown",
                regularType,
                ["rag"],
                [],
                "Разбиение Markdown по заголовкам сохраняет смысл секции.")
        ];

        IReadOnlyList<LinkEvalCase> cases =
        [
            new LinkEvalCase(
                Id: "grey-merge",
                Input: "Если добавлять теги внутрь фрагмента перед расчётом вектора, семантика искажается.",
                RecipeId: "insight",
                Gold: new LinkGold(
                    Related: ["10_Notes/Related.md"],
                    Up: [],
                    HardNegatives: [],
                    DuplicateOf: "10_Notes/Target.md",
                    Mentions: new Dictionary<string, string>(),
                    Action: "merge"))
        ];

        var embeddingClient = Substitute.For<IEmbeddingClient>();
        embeddingClient.ModelId.Returns("fake-bge-m3");
        embeddingClient.Dimensions.Returns(4);
        embeddingClient.EmbedDocumentsAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                IReadOnlyList<string> texts = callInfo.ArgAt<IReadOnlyList<string>>(0);
                var vecs = texts.Select(t => t.Contains("Исключение тегов", StringComparison.Ordinal)
                    ? new float[] { 1.0f, 0.0f, 0.0f, 0.0f }
                    : new float[] { 0.0f, 1.0f, 0.0f, 0.0f }).ToArray();
                return Task.FromResult<IReadOnlyList<float[]>>(vecs);
            });
        // Cosine with Target.md = 0.80 (grey zone: < 0.92, >= 0.72)
        embeddingClient.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new float[] { 0.80f, 0.60f, 0.0f, 0.0f });

        var chatClient = Substitute.For<IChatClient>();
        chatClient.ExtractAsync<EvalLlmSelection>(
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(new EvalLlmSelection(
                Related: ["C2"],
                Up: [],
                DuplicateOf: "C1",
                Action: "merge"));

        var chatRouter = Substitute.For<IChatRouter>();
        chatRouter.Resolve("extract").Returns(chatClient);
        chatRouter.Describe("extract").Returns(new LlmProfile("ollama", "t-lite", 8192, 0.15, 0.8, 20, 0.0, 2048, 120));

        var runner = new LinkEvalRunner(tempVault.Path, corpus, embeddingClient, chatRouter, new MutableTimeProvider(BaseNow));
        IReadOnlyList<AblationResult> results = await runner.RunAsync(
            cases,
            [LinkEvalRunner.StandardAblationConfigs[3], LinkEvalRunner.StandardAblationConfigs[4]],
            live: false);

        results.Should().HaveCount(2);
        AblationResult hg = results[0];
        AblationResult hgLlm = results[1];

        hg.Metrics.FalseSplits.Should().Be(1);
        hgLlm.Metrics.FalseSplits.Should().Be(0);
        hgLlm.Metrics.FalseMerges.Should().Be(0);
        hgLlm.Metrics.ArbitrationRate.Should().Be(1.0);
        hgLlm.Metrics.OutOfCandidateLinks.Should().Be(0);
        hgLlm.Metrics.LinkRecallAt10.Should().Be(1.0);
    }

    [Fact]
    public async Task CompareAsync_ProducesMarkdownTableWithNonZeroDeltas()
    {
        using var tempVault = new TempDirectoryScope();
        IReadOnlyList<NoteDocument> corpus = BuildSyntheticCorpus();
        IReadOnlyList<LinkEvalCase> cases = BuildSyntheticCases();

        var clock = new MutableTimeProvider(BaseNow);

        // Run A: lexical-only ("L")
        var runnerA = new LinkEvalRunner(tempVault.Path, corpus, embeddingClient: null, chatRouter: null, timeProvider: clock);
        _ = await runnerA.RunAsync(cases, [LinkEvalRunner.StandardAblationConfigs[0]], live: false);
        string reportA = runnerA.LastReportJsonPath!;

        // Run B: vector-enabled on "L" config name to verify deltas on common config name.
        clock.Advance(TimeSpan.FromMinutes(1));
        var embeddingClient = new DeterministicTopicEmbeddingClient();
        var runnerB = new LinkEvalRunner(tempVault.Path, corpus, embeddingClient, chatRouter: null, timeProvider: clock);
        var upgradedLexicalNamedConfig = LinkEvalRunner.StandardAblationConfigs[0] with { Lexical = true, Vector = true };
        _ = await runnerB.RunAsync(cases, [upgradedLexicalNamedConfig], live: false);
        string reportB = runnerB.LastReportJsonPath!;

        string comparison = await runnerB.CompareAsync(reportA, reportB);

        comparison.Should().Contain("# LinkEval Comparison");
        comparison.Should().Contain("| Config | Metric | Report A | Report B | Delta (B - A) |");
        comparison.Should().Contain("RecallAt10");
        comparison.Should().Contain("Mrr");
        comparison.Should().Contain("LinkPrecisionAt5");
        comparison.Should().Contain("HardNegativeInTop5");
        comparison.Should().Contain("OutOfCandidateLinks");
        comparison.Should().Contain("FalseMerges");
        comparison.Should().Contain("StabilityJaccard");
        comparison.Should().Contain("P95Ms");
        comparison.Should().Contain("+");
    }

    [Fact]
    public async Task CliApp_EvalOfflineAndCompareCommands_SucceedWithTempVault()
    {
        using var tempVault = new TempDirectoryScope();
        using var tempGold = new TempDirectoryScope();

        string regularType = ResolveNoteType("Note", "Atomic", "Concept", "Permanent", "Fleeting", "Diary")
            .ToString()
            .ToLowerInvariant();

        string corpusYaml = $"""
            notes:
              - path: "10_Notes/SqliteWal.md"
                id: "note-1"
                title: "Транзакции WAL в SQLite"
                type: "{regularType}"
                tags: ["sqlite", "wal"]
                up: []
                related: []
                body: |
                  Настройка журнала WAL для конкурентного чтения и записи в SQLite.
              - path: "10_Notes/Postgres.md"
                id: "note-2"
                title: "Индексы PostgreSQL"
                type: "{regularType}"
                tags: ["postgres"]
                up: []
                related: []
                body: |
                  Использование GIN и B-Tree индексов в PostgreSQL.
            """;

        string linksYaml = """
            cases:
              - id: "cli-case-1"
                input: "Как работает журнал WAL в SQLite при конкурентной записи?"
                recipe_id: "note"
                gold:
                  related:
                    - "10_Notes/SqliteWal.md"
                  up: []
                  hard_negatives: []
                  duplicate_of: null
                  mentions:
                    "SQLite": "10_Notes/SqliteWal.md"
                  action: "new"
            """;

        await AtomicWriter.WriteAllTextAtomicAsync(Path.Combine(tempGold.Path, "corpus.yaml"), corpusYaml);
        await AtomicWriter.WriteAllTextAtomicAsync(Path.Combine(tempGold.Path, "links.yaml"), linksYaml);

        var clock = new MutableTimeProvider(BaseNow);

        // First offline CLI eval run
        using var stdout1 = new StringWriter();
        using var stderr1 = new StringWriter();
        var cli1 = new CliApp(stdout1, stderr1, clock);
        int exit1 = await cli1.RunAsync(["eval", "--vault", tempVault.Path, "--gold", tempGold.Path]);

        exit1.Should().Be(0, stderr1.ToString());
        string outText1 = stdout1.ToString();
        outText1.Should().Contain("# SecondBrain LinkEval Report");
        outText1.Should().Contain("| L |");

        string evalDir = Path.Combine(tempVault.Path, ".brain", "eval");
        string[] jsonReports1 = Directory.GetFiles(evalDir, "*.json");
        jsonReports1.Should().ContainSingle();

        // Second offline CLI eval run at later timestamp
        clock.Advance(TimeSpan.FromMinutes(2));
        using var stdout2 = new StringWriter();
        using var stderr2 = new StringWriter();
        var cli2 = new CliApp(stdout2, stderr2, clock);
        int exit2 = await cli2.RunAsync(["eval", "--vault", tempVault.Path, "--gold", tempGold.Path]);

        exit2.Should().Be(0, stderr2.ToString());
        string[] jsonReports2 = Directory.GetFiles(evalDir, "*.json").OrderBy(p => p, StringComparer.Ordinal).ToArray();
        jsonReports2.Should().HaveCount(2);

        // CLI --compare
        using var stdoutCmp = new StringWriter();
        using var stderrCmp = new StringWriter();
        var cliCmp = new CliApp(stdoutCmp, stderrCmp, clock);
        int exitCmp = await cliCmp.RunAsync(["eval", "--compare", jsonReports2[0], jsonReports2[1]]);

        exitCmp.Should().Be(0, stderrCmp.ToString());
        string cmpOut = stdoutCmp.ToString();
        cmpOut.Should().Contain("# LinkEval Comparison");
        cmpOut.Should().Contain("RecallAt10");
        cmpOut.Should().Contain("OutOfCandidateLinks");
    }

    private static IReadOnlyList<NoteDocument> BuildSyntheticCorpus()
    {
        NoteType regularType = ResolveNoteType("Note", "Atomic", "Concept", "Permanent", "Fleeting");
        NoteType mapType = ResolveNoteType("Map", "Moc");

        var docs = new List<NoteDocument>
        {
            CreateNote(
                "10_Notes/Embeddings.md",
                "n-embed",
                "Семантический поиск и эмбеддинги",
                regularType,
                ["rag", "vectors"],
                [],
                "Построение плотных векторных представлений bge-m3 и косинусной близости."),
            CreateNote(
                "10_Notes/SqliteCache.md",
                "n-sql-cache",
                "Кэширование в SQLite",
                regularType,
                ["sqlite", "wal"],
                ["00_Maps/Backend.md"],
                "Хранение индексов и транзакции WAL в локальной базе SQLite."),
            CreateNote(
                "10_Notes/SqliteBackup.md",
                "n-sql-backup",
                "Резервное копирование SQLite",
                regularType,
                ["sqlite", "backup"],
                [],
                "Скрипты архивирования файлов SQLite на внешний диск."),
            CreateNote(
                "10_Notes/DockerCompose.md",
                "n-docker-c",
                "Docker Compose",
                regularType,
                ["docker"],
                [],
                "Оркестрация локальных контейнеров в Docker Compose."),
            CreateNote(
                "10_Notes/DockerSwarm.md",
                "n-docker-s",
                "Docker Swarm",
                regularType,
                ["docker"],
                [],
                "Кластерный запуск контейнеров в Docker Swarm."),
            CreateNote(
                "00_Maps/Backend.md",
                "n-map-backend",
                "Карта Backend",
                mapType,
                ["map"],
                [],
                "Обзорная карта заметок по серверной разработке.")
        };

        for (int i = 0; i < 6; i++)
        {
            docs.Add(CreateNote(
                $"10_Notes/Filler{i}.md",
                $"n-filler-{i}",
                $"Заметка архива {i}",
                regularType,
                ["archive"],
                [],
                $"Справочная запись номер {i} из архивного раздела."));
        }

        return docs;
    }

    private static IReadOnlyList<LinkEvalCase> BuildSyntheticCases() =>
    [
        // Case 1: Synonym / paraphrase query with zero lexical overlap with Embeddings.md.
        new LinkEvalCase(
            Id: "case-synonym",
            Input: "Смысловой подбор записей по нейросетевым координатам",
            RecipeId: "note",
            Gold: new LinkGold(
                Related: ["10_Notes/Embeddings.md"],
                Up: [],
                HardNegatives: [],
                DuplicateOf: null,
                Mentions: new Dictionary<string, string>(),
                Action: "new")),

        // Case 2: Paraphrase duplicate of Embeddings.md -> triggers Merge.
        new LinkEvalCase(
            Id: "case-paraphrase-dup",
            Input: "Дубликат заметки про плотные нейросетевые эмбеддинги и косинусное сходство",
            RecipeId: "note",
            Gold: new LinkGold(
                Related: [],
                Up: [],
                HardNegatives: [],
                DuplicateOf: "10_Notes/Embeddings.md",
                Mentions: new Dictionary<string, string>(),
                Action: "merge")),

        // Case 3: SQLite cache note + hard negative (SqliteBackup) + 1-hop Up map + mention.
        new LinkEvalCase(
            Id: "case-sql-up-hardneg",
            Input: "Обсудили Кэширование в SQLite и оптимизацию журнала WAL",
            RecipeId: "note",
            Gold: new LinkGold(
                Related: ["10_Notes/SqliteCache.md"],
                Up: ["00_Maps/Backend.md"],
                HardNegatives: ["10_Notes/SqliteBackup.md"],
                DuplicateOf: null,
                Mentions: new Dictionary<string, string>
                {
                    ["Кэширование в SQLite"] = "10_Notes/SqliteCache.md"
                },
                Action: "new")),

        // Case 4: Close candidates above threshold (margin < 0.03) -> triggers Arbitrate.
        new LinkEvalCase(
            Id: "case-arbitrate",
            Input: "Запуск и настройка контейнерного окружения разработчика",
            RecipeId: "note",
            Gold: new LinkGold(
                Related: ["10_Notes/DockerCompose.md"],
                Up: [],
                HardNegatives: [],
                DuplicateOf: null,
                Mentions: new Dictionary<string, string>(),
                Action: "new"))
    ];

    private static NoteDocument CreateNote(
        string path,
        string id,
        string title,
        NoteType type,
        string[] tags,
        string[] up,
        string body)
    {
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{id}\n{title}\n{body}"))).ToLowerInvariant();
        var metadata = new NoteMetadata
        {
            Id = id,
            Title = title,
            Type = type,
            Status = NoteStatus.Active,
            Tags = tags,
            Up = up,
            Related = [],
            Lang = "ru",
            Created = BaseNow,
            Updated = BaseNow,
            ContentHash = hash
        };

        return new NoteDocument(path, metadata, body, hash, BaseNow);
    }

    private static NoteType ResolveNoteType(params string[] preferredNames)
    {
        foreach (string name in preferredNames)
        {
            if (Enum.TryParse<NoteType>(name, ignoreCase: true, out NoteType parsed) && Enum.IsDefined(parsed))
            {
                return parsed;
            }
        }

        return Enum.GetValues<NoteType>().First(t => t != NoteType.Diary);
    }

    private sealed class DeterministicTopicEmbeddingClient : IEmbeddingClient
    {
        public string ModelId => "fake-bge-m3";
        public int Dimensions => 6;
        public string QueryPrefix => "query: ";
        public int DocumentBatchCalls { get; private set; }

        public Task<IReadOnlyList<float[]>> EmbedDocumentsAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            DocumentBatchCalls++;

            IReadOnlyList<float[]> vectors = texts.Select(EmbedText).ToArray();
            return Task.FromResult(vectors);
        }

        public Task<float[]> EmbedQueryAsync(string text, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(EmbedText(text));
        }

        private static float[] EmbedText(string text)
        {
            if (text.Contains("Семантический поиск", StringComparison.OrdinalIgnoreCase))
            {
                return Normalize([1.0f, 0.0f, 0.0f, 0.0f, 0.0f, 0.0f]);
            }

            if (text.Contains("Смысловой подбор", StringComparison.OrdinalIgnoreCase))
            {
                return Normalize([0.82f, 0.0f, 0.0f, 0.0f, 0.572f, 0.0f]);
            }

            if (text.Contains("Дубликат заметки", StringComparison.OrdinalIgnoreCase))
            {
                return Normalize([0.94f, 0.0f, 0.0f, 0.0f, 0.341f, 0.0f]);
            }

            if (text.Contains("Резервное копирование SQLite", StringComparison.OrdinalIgnoreCase))
            {
                return Normalize([0.0f, 0.25f, 0.0f, 0.0f, 0.0f, 0.968f]);
            }

            if (text.Contains("Кэширование в SQLite", StringComparison.OrdinalIgnoreCase) &&
                text.Contains("Обсудили", StringComparison.OrdinalIgnoreCase))
            {
                return Normalize([0.0f, 0.83f, 0.0f, 0.0f, 0.558f, 0.0f]);
            }

            if (text.Contains("Кэширование в SQLite", StringComparison.OrdinalIgnoreCase))
            {
                return Normalize([0.0f, 1.0f, 0.0f, 0.0f, 0.0f, 0.0f]);
            }

            if (text.Contains("Docker Compose", StringComparison.OrdinalIgnoreCase))
            {
                return Normalize([0.0f, 0.0f, 0.99f, 0.141f, 0.0f, 0.0f]);
            }

            if (text.Contains("Docker Swarm", StringComparison.OrdinalIgnoreCase))
            {
                return Normalize([0.0f, 0.0f, 0.99f, -0.141f, 0.0f, 0.0f]);
            }

            if (text.Contains("контейнерного окружения", StringComparison.OrdinalIgnoreCase))
            {
                return Normalize([0.0f, 0.0f, 1.0f, 0.0f, 0.0f, 0.0f]);
            }

            if (text.Contains("Карта Backend", StringComparison.OrdinalIgnoreCase))
            {
                return Normalize([0.0f, 0.0f, 0.0f, 0.0f, -0.3f, 0.954f]);
            }

            for (int i = 0; i < 6; i++)
            {
                if (text.Contains($"Заметка архива {i}", StringComparison.OrdinalIgnoreCase))
                {
                    return Normalize([0.0f, 0.0f, 0.0f, 0.0f, 0.85f, 0.526f - (0.04f * i)]);
                }
            }

            return Normalize([0.0f, 0.0f, 0.0f, 0.0f, 0.0f, 1.0f]);
        }

        private static float[] Normalize(float[] raw)
        {
            double sumSq = raw.Sum(static v => (double)v * v);
            double norm = Math.Sqrt(sumSq);
            return raw.Select(v => (float)(v / norm)).ToArray();
        }
    }

    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
    }

    private sealed class TempDirectoryScope : IDisposable
    {
        public string Path { get; }

        public TempDirectoryScope()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"sb-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                try
                {
                    Directory.Delete(Path, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup.
                }
            }
        }
    }
}