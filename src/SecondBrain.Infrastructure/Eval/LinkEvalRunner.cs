using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Eval;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;
using SecondBrain.Infrastructure.Indexing;
using SecondBrain.Infrastructure.Vault;

namespace SecondBrain.Infrastructure.Eval;

public sealed record EvalLlmSelection(
    [property: JsonPropertyName("related")] string[]? Related,
    [property: JsonPropertyName("up")] string[]? Up,
    [property: JsonPropertyName("duplicate_of")] string? DuplicateOf,
    [property: JsonPropertyName("action")] string? Action);

public sealed record LinkEvalConfigReport(
    AblationConfig Config,
    LinkMetrics Metrics,
    int PromptTokens,
    string PromptHash,
    int RejectedHallucinations);

public sealed record DedupScoreSummary(
    int MergeCount,
    int NewCount,
    double? MinMergeCosine,
    double? MaxMergeCosine,
    double? MaxNewCosine);

public sealed record LinkEvalReport(
    DateTimeOffset At,
    string Model,
    string Profile,
    string PromptPack,
    string PromptHash,
    string EmbedModel,
    bool Live,
    int CaseCount,
    int CorpusCount,
    IReadOnlyList<LinkEvalConfigReport> Results,
    IReadOnlyList<ThresholdSweepPoint> DedupSweep,
    DedupScoreSummary? DedupStats,
    string LastReportJsonPath,
    string LastReportMarkdownPath);

public sealed class LinkEvalRunner : ILinkEvalRunner
{
    private const string PromptPackName = "link-eval-v2";
    private const int EmbedBatchSize = 128;
    private const double LlmGreyZoneCosineFloor = 0.72;

    private const string LlmSystemPrompt = """
        Ты — строгий классификатор связей базы знаний SecondBrain (Obsidian/Zettelkasten).
        Тебе даны входящая заметка и закрытый список кандидатов [C1], [C2], ... с типом, тегами и фрагментом текста.

        ЖЕСТКИЕ ПРАВИЛА:
        1. ЗАМКНУТЫЙ МИР: в полях "related", "up" и "duplicate_of" разрешено указывать ТОЛЬКО метки из переданного списка (например "C1", "C2"). Запрещено придумывать метки, названия или пути файлов вне списка.
        2. КАРТЫ ("up"): в массив "up" включай ТОЛЬКО кандидатов с типом [type:map], тематически охватывающих входящую заметку (обычно 1 родительская карта, если подходящая карта есть в списке). Никогда не добавляй [type:map] в "related".
        3. СВЯЗИ ("related"): выбери от 1 до 4 наиболее релевантных кандидатов (НЕ [type:map] и НЕ duplicate_of).
           - Обязательно включай упомянутых в тексте людей [type:person], проекты [type:project], решения [type:decision] и тесно связанные заметки [type:insight]/[type:resource].
           - НЕ включай контрастных двойников из другой системы, другой технологии с похожим термином или противоположного тезиса, если во входящем тексте нет прямого сравнения с ними.
        4. ДЕДУПЛИКАЦИЯ ("action" и "duplicate_of"):
           - Ставь "action": "merge" и "duplicate_of": "C..." ТОЛЬКО если входящий текст перефразирует и повторяет ту же самую атомарную мысль, что и кандидат, не добавляя нового события, встречи, инцидента или отдельного сценария.
           - Если это новая встреча, решение, инцидент, отдельная практика или самостоятельный тезис — ставь "action": "new" и "duplicate_of": null.
           - Даже при "action": "merge" обязательно укажи в "related" и "up" остальные подходящие связи из списка (исключая сам duplicate_of).
        5. Отвечай строго валидным JSON по заданной схеме.
        """;

    private const string LlmJsonSchema = """
        {
          "type": "object",
          "properties": {
            "related": {
              "type": "array",
              "items": { "type": "string" }
            },
            "up": {
              "type": "array",
              "items": { "type": "string" }
            },
            "duplicate_of": {
              "type": ["string", "null"]
            },
            "action": {
              "type": "string",
              "enum": ["new", "merge"]
            }
          },
          "required": ["related", "up", "duplicate_of", "action"],
          "additionalProperties": false
        }
        """;

    private static readonly JsonSerializerOptions ReportJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static IReadOnlyList<AblationConfig> StandardAblationConfigs { get; } =
    [
        new AblationConfig("L", Lexical: true, Vector: false, Graph: false, Llm: false, ChunkTextVariant: "title_header", CosineThreshold: 0.92, MarginDelta: 0.03),
        new AblationConfig("V", Lexical: false, Vector: true, Graph: false, Llm: false, ChunkTextVariant: "title_header", CosineThreshold: 0.92, MarginDelta: 0.03),
        new AblationConfig("H", Lexical: true, Vector: true, Graph: false, Llm: false, ChunkTextVariant: "title_header", CosineThreshold: 0.92, MarginDelta: 0.03),
        new AblationConfig("H+G", Lexical: true, Vector: true, Graph: true, Llm: false, ChunkTextVariant: "title_header", CosineThreshold: 0.92, MarginDelta: 0.03),
        new AblationConfig("H+G+LLM", Lexical: true, Vector: true, Graph: true, Llm: true, ChunkTextVariant: "title_header", CosineThreshold: 0.92, MarginDelta: 0.03)
    ];

    private readonly string _vaultRoot;
    private readonly IReadOnlyList<NoteDocument> _corpus;
    private readonly IEmbeddingClient? _embeddingClient;
    private readonly IChatRouter? _chatRouter;
    private readonly TimeProvider _timeProvider;

    public LinkEvalRunner(
        string vaultRoot,
        IReadOnlyList<NoteDocument> corpus,
        IEmbeddingClient? embeddingClient = null,
        IChatRouter? chatRouter = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        ArgumentNullException.ThrowIfNull(corpus);

        _vaultRoot = Path.GetFullPath(vaultRoot);
        _corpus = corpus;
        _embeddingClient = embeddingClient;
        _chatRouter = chatRouter;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string? LastReportJsonPath { get; private set; }
    public string? LastReportMarkdownPath { get; private set; }
    public LinkEvalReport? LastReport { get; private set; }

    public async Task<IReadOnlyList<AblationResult>> RunAsync(
        IReadOnlyList<LinkEvalCase> cases,
        IReadOnlyList<AblationConfig> configs,
        bool live,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(configs);

        if (configs.Count == 0)
        {
            throw new ArgumentException("At least one ablation configuration must be provided.", nameof(configs));
        }

        ct.ThrowIfCancellationRequested();

        string promptHash = ComputePromptHash();
        Dictionary<string, NoteDocument> corpusByPath = BuildCorpusLookup(_corpus);
        List<CorpusAliasEntry> corpusAliases = BuildCorpusAliases(_corpus);

        string tempRoot = Path.Combine(Path.GetTempPath(), $"secondbrain-linkeval-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempRoot);

        var variantCache = new Dictionary<string, VariantIndexContext>(StringComparer.Ordinal);
        var queryVectorCache = new Dictionary<string, float[]>(StringComparer.Ordinal);
        var dedupCache = new Dictionary<(string Variant, string CaseId), IReadOnlyList<DedupCandidate>>();
        var configReports = new List<LinkEvalConfigReport>(configs.Count);
        var ablationResults = new List<AblationResult>(configs.Count);
        string? sweepVariant = null;
        double sweepMarginDelta = 0.03;

        try
        {
            for (int c = 0; c < configs.Count; c++)
            {
                ct.ThrowIfCancellationRequested();

                AblationConfig cfg = configs[c];
                string variant = NormalizeVariant(cfg.ChunkTextVariant);

                if (!variantCache.TryGetValue(variant, out VariantIndexContext? variantCtx))
                {
                    variantCtx = await CreateAndIndexVariantAsync(tempRoot, variantCache.Count, variant, ct)
                        .ConfigureAwait(false);
                    variantCache[variant] = variantCtx;
                }

                if (cfg.Vector && _embeddingClient is not null && !variantCtx.VectorsComputed)
                {
                    await PopulateVariantVectorsAsync(variantCtx, corpusByPath, ct).ConfigureAwait(false);
                    if (sweepVariant is null || string.Equals(variant, "title_header", StringComparison.Ordinal))
                    {
                        sweepVariant = variant;
                        sweepMarginDelta = cfg.MarginDelta;
                    }
                }

                bool canUseVector = cfg.Vector && _embeddingClient is not null && variantCtx.VectorsComputed;
                bool canUseLlm = cfg.Llm && _chatRouter is not null;
                int stabilitySampleLimit = live ? Math.Min(5, cases.Count) : cases.Count;

                int totalPromptTokens = 0;
                int totalRejectedHallucinations = 0;
                var caseEvaluations = new List<LinkCaseEvaluation>(cases.Count);

                for (int i = 0; i < cases.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();

                    LinkEvalCase evalCase = cases[i];
                    var sw = Stopwatch.StartNew();

                    (SearchResult searchResult, _) = await variantCtx.Index.SearchDetailedAsync(
                        new SearchQuery(evalCase.Input, Limit: 10),
                        filters: null,
                        useLexical: cfg.Lexical,
                        useVector: canUseVector,
                        useGraph: cfg.Graph,
                        ct: ct).ConfigureAwait(false);

                    List<string> retrievedTop10 = searchResult.Hits
                        .Select(static h => NormalizePath(h.Path))
                        .Distinct(StringComparer.Ordinal)
                        .Take(10)
                        .ToList();

                    IReadOnlyList<DedupCandidate> dedupCandidates = Array.Empty<DedupCandidate>();
                    DedupDecision? dedupDecision = null;

                    if (canUseVector)
                    {
                        dedupCandidates = await GetOrComputeDedupCandidatesAsync(
                            variantCtx,
                            evalCase,
                            queryVectorCache,
                            dedupCache,
                            ct).ConfigureAwait(false);

                        dedupDecision = LinkMetricsCalculator.DecideDedup(
                            dedupCandidates,
                            cfg.CosineThreshold,
                            cfg.MarginDelta);
                    }

                    string predictedAction = "new";
                    string? predictedDuplicateTarget = null;
                    bool arbitrated = false;

                    if (dedupDecision is not null)
                    {
                        if (dedupDecision.Kind == DedupDecisionKind.Merge)
                        {
                            predictedAction = "merge";
                            predictedDuplicateTarget = dedupDecision.TargetPath;
                        }
                        else if (dedupDecision.Kind == DedupDecisionKind.Arbitrate && !canUseLlm)
                        {
                            predictedAction = "new";
                            predictedDuplicateTarget = null;
                        }
                    }

                    IReadOnlyList<string> proposedLinks;
                    IReadOnlyList<string> proposedUpMaps;
                    IReadOnlyList<string>? secondRunProposedLinks = null;

                    if (canUseLlm)
                    {
                        List<ShortlistCandidate> shortlist = BuildShortlist(searchResult.Hits, corpusByPath, cfg.Graph);
                        if (shortlist.Count > 0)
                        {
                            IChatClient chatClient = _chatRouter!.Resolve("extract");
                            bool needsArbitrationHint = dedupDecision?.Kind == DedupDecisionKind.Arbitrate ||
                                (dedupCandidates.Count > 0 && dedupCandidates[0].CosineScore >= LlmGreyZoneCosineFloor);
                            string userPrompt = BuildLlmUserPrompt(evalCase.Input, shortlist, needsArbitrationHint);

                            LlmEvaluationOutcome primary = await EvaluateShortlistWithLlmAsync(
                                chatClient,
                                userPrompt,
                                shortlist,
                                dedupDecision,
                                dedupCandidates,
                                predictedAction,
                                predictedDuplicateTarget,
                                ct).ConfigureAwait(false);

                            proposedLinks = primary.ProposedLinks;
                            proposedUpMaps = primary.ProposedUpMaps;
                            predictedAction = primary.PredictedAction;
                            predictedDuplicateTarget = primary.PredictedDuplicateTarget;
                            arbitrated = primary.Arbitrated;
                            totalPromptTokens += EstimateTokens(LlmSystemPrompt) + EstimateTokens(userPrompt) + EstimateTokens(LlmJsonSchema);
                            totalRejectedHallucinations += primary.RejectedHallucinations;

                            if (i < stabilitySampleLimit)
                            {
                                LlmEvaluationOutcome secondary = await EvaluateShortlistWithLlmAsync(
                                    chatClient,
                                    userPrompt,
                                    shortlist,
                                    dedupDecision,
                                    dedupCandidates,
                                    dedupDecision?.Kind == DedupDecisionKind.Merge ? "merge" : "new",
                                    dedupDecision?.Kind == DedupDecisionKind.Merge ? dedupDecision.TargetPath : null,
                                    ct).ConfigureAwait(false);

                                secondRunProposedLinks = secondary.ProposedLinks;
                            }
                        }
                        else
                        {
                            proposedLinks = Array.Empty<string>();
                            proposedUpMaps = Array.Empty<string>();
                            if (i < stabilitySampleLimit)
                            {
                                secondRunProposedLinks = Array.Empty<string>();
                            }
                        }
                    }
                    else
                    {
                        proposedLinks = SelectProposedLinksDeterministic(
                            retrievedTop10,
                            corpusByPath,
                            predictedDuplicateTarget);

                        proposedUpMaps = SelectProposedUpMapsDeterministic(
                            retrievedTop10,
                            corpusByPath,
                            cfg.Graph);
                    }

                    Dictionary<string, string> resolvedMentions = ResolveMentionsDeterministic(
                        evalCase.Input,
                        retrievedTop10,
                        corpusByPath,
                        corpusAliases);

                    sw.Stop();

                    caseEvaluations.Add(new LinkCaseEvaluation(
                        CaseId: evalCase.Id,
                        Gold: evalCase.Gold,
                        RetrievedTop10: retrievedTop10,
                        ProposedLinks: proposedLinks,
                        ProposedUpMaps: proposedUpMaps,
                        ResolvedMentions: resolvedMentions,
                        PredictedAction: predictedAction,
                        PredictedDuplicateTarget: predictedDuplicateTarget,
                        OutOfCandidateCount: 0,
                        SecondRunProposedLinks: secondRunProposedLinks,
                        Arbitrated: arbitrated,
                        ElapsedMs: sw.Elapsed.TotalMilliseconds));
                }

                LinkMetrics metrics = LinkMetricsCalculator.Compute(caseEvaluations);
                string cfgPromptHash = canUseLlm ? promptHash : string.Empty;

                ablationResults.Add(new AblationResult(cfg, metrics, totalPromptTokens, cfgPromptHash));
                configReports.Add(new LinkEvalConfigReport(cfg, metrics, totalPromptTokens, cfgPromptHash, totalRejectedHallucinations));
            }

            IReadOnlyList<ThresholdSweepPoint> dedupSweep = Array.Empty<ThresholdSweepPoint>();
            DedupScoreSummary? dedupStats = null;

            if (_embeddingClient is not null && cases.Count > 0)
            {
                VariantIndexContext? sweepCtx = null;
                if (sweepVariant is not null && variantCache.TryGetValue(sweepVariant, out VariantIndexContext? foundSweepCtx))
                {
                    sweepCtx = foundSweepCtx;
                }
                else if (variantCache.Count > 0)
                {
                    sweepCtx = variantCache.Values.First();
                    if (!sweepCtx.VectorsComputed)
                    {
                        await PopulateVariantVectorsAsync(sweepCtx, corpusByPath, ct).ConfigureAwait(false);
                    }
                }

                if (sweepCtx is not null && sweepCtx.VectorsComputed)
                {
                    var sweepCases = new List<DedupSweepCase>(cases.Count);
                    var mergeTopScores = new List<double>();
                    var newTopScores = new List<double>();

                    for (int i = 0; i < cases.Count; i++)
                    {
                        ct.ThrowIfCancellationRequested();
                        LinkEvalCase evalCase = cases[i];
                        IReadOnlyList<DedupCandidate> candidates = await GetOrComputeDedupCandidatesAsync(
                            sweepCtx,
                            evalCase,
                            queryVectorCache,
                            dedupCache,
                            ct).ConfigureAwait(false);

                        string? goldDuplicate = string.IsNullOrWhiteSpace(evalCase.Gold.DuplicateOf)
                            ? null
                            : NormalizePath(evalCase.Gold.DuplicateOf);

                        double top1Score = candidates.Count > 0 ? candidates[0].CosineScore : 0.0;
                        if (goldDuplicate is not null)
                        {
                            DedupCandidate? targetCand = candidates.FirstOrDefault(
                                c => string.Equals(c.Path, goldDuplicate, StringComparison.Ordinal));
                            mergeTopScores.Add(targetCand?.CosineScore ?? 0.0);
                        }
                        else
                        {
                            newTopScores.Add(top1Score);
                        }

                        sweepCases.Add(new DedupSweepCase(evalCase.Id, goldDuplicate, candidates));
                    }

                    dedupSweep = LinkMetricsCalculator.SweepDedupThresholds(
                        sweepCases,
                        minThreshold: 0.74,
                        maxThreshold: 0.96,
                        step: 0.02,
                        marginDelta: sweepMarginDelta);

                    dedupStats = new DedupScoreSummary(
                        MergeCount: mergeTopScores.Count,
                        NewCount: newTopScores.Count,
                        MinMergeCosine: mergeTopScores.Count > 0 ? mergeTopScores.Min() : null,
                        MaxMergeCosine: mergeTopScores.Count > 0 ? mergeTopScores.Max() : null,
                        MaxNewCosine: newTopScores.Count > 0 ? newTopScores.Max() : null);
                }
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            (string jsonPath, string mdPath) = ResolveUniqueReportPaths(now);

            string chatModel = ResolveChatModelName(_chatRouter);
            string chatProfile = _chatRouter is null ? "offline" : "extract";
            string embedModel = _embeddingClient?.ModelId ?? "none";

            var report = new LinkEvalReport(
                At: now,
                Model: chatModel,
                Profile: chatProfile,
                PromptPack: PromptPackName,
                PromptHash: promptHash,
                EmbedModel: embedModel,
                Live: live,
                CaseCount: cases.Count,
                CorpusCount: _corpus.Count,
                Results: configReports,
                DedupSweep: dedupSweep,
                DedupStats: dedupStats,
                LastReportJsonPath: jsonPath,
                LastReportMarkdownPath: mdPath);

            string jsonContent = JsonSerializer.Serialize(report, ReportJsonOptions);
            string mdContent = FormatReportMarkdown(report);

            await AtomicWriter.WriteAllTextAtomicAsync(jsonPath, jsonContent, ct).ConfigureAwait(false);
            await AtomicWriter.WriteAllTextAtomicAsync(mdPath, mdContent, ct).ConfigureAwait(false);

            LastReportJsonPath = jsonPath;
            LastReportMarkdownPath = mdPath;
            LastReport = report;

            return ablationResults;
        }
        finally
        {
            foreach (VariantIndexContext ctx in variantCache.Values)
            {
                ctx.Index.Dispose();
            }

            SqliteConnection.ClearAllPools();

            if (Directory.Exists(tempRoot))
            {
                try
                {
                    Directory.Delete(tempRoot, recursive: true);
                }
                catch (IOException)
                {
                    // Best-effort cleanup if OS holds a transient handle during cancellation.
                }
                catch (UnauthorizedAccessException)
                {
                    // Best-effort cleanup.
                }
            }
        }
    }

    public async Task<string> CompareAsync(string reportA, string reportB, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reportA);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportB);
        ct.ThrowIfCancellationRequested();

        string fullPathA = Path.GetFullPath(reportA);
        string fullPathB = Path.GetFullPath(reportB);

        if (!File.Exists(fullPathA))
        {
            throw new FileNotFoundException($"Eval report A not found: '{fullPathA}'.", fullPathA);
        }

        if (!File.Exists(fullPathB))
        {
            throw new FileNotFoundException($"Eval report B not found: '{fullPathB}'.", fullPathB);
        }

        string jsonA = await File.ReadAllTextAsync(fullPathA, Encoding.UTF8, ct).ConfigureAwait(false);
        string jsonB = await File.ReadAllTextAsync(fullPathB, Encoding.UTF8, ct).ConfigureAwait(false);

        LinkEvalReport parsedA = JsonSerializer.Deserialize<LinkEvalReport>(jsonA, ReportJsonOptions)
            ?? throw new InvalidDataException($"Eval report '{fullPathA}' deserialized to null.");
        LinkEvalReport parsedB = JsonSerializer.Deserialize<LinkEvalReport>(jsonB, ReportJsonOptions)
            ?? throw new InvalidDataException($"Eval report '{fullPathB}' deserialized to null.");

        var mapA = new Dictionary<string, LinkEvalConfigReport>(StringComparer.OrdinalIgnoreCase);
        foreach (LinkEvalConfigReport row in parsedA.Results)
        {
            mapA.TryAdd(row.Config.Name, row);
        }

        var commonPairs = new List<(LinkEvalConfigReport A, LinkEvalConfigReport B)>();
        foreach (LinkEvalConfigReport rowB in parsedB.Results)
        {
            if (mapA.TryGetValue(rowB.Config.Name, out LinkEvalConfigReport? rowA))
            {
                commonPairs.Add((rowA, rowB));
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("# LinkEval Comparison");
        sb.AppendLine();
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Report A: `{fullPathA}` ({parsedA.At:O}, Model={parsedA.Model}, EmbedModel={parsedA.EmbedModel}, PromptHash={parsedA.PromptHash})"));
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"- Report B: `{fullPathB}` ({parsedB.At:O}, Model={parsedB.Model}, EmbedModel={parsedB.EmbedModel}, PromptHash={parsedB.PromptHash})"));
        sb.AppendLine();

        if (commonPairs.Count == 0)
        {
            sb.AppendLine("No common ablation configurations found between the two reports.");
            return sb.ToString();
        }

        sb.AppendLine("| Config | Metric | Report A | Report B | Delta (B - A) |");
        sb.AppendLine("| :--- | :--- | ---: | ---: | ---: |");

        foreach ((LinkEvalConfigReport a, LinkEvalConfigReport b) in commonPairs)
        {
            LinkMetrics ma = a.Metrics;
            LinkMetrics mb = b.Metrics;

            AppendDoubleDeltaRow(sb, b.Config.Name, "RecallAt10", ma.RecallAt10, mb.RecallAt10);
            AppendDoubleDeltaRow(sb, b.Config.Name, "Mrr", ma.Mrr, mb.Mrr);
            AppendDoubleDeltaRow(sb, b.Config.Name, "LinkPrecisionAt5", ma.LinkPrecisionAt5, mb.LinkPrecisionAt5);
            AppendDoubleDeltaRow(sb, b.Config.Name, "HardNegativeInTop5", ma.HardNegativeInTop5, mb.HardNegativeInTop5);
            AppendIntDeltaRow(sb, b.Config.Name, "OutOfCandidateLinks", ma.OutOfCandidateLinks, mb.OutOfCandidateLinks);
            AppendIntDeltaRow(sb, b.Config.Name, "FalseMerges", ma.FalseMerges, mb.FalseMerges);
            AppendDoubleDeltaRow(sb, b.Config.Name, "StabilityJaccard", ma.StabilityJaccard, mb.StabilityJaccard);
            AppendDoubleDeltaRow(sb, b.Config.Name, "P95Ms", ma.P95Ms, mb.P95Ms, format: "0.00");
        }

        return sb.ToString();
    }

    public static string FormatReportMarkdown(LinkEvalReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("# SecondBrain LinkEval Report");
        sb.AppendLine();
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- At: {report.At:O}"));
        sb.AppendLine($"- Model: {report.Model}");
        sb.AppendLine($"- Profile: {report.Profile}");
        sb.AppendLine($"- PromptPack: {report.PromptPack}");
        sb.AppendLine($"- PromptHash: {report.PromptHash}");
        sb.AppendLine($"- EmbedModel: {report.EmbedModel}");
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Live: {(report.Live ? "true" : "false")}"));
        sb.AppendLine(string.Create(CultureInfo.InvariantCulture, $"- Cases: {report.CaseCount}, Corpus: {report.CorpusCount}"));
        sb.AppendLine($"- LastReportJsonPath: {report.LastReportJsonPath}");
        sb.AppendLine($"- LastReportMarkdownPath: {report.LastReportMarkdownPath}");
        sb.AppendLine();
        sb.AppendLine("## Results");
        sb.AppendLine();
        sb.AppendLine("| Config | Variant | RecallAt10 | Mrr | LinkPrecisionAt5 | LinkRecallAt10 | LinkF1 | HardNegativeInTop5 | UpMapAccuracy | MentionAccuracy | FalseMerges | FalseSplits | OutOfCandidateLinks | RejectedHallucinations | StabilityJaccard | ArbitrationRate | P95Ms | PromptTokens |");
        sb.AppendLine("| :--- | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");

        foreach (LinkEvalConfigReport row in report.Results)
        {
            LinkMetrics m = row.Metrics;
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"| {row.Config.Name} | {row.Config.ChunkTextVariant} | {m.RecallAt10:0.000} | {m.Mrr:0.000} | {m.LinkPrecisionAt5:0.000} | {m.LinkRecallAt10:0.000} | {m.LinkF1:0.000} | {m.HardNegativeInTop5:0.000} | {m.UpMapAccuracy:0.000} | {m.MentionAccuracy:0.000} | {m.FalseMerges} | {m.FalseSplits} | {m.OutOfCandidateLinks} | {row.RejectedHallucinations} | {m.StabilityJaccard:0.000} | {m.ArbitrationRate:0.000} | {m.P95Ms:0.00} | {row.PromptTokens} |"));
        }

        sb.AppendLine();
        sb.AppendLine("## DedupSweep");
        sb.AppendLine();

        if (report.DedupStats is { } stats)
        {
            sb.AppendLine(string.Create(
                CultureInfo.InvariantCulture,
                $"_Cosine range — merge ({stats.MergeCount}): [{stats.MinMergeCosine:0.000} .. {stats.MaxMergeCosine:0.000}], max new ({stats.NewCount}): {stats.MaxNewCosine:0.000}_"));
            sb.AppendLine();
        }

        if (report.DedupSweep.Count == 0)
        {
            sb.AppendLine("_No DedupSweep points (embedding client unavailable or empty cases)._");
        }
        else
        {
            sb.AppendLine("| Threshold | FalseMerges | FalseSplits | F1 |");
            sb.AppendLine("| ---: | ---: | ---: | ---: |");

            foreach (ThresholdSweepPoint point in report.DedupSweep)
            {
                sb.AppendLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"| {point.Threshold:0.00} | {point.FalseMerges} | {point.FalseSplits} | {point.F1:0.000} |"));
            }
        }

        return sb.ToString();
    }

    public static string ComputePromptHash()
    {
        string combined = $"{LlmSystemPrompt}\n{LlmJsonSchema}";
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(combined));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private (string JsonPath, string MdPath) ResolveUniqueReportPaths(DateTimeOffset now)
    {
        string evalDir = Path.Combine(_vaultRoot, ".brain", "eval");
        Directory.CreateDirectory(evalDir);

        string stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string jsonPath = Path.Combine(evalDir, $"{stamp}.json");
        string mdPath = Path.Combine(evalDir, $"{stamp}.md");

        int suffix = 1;
        while (File.Exists(jsonPath) || File.Exists(mdPath))
        {
            jsonPath = Path.Combine(evalDir, $"{stamp}-{suffix}.json");
            mdPath = Path.Combine(evalDir, $"{stamp}-{suffix}.md");
            suffix++;
        }

        return (jsonPath, mdPath);
    }

    private async Task<VariantIndexContext> CreateAndIndexVariantAsync(
        string tempRoot,
        int variantOrdinal,
        string variant,
        CancellationToken ct)
    {
        string variantRoot = Path.Combine(tempRoot, $"variant_{variantOrdinal}");
        Directory.CreateDirectory(variantRoot);

        string dbPath = Path.Combine(variantRoot, ".brain", "index.db");
        var vaultStore = new FileSystemVaultStore(variantRoot);
        var index = new SqliteVaultIndex(
            variantRoot,
            vaultStore,
            _timeProvider,
            dbPath: dbPath,
            embeddingClient: _embeddingClient);

        try
        {
            await index.EnsureInitializedAsync(ct).ConfigureAwait(false);

            foreach (NoteDocument doc in _corpus)
            {
                ct.ThrowIfCancellationRequested();
                await index.UpsertAsync(doc, ct).ConfigureAwait(false);
            }

            return new VariantIndexContext(variant, dbPath, index);
        }
        catch
        {
            index.Dispose();
            throw;
        }
    }

    private async Task PopulateVariantVectorsAsync(
        VariantIndexContext variantCtx,
        IReadOnlyDictionary<string, NoteDocument> corpusByPath,
        CancellationToken ct)
    {
        if (_embeddingClient is null || variantCtx.VectorsComputed)
        {
            return;
        }

        string connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = variantCtx.DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString();

        var chunkEntries = new List<StoredChunkRow>();
        var embedTexts = new List<string>();

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using SqliteCommand selectCmd = connection.CreateCommand();
            selectCmd.CommandText = """
                SELECT chunk_id, path, header_breadcrumb, body
                FROM chunks
                ORDER BY path, chunk_index;
                """;

            await using SqliteDataReader reader = await selectCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                string chunkId = reader.GetString(0);
                string path = NormalizePath(reader.GetString(1));
                string breadcrumb = reader.GetString(2);
                string body = reader.GetString(3);

                if (!corpusByPath.TryGetValue(path, out NoteDocument? doc))
                {
                    throw new InvalidOperationException($"Indexed chunk '{chunkId}' refers to missing corpus path '{path}'.");
                }

                string embedText = BuildChunkEmbedText(doc, breadcrumb, body, variantCtx.Variant);
                chunkEntries.Add(new StoredChunkRow(chunkId, path, embedText));
                embedTexts.Add(embedText);
            }
        }

        if (chunkEntries.Count == 0)
        {
            variantCtx.VectorsComputed = true;
            return;
        }

        var allVectors = new List<float[]>(chunkEntries.Count);
        for (int offset = 0; offset < embedTexts.Count; offset += EmbedBatchSize)
        {
            ct.ThrowIfCancellationRequested();
            int count = Math.Min(EmbedBatchSize, embedTexts.Count - offset);
            IReadOnlyList<string> batch = embedTexts.GetRange(offset, count);
            IReadOnlyList<float[]> batchVectors = await _embeddingClient.EmbedDocumentsAsync(batch, ct)
                .ConfigureAwait(false);

            if (batchVectors.Count != count)
            {
                throw new InvalidOperationException(
                    $"Embedding client returned {batchVectors.Count} vectors for {count} chunks.");
            }

            for (int j = 0; j < batchVectors.Count; j++)
            {
                allVectors.Add(NormalizeL2(batchVectors[j]));
            }
        }

        string nowIso = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
        int dimensions = allVectors[0].Length;

        await using (var connection = new SqliteConnection(connectionString))
        {
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var pragmaCmd = connection.CreateCommand();
            pragmaCmd.CommandText = "PRAGMA busy_timeout = 5000; PRAGMA foreign_keys = ON;";
            await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using var updateChunkCmd = connection.CreateCommand();
            updateChunkCmd.Transaction = transaction;
            updateChunkCmd.CommandText = "UPDATE chunks SET embed_text = @embed_text WHERE chunk_id = @chunk_id;";
            SqliteParameter pChunkEmbed = updateChunkCmd.Parameters.Add("@embed_text", SqliteType.Text);
            SqliteParameter pChunkId = updateChunkCmd.Parameters.Add("@chunk_id", SqliteType.Text);

            await using var insertVecCmd = connection.CreateCommand();
            insertVecCmd.Transaction = transaction;
            insertVecCmd.CommandText = """
                INSERT OR REPLACE INTO chunk_vectors (chunk_id, embedding, model, updated_at)
                VALUES (@chunk_id, @embedding, @model, @updated_at);
                """;
            SqliteParameter pVecChunkId = insertVecCmd.Parameters.Add("@chunk_id", SqliteType.Text);
            SqliteParameter pVecBlob = insertVecCmd.Parameters.Add("@embedding", SqliteType.Blob);
            SqliteParameter pVecModel = insertVecCmd.Parameters.Add("@model", SqliteType.Text);
            SqliteParameter pVecUpdated = insertVecCmd.Parameters.Add("@updated_at", SqliteType.Text);

            await using var updateQueueCmd = connection.CreateCommand();
            updateQueueCmd.Transaction = transaction;
            updateQueueCmd.CommandText = """
                UPDATE embedding_queue
                SET status = 'done', updated_at = @updated_at
                WHERE chunk_id = @chunk_id;
                """;
            SqliteParameter pQueueUpdated = updateQueueCmd.Parameters.Add("@updated_at", SqliteType.Text);
            SqliteParameter pQueueChunkId = updateQueueCmd.Parameters.Add("@chunk_id", SqliteType.Text);

            pVecModel.Value = _embeddingClient.ModelId;
            pVecUpdated.Value = nowIso;
            pQueueUpdated.Value = nowIso;

            for (int i = 0; i < chunkEntries.Count; i++)
            {
                StoredChunkRow row = chunkEntries[i];
                float[] normalizedVector = allVectors[i];
                byte[] bytes = MemoryMarshal.AsBytes(normalizedVector.AsSpan()).ToArray();

                pChunkEmbed.Value = row.EmbedText;
                pChunkId.Value = row.ChunkId;
                await updateChunkCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                pVecChunkId.Value = row.ChunkId;
                pVecBlob.Value = bytes;
                await insertVecCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                pQueueChunkId.Value = row.ChunkId;
                await updateQueueCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                variantCtx.ChunkVectors.Add((row.Path, normalizedVector));
            }

            await using var metaCmd = connection.CreateCommand();
            metaCmd.Transaction = transaction;
            metaCmd.CommandText = """
                INSERT OR REPLACE INTO index_meta (key, value)
                VALUES ('embedding_model', @model), ('dimensions', @dims);
                """;
            metaCmd.Parameters.AddWithValue("@model", _embeddingClient.ModelId);
            metaCmd.Parameters.AddWithValue("@dims", dimensions.ToString(CultureInfo.InvariantCulture));
            await metaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }

        variantCtx.VectorsComputed = true;
    }

    private async Task<IReadOnlyList<DedupCandidate>> GetOrComputeDedupCandidatesAsync(
        VariantIndexContext variantCtx,
        LinkEvalCase evalCase,
        Dictionary<string, float[]> queryVectorCache,
        Dictionary<(string Variant, string CaseId), IReadOnlyList<DedupCandidate>> dedupCache,
        CancellationToken ct)
    {
        var cacheKey = (variantCtx.Variant, evalCase.Id);
        if (dedupCache.TryGetValue(cacheKey, out IReadOnlyList<DedupCandidate>? cached))
        {
            return cached;
        }

        if (_embeddingClient is null || variantCtx.ChunkVectors.Count == 0)
        {
            dedupCache[cacheKey] = Array.Empty<DedupCandidate>();
            return Array.Empty<DedupCandidate>();
        }

        if (!queryVectorCache.TryGetValue(evalCase.Input, out float[]? queryVector))
        {
            float[] rawQuery = await _embeddingClient.EmbedQueryAsync(evalCase.Input, ct).ConfigureAwait(false);
            queryVector = NormalizeL2(rawQuery);
            queryVectorCache[evalCase.Input] = queryVector;
        }

        if (queryVector.Length == 0)
        {
            dedupCache[cacheKey] = Array.Empty<DedupCandidate>();
            return Array.Empty<DedupCandidate>();
        }

        var bestByPath = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach ((string path, float[] chunkVec) in variantCtx.ChunkVectors)
        {
            if (chunkVec.Length != queryVector.Length)
            {
                continue;
            }

            double cos = DotProduct(queryVector, chunkVec);
            if (!bestByPath.TryGetValue(path, out double existing) || cos > existing)
            {
                bestByPath[path] = cos;
            }
        }

        IReadOnlyList<DedupCandidate> sorted = bestByPath
            .OrderByDescending(static kvp => kvp.Value)
            .ThenBy(static kvp => kvp.Key, StringComparer.Ordinal)
            .Select(static kvp => new DedupCandidate(kvp.Key, kvp.Value))
            .ToList();

        dedupCache[cacheKey] = sorted;
        return sorted;
    }

    private static async Task<LlmEvaluationOutcome> EvaluateShortlistWithLlmAsync(
        IChatClient chatClient,
        string userPrompt,
        List<ShortlistCandidate> shortlist,
        DedupDecision? dedupDecision,
        IReadOnlyList<DedupCandidate> dedupCandidates,
        string baselineAction,
        string? baselineDuplicateTarget,
        CancellationToken ct)
    {
        var labelToCandidate = new Dictionary<string, ShortlistCandidate>(StringComparer.OrdinalIgnoreCase);
        var pathToCandidate = new Dictionary<string, ShortlistCandidate>(StringComparer.Ordinal);

        foreach (ShortlistCandidate candidate in shortlist)
        {
            labelToCandidate[candidate.Label] = candidate;
            pathToCandidate[candidate.Path] = candidate;
        }

        var dedupScoreByPath = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (DedupCandidate dc in dedupCandidates)
        {
            dedupScoreByPath.TryAdd(dc.Path, dc.CosineScore);
        }

        EvalLlmSelection? selection = await chatClient.ExtractAsync<EvalLlmSelection>(
            LlmSystemPrompt,
            userPrompt,
            LlmJsonSchema,
            ct).ConfigureAwait(false);

        int rejectedHallucinations = 0;
        string predictedAction = baselineAction;
        string? predictedDuplicateTarget = baselineDuplicateTarget;
        bool arbitrated = false;

        if (dedupDecision?.Kind == DedupDecisionKind.Arbitrate)
        {
            arbitrated = true;
            if (selection is not null &&
                string.Equals(selection.Action?.Trim(), "merge", StringComparison.OrdinalIgnoreCase) &&
                TryResolveShortlistTarget(
                    selection.DuplicateOf,
                    labelToCandidate,
                    pathToCandidate,
                    out ShortlistCandidate? dupCand,
                    ref rejectedHallucinations))
            {
                predictedAction = "merge";
                predictedDuplicateTarget = dupCand!.Path;
            }
            else
            {
                predictedAction = "new";
                predictedDuplicateTarget = null;
            }
        }
        else if (dedupDecision?.Kind == DedupDecisionKind.New &&
                 dedupCandidates.Count > 0 &&
                 dedupCandidates[0].CosineScore >= LlmGreyZoneCosineFloor &&
                 selection is not null &&
                 string.Equals(selection.Action?.Trim(), "merge", StringComparison.OrdinalIgnoreCase))
        {
            if (TryResolveShortlistTarget(
                    selection.DuplicateOf,
                    labelToCandidate,
                    pathToCandidate,
                    out ShortlistCandidate? dupCand,
                    ref rejectedHallucinations) &&
                !dupCand!.IsMap &&
                dedupScoreByPath.TryGetValue(dupCand.Path, out double candCos) &&
                candCos >= LlmGreyZoneCosineFloor)
            {
                arbitrated = true;
                predictedAction = "merge";
                predictedDuplicateTarget = dupCand.Path;
            }
        }
        else if (selection is not null && !string.IsNullOrWhiteSpace(selection.DuplicateOf))
        {
            _ = TryResolveShortlistTarget(
                selection.DuplicateOf,
                labelToCandidate,
                pathToCandidate,
                out _,
                ref rejectedHallucinations);
        }

        var proposedLinks = new List<string>(5);
        var proposedUpMaps = new List<string>();
        var seenLinks = new HashSet<string>(StringComparer.Ordinal);
        var seenUp = new HashSet<string>(StringComparer.Ordinal);

        if (selection?.Up is not null)
        {
            foreach (string rawUp in selection.Up)
            {
                if (TryResolveShortlistTarget(rawUp, labelToCandidate, pathToCandidate, out ShortlistCandidate? cand, ref rejectedHallucinations))
                {
                    if (seenUp.Add(cand!.Path))
                    {
                        proposedUpMaps.Add(cand.Path);
                    }
                }
            }
        }

        if (selection?.Related is not null)
        {
            foreach (string rawRelated in selection.Related)
            {
                if (TryResolveShortlistTarget(rawRelated, labelToCandidate, pathToCandidate, out ShortlistCandidate? cand, ref rejectedHallucinations))
                {
                    if (cand!.IsMap)
                    {
                        if (seenUp.Add(cand.Path))
                        {
                            proposedUpMaps.Add(cand.Path);
                        }

                        continue;
                    }

                    if (predictedDuplicateTarget is not null &&
                        string.Equals(cand.Path, predictedDuplicateTarget, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    if (seenLinks.Add(cand.Path) && proposedLinks.Count < 5)
                    {
                        proposedLinks.Add(cand.Path);
                    }
                }
            }
        }

        return new LlmEvaluationOutcome(
            ProposedLinks: proposedLinks,
            ProposedUpMaps: proposedUpMaps,
            PredictedAction: predictedAction,
            PredictedDuplicateTarget: predictedDuplicateTarget,
            Arbitrated: arbitrated,
            RejectedHallucinations: rejectedHallucinations);
    }

    private static bool TryResolveShortlistTarget(
        string? rawToken,
        IReadOnlyDictionary<string, ShortlistCandidate> labelToCandidate,
        IReadOnlyDictionary<string, ShortlistCandidate> pathToCandidate,
        out ShortlistCandidate? candidate,
        ref int rejectedHallucinations)
    {
        candidate = null;
        if (string.IsNullOrWhiteSpace(rawToken))
        {
            return false;
        }

        string trimmed = rawToken.Trim();
        if (trimmed.StartsWith('[') && trimmed.EndsWith(']') && trimmed.Length > 2)
        {
            trimmed = trimmed[1..^1].Trim();
        }

        if (labelToCandidate.TryGetValue(trimmed, out candidate))
        {
            return true;
        }

        string normalizedAsPath = NormalizePath(trimmed);
        if (pathToCandidate.TryGetValue(normalizedAsPath, out candidate))
        {
            return true;
        }

        rejectedHallucinations++;
        return false;
    }

    private static List<ShortlistCandidate> BuildShortlist(
        IReadOnlyList<SearchHit> hits,
        IReadOnlyDictionary<string, NoteDocument> corpusByPath,
        bool includeGraphUpMaps)
    {
        const int maxShortlistSize = 10;
        var orderedPaths = new List<string>(maxShortlistSize);
        var seenPaths = new HashSet<string>(StringComparer.Ordinal);
        var hitByPath = new Dictionary<string, SearchHit>(StringComparer.Ordinal);

        for (int i = 0; i < hits.Count && orderedPaths.Count < maxShortlistSize; i++)
        {
            SearchHit hit = hits[i];
            string path = NormalizePath(hit.Path);
            if (seenPaths.Add(path))
            {
                orderedPaths.Add(path);
                hitByPath[path] = hit;
            }
        }

        if (includeGraphUpMaps)
        {
            var graphUpMaps = new List<string>();
            int seedsInspected = 0;

            for (int i = 0; i < orderedPaths.Count && seedsInspected < 3; i++)
            {
                if (!corpusByPath.TryGetValue(orderedPaths[i], out NoteDocument? seedDoc) || IsMapNote(seedDoc))
                {
                    continue;
                }

                seedsInspected++;
                foreach (string rawUp in seedDoc.Metadata.Up)
                {
                    if (string.IsNullOrWhiteSpace(rawUp))
                    {
                        continue;
                    }

                    string upPath = NormalizePath(rawUp);
                    if (corpusByPath.ContainsKey(upPath) && !seenPaths.Contains(upPath) && !graphUpMaps.Contains(upPath, StringComparer.Ordinal))
                    {
                        graphUpMaps.Add(upPath);
                    }
                }
            }

            foreach (string mapPath in graphUpMaps)
            {
                if (orderedPaths.Count < maxShortlistSize)
                {
                    orderedPaths.Add(mapPath);
                    seenPaths.Add(mapPath);
                    continue;
                }

                int replaceIdx = -1;
                for (int k = orderedPaths.Count - 1; k >= 5; k--)
                {
                    if (!corpusByPath.TryGetValue(orderedPaths[k], out NoteDocument? tailDoc) || !IsMapNote(tailDoc))
                    {
                        replaceIdx = k;
                        break;
                    }
                }

                if (replaceIdx >= 0)
                {
                    seenPaths.Remove(orderedPaths[replaceIdx]);
                    orderedPaths[replaceIdx] = mapPath;
                    seenPaths.Add(mapPath);
                }
            }
        }

        var shortlist = new List<ShortlistCandidate>(orderedPaths.Count);
        for (int i = 0; i < orderedPaths.Count; i++)
        {
            string path = orderedPaths[i];
            hitByPath.TryGetValue(path, out SearchHit? hit);
            corpusByPath.TryGetValue(path, out NoteDocument? doc);

            bool isMap = doc is not null && IsMapNote(doc);
            string typeLabel = doc is not null
                ? doc.Metadata.Type.ToString().ToLowerInvariant()
                : "note";
            string title = doc?.Metadata.Title ?? hit?.Title ?? path;
            IReadOnlyList<string> tags = doc?.Metadata.Tags ?? (IReadOnlyList<string>?)hit?.Tags ?? Array.Empty<string>();
            string snippet = doc is not null && !string.IsNullOrWhiteSpace(doc.Body)
                ? TruncateText(doc.Body, 360)
                : CollapseSpaces(hit?.Snippet ?? string.Empty);

            shortlist.Add(new ShortlistCandidate(
                Label: $"C{i + 1}",
                Path: path,
                Title: title,
                TypeLabel: typeLabel,
                Tags: tags,
                Snippet: snippet,
                IsMap: isMap));
        }

        return shortlist;
    }

    private static string BuildLlmUserPrompt(
        string inputText,
        IReadOnlyList<ShortlistCandidate> shortlist,
        bool needsDedupArbitration)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Входящая заметка:");
        sb.AppendLine(inputText);
        sb.AppendLine();
        sb.AppendLine("Закрытый список кандидатов (выбирай ТОЛЬКО эти метки):");

        foreach (ShortlistCandidate candidate in shortlist)
        {
            string tagsPart = candidate.Tags.Count > 0
                ? $" | tags: {string.Join(", ", candidate.Tags)}"
                : string.Empty;
            sb.AppendLine($"[{candidate.Label}] [type:{candidate.TypeLabel}] {candidate.Title}{tagsPart} — {candidate.Snippet}");
        }

        if (needsDedupArbitration)
        {
            sb.AppendLine();
            sb.AppendLine("Внимание: среди кандидатов есть семантически близкие заметки. Внимательно проверь, является ли входящий текст перефразом той же самой мысли (action=\"merge\", duplicate_of=\"C...\") или новой самостоятельной заметкой (action=\"new\", duplicate_of=null).");
        }

        return sb.ToString();
    }

    private static List<string> SelectProposedLinksDeterministic(
        IReadOnlyList<string> retrievedTop10,
        IReadOnlyDictionary<string, NoteDocument> corpusByPath,
        string? duplicateTarget)
    {
        var links = new List<string>(5);
        for (int i = 0; i < retrievedTop10.Count && links.Count < 5; i++)
        {
            string path = retrievedTop10[i];
            if (duplicateTarget is not null && string.Equals(path, duplicateTarget, StringComparison.Ordinal))
            {
                continue;
            }

            if (corpusByPath.TryGetValue(path, out NoteDocument? doc) && IsMapNote(doc))
            {
                continue;
            }

            links.Add(path);
        }

        return links;
    }

    private static List<string> SelectProposedUpMapsDeterministic(
        IReadOnlyList<string> retrievedTop10,
        IReadOnlyDictionary<string, NoteDocument> corpusByPath,
        bool useGraph)
    {
        var upMaps = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < retrievedTop10.Count; i++)
        {
            string path = retrievedTop10[i];
            if (corpusByPath.TryGetValue(path, out NoteDocument? doc) && IsMapNote(doc))
            {
                if (seen.Add(path))
                {
                    upMaps.Add(path);
                }
            }
        }

        if (useGraph)
        {
            int seedsInspected = 0;
            for (int i = 0; i < retrievedTop10.Count && seedsInspected < 3; i++)
            {
                string path = retrievedTop10[i];
                if (!corpusByPath.TryGetValue(path, out NoteDocument? doc) || IsMapNote(doc))
                {
                    continue;
                }

                seedsInspected++;
                foreach (string rawUp in doc.Metadata.Up)
                {
                    if (string.IsNullOrWhiteSpace(rawUp))
                    {
                        continue;
                    }

                    string upPath = NormalizePath(rawUp);
                    if (corpusByPath.ContainsKey(upPath) && seen.Add(upPath))
                    {
                        upMaps.Add(upPath);
                    }
                }
            }
        }

        return upMaps;
    }

private static readonly HashSet<string> RussianCaseEndings = new(StringComparer.Ordinal)
    {
        "", "а", "я", "у", "ю", "ом", "ем", "ём", "ой", "ей", "ою", "ею",
        "е", "и", "ы", "ь", "ов", "ев", "ам", "ям", "ами", "ями", "ах", "ях"
    };

    private static Dictionary<string, string> ResolveMentionsDeterministic(
        string inputText,
        IReadOnlyList<string> retrievedTop10,
        IReadOnlyDictionary<string, NoteDocument> corpusByPath,
        IReadOnlyList<CorpusAliasEntry> corpusAliases)
    {
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(inputText) || corpusAliases.Count == 0)
        {
            return resolved;
        }

        var topRank = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < retrievedTop10.Count; i++)
        {
            topRank.TryAdd(retrievedTop10[i], i);
        }

        List<InputTokenSpan> inputTokens = TokenizeWithSpans(inputText);

        // Pass 1: exact boundary-safe surface substring matches (full titles first, then sub-aliases).
        foreach (CorpusAliasEntry entry in corpusAliases)
        {
            if (entry.Alias.Length < 2)
            {
                continue;
            }

            int idx = FindExactBoundaryMatch(inputText, entry.Alias);
            if (idx >= 0)
            {
                string surface = inputText.Substring(idx, entry.Alias.Length);
                string bestPath = ChooseBestTargetPath(entry, topRank);
                resolved.TryAdd(surface, bestPath);
                resolved.TryAdd(entry.Alias, bestPath);
                if (!entry.IsSubAlias &&
                    corpusByPath.TryGetValue(bestPath, out NoteDocument? doc) &&
                    !string.IsNullOrWhiteSpace(doc.Metadata.Title))
                {
                    resolved.TryAdd(doc.Metadata.Title, bestPath);
                }
            }
        }

        // Pass 2: normalized token-sequence matches (captures Russian inflection via RuNormalizer).
        if (inputTokens.Count > 0)
        {
            foreach (CorpusAliasEntry entry in corpusAliases)
            {
                IReadOnlyList<string> aliasTokens = entry.NormalizedTokens;
                if (aliasTokens.Count == 0)
                {
                    continue;
                }

                if (aliasTokens.Count == 1 && aliasTokens[0].Length < 3)
                {
                    continue;
                }

                for (int i = 0; i <= inputTokens.Count - aliasTokens.Count; i++)
                {
                    bool matched = true;
                    for (int j = 0; j < aliasTokens.Count; j++)
                    {
                        if (!string.Equals(inputTokens[i + j].Normalized, aliasTokens[j], StringComparison.Ordinal))
                        {
                            matched = false;
                            break;
                        }
                    }

                    if (!matched)
                    {
                        continue;
                    }

                    int start = inputTokens[i].Start;
                    int end = inputTokens[i + aliasTokens.Count - 1].End;
                    string surface = inputText[start..end];
                    string bestPath = ChooseBestTargetPath(entry, topRank);

                    resolved.TryAdd(surface, bestPath);
                    resolved.TryAdd(entry.Alias, bestPath);
                    if (!entry.IsSubAlias &&
                        corpusByPath.TryGetValue(bestPath, out NoteDocument? doc) &&
                        !string.IsNullOrWhiteSpace(doc.Metadata.Title))
                    {
                        resolved.TryAdd(doc.Metadata.Title, bestPath);
                    }

                    break;
                }
            }

            // Pass 3: exact Russian noun/case-ending fallback for single-word aliases (handles short proper names).
            foreach (CorpusAliasEntry entry in corpusAliases)
            {
                if (entry.NormalizedTokens.Count != 1 || entry.Alias.Length < 4 || entry.Alias.Contains(' '))
                {
                    continue;
                }

                string aliasLower = entry.Alias.ToLowerInvariant().Replace('ё', 'е');
                for (int i = 0; i < inputTokens.Count; i++)
                {
                    InputTokenSpan tok = inputTokens[i];
                    if (IsRussianCaseInflection(aliasLower, tok.RawLower))
                    {
                        string surface = inputText[tok.Start..tok.End];
                        string bestPath = ChooseBestTargetPath(entry, topRank);
                        resolved.TryAdd(surface, bestPath);
                        resolved.TryAdd(entry.Alias, bestPath);
                        break;
                    }
                }
            }
        }

        return resolved;
    }

    private static bool IsRussianCaseInflection(string aliasLower, string wordLower)
    {
        if (aliasLower.Length < 4 || wordLower.Length < 3)
        {
            return false;
        }

        char last = aliasLower[^1];
        string stemBase = last is 'а' or 'я' or 'о' or 'е' or 'ь' or 'й' or 'ы' or 'и'
            ? aliasLower[..^1]
            : aliasLower;

        if (stemBase.Length < 3 || !wordLower.StartsWith(stemBase, StringComparison.Ordinal))
        {
            return false;
        }

        string suffix = wordLower[stemBase.Length..];
        return RussianCaseEndings.Contains(suffix);
    }
    
    private static string ChooseBestTargetPath(
        CorpusAliasEntry entry,
        IReadOnlyDictionary<string, int> topRank)
    {
        IReadOnlyList<AliasTargetInfo> targets = entry.Targets;
        if (targets.Count == 1)
        {
            return targets[0].Path;
        }

        AliasTargetInfo best = targets[0];
        int bestRank = topRank.TryGetValue(best.Path, out int r0) ? r0 : int.MaxValue;
        bool bestInTop = bestRank != int.MaxValue;

        for (int i = 1; i < targets.Count; i++)
        {
            AliasTargetInfo candidate = targets[i];
            int candRank = topRank.TryGetValue(candidate.Path, out int rc) ? rc : int.MaxValue;
            bool candInTop = candRank != int.MaxValue;

            if (candInTop != bestInTop)
            {
                if (candInTop)
                {
                    best = candidate;
                    bestRank = candRank;
                    bestInTop = true;
                }

                continue;
            }

            if (entry.IsSubAlias && candidate.IsMap != best.IsMap)
            {
                if (!candidate.IsMap)
                {
                    best = candidate;
                    bestRank = candRank;
                }

                continue;
            }

            if (candRank != bestRank)
            {
                if (candRank < bestRank)
                {
                    best = candidate;
                    bestRank = candRank;
                }

                continue;
            }

            if (candidate.TitleTokenCount != best.TitleTokenCount)
            {
                if (candidate.TitleTokenCount < best.TitleTokenCount)
                {
                    best = candidate;
                }

                continue;
            }

            if (string.CompareOrdinal(candidate.Path, best.Path) < 0)
            {
                best = candidate;
            }
        }

        return best.Path;
    }

    private static int FindExactBoundaryMatch(string text, string pattern)
    {
        int startIndex = 0;
        while (startIndex <= text.Length - pattern.Length)
        {
            int found = text.IndexOf(pattern, startIndex, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
            {
                return -1;
            }

            bool leftOk = found == 0 || !char.IsLetterOrDigit(text[found - 1]);
            int after = found + pattern.Length;
            bool rightOk = after >= text.Length || !char.IsLetterOrDigit(text[after]);

            if (leftOk && rightOk)
            {
                return found;
            }

            startIndex = found + 1;
        }

        return -1;
    }

    private static List<InputTokenSpan> TokenizeWithSpans(string text)
    {
        var tokens = new List<InputTokenSpan>();
        int i = 0;

        while (i < text.Length)
        {
            while (i < text.Length && !char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            int start = i;
            while (i < text.Length && char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            string raw = text[start..i];
            string rawLower = raw.ToLowerInvariant().Replace('ё', 'е');
            IReadOnlyList<string> normalizedParts = RuNormalizer.TokenizeNormalized(raw);
            if (normalizedParts.Count == 1 && normalizedParts[0].Length > 0)
            {
                tokens.Add(new InputTokenSpan(start, i, rawLower, normalizedParts[0]));
            }
            else if (normalizedParts.Count == 0)
            {
                string fallback = RuNormalizer.NormalizeForSearch(raw);
                if (fallback.Length > 0)
                {
                    tokens.Add(new InputTokenSpan(start, i, rawLower, fallback));
                }
            }
            else
            {
                foreach (string part in normalizedParts)
                {
                    if (part.Length > 0)
                    {
                        tokens.Add(new InputTokenSpan(start, i, rawLower, part));
                    }
                }
            }
        }

        return tokens;
    }
    
    private static Dictionary<string, NoteDocument> BuildCorpusLookup(IReadOnlyList<NoteDocument> corpus)
    {
        var map = new Dictionary<string, NoteDocument>(corpus.Count, StringComparer.Ordinal);
        foreach (NoteDocument doc in corpus)
        {
            string normalizedPath = NormalizePath(doc.Path);
            map[normalizedPath] = string.Equals(doc.Path, normalizedPath, StringComparison.Ordinal)
                ? doc
                : doc with { Path = normalizedPath };
        }

        return map;
    }

    private static List<CorpusAliasEntry> BuildCorpusAliases(IReadOnlyList<NoteDocument> corpus)
    {
        var grouped = new Dictionary<(string Alias, bool IsSubAlias), Dictionary<string, AliasTargetInfo>>(AliasKeyComparer.Instance);

        foreach (NoteDocument doc in corpus)
        {
            string path = NormalizePath(doc.Path);
            bool isMap = IsMapNote(doc);
            string title = doc.Metadata.Title.Trim();
            int titleTokenCount = string.IsNullOrWhiteSpace(title)
                ? int.MaxValue
                : RuNormalizer.TokenizeNormalized(title).Count;

            var targetInfo = new AliasTargetInfo(path, isMap, titleTokenCount);

            if (!string.IsNullOrWhiteSpace(title))
            {
                AddAliasTarget(grouped, title, isSubAlias: false, targetInfo);
                foreach (string subAlias in ExtractTitleSubAliases(title, doc.Metadata.Type))
                {
                    AddAliasTarget(grouped, subAlias, isSubAlias: true, targetInfo);
                }
            }

            string stem = Path.GetFileNameWithoutExtension(path).Trim();
            if (stem.Length >= 2)
            {
                AddAliasTarget(grouped, stem, isSubAlias: false, targetInfo);
            }
        }

        return grouped
            .Select(static kvp => new CorpusAliasEntry(
                Alias: kvp.Key.Alias,
                IsSubAlias: kvp.Key.IsSubAlias,
                NormalizedTokens: RuNormalizer.TokenizeNormalized(kvp.Key.Alias),
                Targets: kvp.Value.Values.OrderBy(static t => t.Path, StringComparer.Ordinal).ToArray()))
            .OrderBy(static entry => entry.IsSubAlias)
            .ThenByDescending(static entry => entry.NormalizedTokens.Count)
            .ThenByDescending(static entry => entry.Alias.Length)
            .ThenBy(static entry => entry.Alias, StringComparer.Ordinal)
            .ToList();
    }

    private static IReadOnlyList<string> ExtractTitleSubAliases(string title, NoteType noteType)
    {
        List<string> words = SplitPreservingHyphens(title);
        if (words.Count == 0)
        {
            return Array.Empty<string>();
        }

        bool isPerson = string.Equals(noteType.ToString(), "person", StringComparison.OrdinalIgnoreCase);
        var result = new List<string>();

        for (int i = 0; i < words.Count; i++)
        {
            string word = words[i];
            if (word.Length < 3)
            {
                continue;
            }

            if (isPerson)
            {
                result.Add(word);
                continue;
            }

            if (ContainsAsciiLetter(word))
            {
                result.Add(word);
                continue;
            }

            if (i > 0 && word.Length >= 4 && char.IsUpper(word[0]))
            {
                result.Add(word);
            }
        }

        return result;
    }

    private static List<string> SplitPreservingHyphens(string text)
    {
        var words = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && !char.IsLetterOrDigit(text[i]))
            {
                i++;
            }

            if (i >= text.Length)
            {
                break;
            }

            int start = i;
            while (i < text.Length &&
                   (char.IsLetterOrDigit(text[i]) ||
                    (text[i] == '-' && i + 1 < text.Length && char.IsLetterOrDigit(text[i + 1]))))
            {
                i++;
            }

            string token = text[start..i].Trim('-');
            if (token.Length > 0)
            {
                words.Add(token);
            }
        }

        return words;
    }

    private static bool ContainsAsciiLetter(string token)
    {
        for (int i = 0; i < token.Length; i++)
        {
            if (char.IsAsciiLetter(token[i]))
            {
                return true;
            }
        }

        return false;
    }

    private static void AddAliasTarget(
        Dictionary<(string Alias, bool IsSubAlias), Dictionary<string, AliasTargetInfo>> grouped,
        string alias,
        bool isSubAlias,
        AliasTargetInfo target)
    {
        var key = (alias, isSubAlias);
        if (!grouped.TryGetValue(key, out Dictionary<string, AliasTargetInfo>? targets))
        {
            targets = new Dictionary<string, AliasTargetInfo>(StringComparer.Ordinal);
            grouped[key] = targets;
        }

        targets.TryAdd(target.Path, target);
    }

    private static string BuildChunkEmbedText(
        NoteDocument doc,
        string headerBreadcrumb,
        string body,
        string variant)
        => MarkdownChunker.BuildEmbedText(doc.Metadata.Title, headerBreadcrumb, body, doc.Metadata.Tags, variant);

    private static string NormalizeVariant(string? variant)
        => string.IsNullOrWhiteSpace(variant) ? "title_header" : variant.Trim();

    private static bool IsMapNote(NoteDocument doc)
        => string.Equals(doc.Metadata.Type.ToString(), "map", StringComparison.OrdinalIgnoreCase);

    private static string ResolveChatModelName(IChatRouter? chatRouter)
    {
        if (chatRouter is null)
        {
            return "none";
        }

        try
        {
            LlmProfile? profile = chatRouter.Describe("extract");
            return profile is not null && !string.IsNullOrWhiteSpace(profile.Model)
                ? profile.Model
                : "extract";
        }
        catch (Exception ex) when (ex is KeyNotFoundException or InvalidOperationException)
        {
            return "extract";
        }
    }

    private static float[] NormalizeL2(float[] vector)
    {
        if (vector.Length == 0)
        {
            return Array.Empty<float>();
        }

        double sumSq = 0.0;
        for (int i = 0; i < vector.Length; i++)
        {
            sumSq += (double)vector[i] * vector[i];
        }

        if (sumSq <= 0.0)
        {
            return (float[])vector.Clone();
        }

        double norm = Math.Sqrt(sumSq);
        var result = new float[vector.Length];
        for (int i = 0; i < vector.Length; i++)
        {
            result[i] = (float)(vector[i] / norm);
        }

        return result;
    }

    private static double DotProduct(float[] a, float[] b)
    {
        double sum = 0.0;
        int len = Math.Min(a.Length, b.Length);
        for (int i = 0; i < len; i++)
        {
            sum += (double)a[i] * b[i];
        }

        return sum;
    }

    private static int EstimateTokens(string text)
        => string.IsNullOrEmpty(text) ? 0 : (text.Length + 3) / 4;

    private static string TruncateText(string text, int maxLength)
    {
        string collapsed = CollapseSpaces(text);
        if (collapsed.Length <= maxLength)
        {
            return collapsed;
        }

        return collapsed[..maxLength].TrimEnd() + "...";
    }

    private static string CollapseSpaces(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        bool prevSpace = true;
        foreach (char ch in text)
        {
            if (char.IsWhiteSpace(ch))
            {
                if (!prevSpace)
                {
                    sb.Append(' ');
                    prevSpace = true;
                }
            }
            else
            {
                sb.Append(ch);
                prevSpace = false;
            }
        }

        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    private static string NormalizePath(string path)
    {
        string unified = path.Replace('\\', '/').Trim();
        while (unified.StartsWith("./", StringComparison.Ordinal))
        {
            unified = unified[2..];
        }

        return unified.TrimStart('/');
    }

    private static void AppendDoubleDeltaRow(
        StringBuilder sb,
        string configName,
        string metricName,
        double a,
        double b,
        string format = "0.000")
    {
        double delta = b - a;
        string sign = delta > 0.0 ? "+" : string.Empty;
        string aStr = a.ToString(format, CultureInfo.InvariantCulture);
        string bStr = b.ToString(format, CultureInfo.InvariantCulture);
        string dStr = string.Concat(sign, delta.ToString(format, CultureInfo.InvariantCulture));
        sb.AppendLine($"| {configName} | {metricName} | {aStr} | {bStr} | {dStr} |");
    }

    private static void AppendIntDeltaRow(
        StringBuilder sb,
        string configName,
        string metricName,
        int a,
        int b)
    {
        int delta = b - a;
        string sign = delta > 0 ? "+" : string.Empty;
        sb.AppendLine(string.Create(
            CultureInfo.InvariantCulture,
            $"| {configName} | {metricName} | {a} | {b} | {sign}{delta} |"));
    }

    private sealed class AliasKeyComparer : IEqualityComparer<(string Alias, bool IsSubAlias)>
    {
        public static AliasKeyComparer Instance { get; } = new();

        public bool Equals((string Alias, bool IsSubAlias) x, (string Alias, bool IsSubAlias) y)
            => x.IsSubAlias == y.IsSubAlias &&
               string.Equals(x.Alias, y.Alias, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string Alias, bool IsSubAlias) obj)
            => HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Alias), obj.IsSubAlias);
    }

    private sealed class VariantIndexContext(string variant, string dbPath, SqliteVaultIndex index)
    {
        public string Variant { get; } = variant;
        public string DbPath { get; } = dbPath;
        public SqliteVaultIndex Index { get; } = index;
        public bool VectorsComputed { get; set; }
        public List<(string Path, float[] NormalizedVector)> ChunkVectors { get; } = [];
    }

    private sealed record StoredChunkRow(string ChunkId, string Path, string EmbedText);

    private sealed record ShortlistCandidate(
        string Label,
        string Path,
        string Title,
        string TypeLabel,
        IReadOnlyList<string> Tags,
        string Snippet,
        bool IsMap);

    private sealed record LlmEvaluationOutcome(
        IReadOnlyList<string> ProposedLinks,
        IReadOnlyList<string> ProposedUpMaps,
        string PredictedAction,
        string? PredictedDuplicateTarget,
        bool Arbitrated,
        int RejectedHallucinations);

    private sealed record AliasTargetInfo(string Path, bool IsMap, int TitleTokenCount);

    private sealed record CorpusAliasEntry(
        string Alias,
        bool IsSubAlias,
        IReadOnlyList<string> NormalizedTokens,
        IReadOnlyList<AliasTargetInfo> Targets);

    private sealed record InputTokenSpan(int Start, int End, string RawLower, string Normalized);
}