using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Linking;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;
using SecondBrain.Core.Recipes;

namespace SecondBrain.Infrastructure.Indexing;

public sealed class SqliteVaultIndex : IVaultIndex, IDisposable
{
    private const string BuiltAtMetaKey = "built_at";
    private const string EmbeddingModelMetaKey = "embedding_model";
    private const string DimensionsMetaKey = "dimensions";
    private const int RrfConstantK = 60;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IVaultStore _vaultStore;
    private readonly TimeProvider _timeProvider;
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly IEmbeddingClient? _embeddingClient;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private volatile bool _initialized;
    private volatile bool _vec0Available;
    private bool _disposed;

    public SqliteVaultIndex(
        string vaultRoot,
        IVaultStore vaultStore,
        TimeProvider? timeProvider = null,
        string? dbPath = null,
        IEmbeddingClient? embeddingClient = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        ArgumentNullException.ThrowIfNull(vaultStore);

        var fullVaultRoot = Path.GetFullPath(vaultRoot);
        _vaultStore = vaultStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dbPath = string.IsNullOrWhiteSpace(dbPath)
            ? Path.Combine(fullVaultRoot, ".brain", "index.db")
            : Path.GetFullPath(dbPath);
        _embeddingClient = embeddingClient;

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    public async Task EnsureInitializedAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_initialized)
        {
            return;
        }

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            var directory = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using var connection = await OpenConnectionAsync(loadVec0: false, ct).ConfigureAwait(false);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS notes (
                    path TEXT PRIMARY KEY,
                    id TEXT NOT NULL,
                    title TEXT NOT NULL,
                    type TEXT NOT NULL,
                    usecase TEXT NOT NULL,
                    status TEXT NOT NULL,
                    tags_json TEXT NOT NULL,
                    up_json TEXT NOT NULL,
                    content_hash TEXT NOT NULL,
                    modified_at TEXT NOT NULL,
                    review_date TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS chunks (
                    chunk_id TEXT PRIMARY KEY,
                    path TEXT NOT NULL,
                    anchor TEXT NOT NULL,
                    header_breadcrumb TEXT NOT NULL,
                    body TEXT NOT NULL,
                    body_norm TEXT NOT NULL,
                    embed_text TEXT NOT NULL,
                    chunk_index INTEGER NOT NULL,
                    FOREIGN KEY(path) REFERENCES notes(path) ON DELETE CASCADE
                );

                CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(
                    body_norm,
                    chunk_id UNINDEXED,
                    tokenize='trigram'
                );

                CREATE TABLE IF NOT EXISTS chunk_vectors (
                    chunk_id TEXT PRIMARY KEY,
                    embedding BLOB NOT NULL,
                    model TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(chunk_id) REFERENCES chunks(chunk_id) ON DELETE CASCADE
                );

                CREATE TABLE IF NOT EXISTS edges (
                    src_path TEXT NOT NULL,
                    dst_path TEXT NOT NULL,
                    kind TEXT NOT NULL,
                    weight REAL NOT NULL,
                    origin TEXT NOT NULL,
                    PRIMARY KEY (src_path, dst_path, kind)
                );

                CREATE TABLE IF NOT EXISTS embedding_queue (
                    chunk_id TEXT PRIMARY KEY,
                    status TEXT NOT NULL,
                    attempts INTEGER NOT NULL DEFAULT 0,
                    last_error TEXT NULL,
                    updated_at TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS index_meta (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS idx_chunks_path ON chunks(path);
                CREATE INDEX IF NOT EXISTS idx_notes_type ON notes(type);

                INSERT OR IGNORE INTO index_meta (key, value) VALUES ('schema_version', '1');
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            try
            {
                connection.EnableExtensions(true);
                connection.LoadExtension("vec0", "sqlite3_vec_init");
                int dims = _embeddingClient is { Dimensions: > 0 } client ? client.Dimensions : 1024;
                await using var vecCmd = connection.CreateCommand();
                vecCmd.CommandText = $"""
                    CREATE VIRTUAL TABLE IF NOT EXISTS vec_chunks USING vec0(
                        chunk_id TEXT PRIMARY KEY,
                        embedding float[{dims}] distance_metric=cosine
                    );
                    """;
                await vecCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                _vec0Available = true;
            }
            catch (Exception)
            {
                _vec0Available = false;
            }

            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task UpsertAsync(NoteDocument doc, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(doc);

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(_vec0Available, ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            var nowIso = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            await UpsertNoteCoreAsync(connection, transaction, doc, nowIso, _vec0Available, ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await EnsureInitializedAsync(ct).ConfigureAwait(false);
        var normalizedPath = NormalizePath(path);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenConnectionAsync(_vec0Available, ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await DeleteNoteCoreAsync(connection, transaction, normalizedPath, _vec0Available, ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IndexStats> RebuildAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var entries = await _vaultStore.ListAsync(ct: ct).ConfigureAwait(false);
            var docs = new List<NoteDocument>();
            var activePaths = new HashSet<string>(StringComparer.Ordinal);

            foreach (var entry in entries)
            {
                ct.ThrowIfCancellationRequested();

                if (!entry.Path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                NoteDocument? doc;
                try
                {
                    doc = await _vaultStore.ReadAsync(entry.Path, ct).ConfigureAwait(false);
                }
                catch (FormatException)
                {
                    continue;
                }

                if (doc is null)
                {
                    continue;
                }

                docs.Add(doc);
                activePaths.Add(NormalizePath(doc.Path));
            }

            var builtAt = _timeProvider.GetUtcNow();
            var builtAtIso = builtAt.ToString("O", CultureInfo.InvariantCulture);

            await using var connection = await OpenConnectionAsync(_vec0Available, ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            var existingPaths = new List<string>();
            await using (var selectPathsCmd = connection.CreateCommand())
            {
                selectPathsCmd.Transaction = transaction;
                selectPathsCmd.CommandText = "SELECT path FROM notes;";
                await using var reader = await selectPathsCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    existingPaths.Add(reader.GetString(0));
                }
            }

            foreach (var existingPath in existingPaths)
            {
                if (!activePaths.Contains(existingPath))
                {
                    await DeleteNoteCoreAsync(connection, transaction, existingPath, _vec0Available, ct).ConfigureAwait(false);
                }
            }

            foreach (var doc in docs)
            {
                ct.ThrowIfCancellationRequested();
                await UpsertNoteCoreAsync(connection, transaction, doc, builtAtIso, _vec0Available, ct).ConfigureAwait(false);
            }

            await using (var metaCmd = connection.CreateCommand())
            {
                metaCmd.Transaction = transaction;
                metaCmd.CommandText = """
                    INSERT OR REPLACE INTO index_meta (key, value)
                    VALUES (@key, @value);
                    """;
                metaCmd.Parameters.AddWithValue("@key", BuiltAtMetaKey);
                metaCmd.Parameters.AddWithValue("@value", builtAtIso);
                await metaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            int chunkCount = await CountRowsAsync(connection, transaction, "chunks", ct).ConfigureAwait(false);
            int vectorCount = await CountRowsAsync(connection, transaction, "chunk_vectors", ct).ConfigureAwait(false);
            (string model, int dimensions) = await ReadEmbeddingMetaAsync(connection, transaction, ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return new IndexStats(
                Chunks: chunkCount,
                Vectors: vectorCount,
                BuiltAt: builtAt,
                EmbeddingModel: model,
                Dimensions: dimensions);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<IndexStats> GetStatsAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await using var connection = await OpenConnectionAsync(loadVec0: false, ct).ConfigureAwait(false);
        int chunkCount = await CountRowsAsync(connection, transaction: null, "chunks", ct).ConfigureAwait(false);
        int vectorCount = await CountRowsAsync(connection, transaction: null, "chunk_vectors", ct).ConfigureAwait(false);
        (string embeddingModel, int dimensions) = await ReadEmbeddingMetaAsync(connection, transaction: null, ct).ConfigureAwait(false);

        var builtAt = DateTimeOffset.MinValue;
        await using (var metaCmd = connection.CreateCommand())
        {
            metaCmd.CommandText = "SELECT value FROM index_meta WHERE key = @key;";
            metaCmd.Parameters.AddWithValue("@key", BuiltAtMetaKey);
            object? scalar = await metaCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (scalar is string builtAtStr &&
                DateTimeOffset.TryParse(
                    builtAtStr,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var parsed))
            {
                builtAt = parsed;
            }
        }

        return new IndexStats(
            Chunks: chunkCount,
            Vectors: vectorCount,
            BuiltAt: builtAt,
            EmbeddingModel: embeddingModel,
            Dimensions: dimensions);
    }

    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        (SearchResult result, _) = await SearchDetailedAsync(
            query,
            filters: null,
            useLexical: true,
            useVector: _embeddingClient is not null,
            useGraph: false,
            ct: ct).ConfigureAwait(false);

        return result;
    }

    public async Task<(SearchResult Result, SearchExplain Explain)> SearchDetailedAsync(
        SearchQuery query,
        SearchFilters? filters = null,
        bool useLexical = true,
        bool useVector = true,
        bool useGraph = false,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(query);

        var totalSw = Stopwatch.StartNew();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        int limit = query.Limit > 0 ? query.Limit : 8;
        int candidateLimit = Math.Max(limit * 20, 500);

        List<string> tokens = RuNormalizer.TokenizeNormalized(query.Text)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        List<string> longTokens = tokens.Where(static t => t.Length >= 3).ToList();

        string[]? rawTags = filters?.Tags is { Length: > 0 } ? filters.Tags : query.Tags;
        HashSet<string> filterTags = rawTags is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : rawTags
                .Where(static t => !string.IsNullOrWhiteSpace(t))
                .Select(static t => t.Trim().TrimStart('#'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        string? filterType = !string.IsNullOrWhiteSpace(filters?.Type)
            ? filters.Type.Trim()
            : query.Type?.ToString();
        string? filterUsecase = string.IsNullOrWhiteSpace(filters?.Usecase)
            ? null
            : filters.Usecase.Trim();
        string? filterStatus = string.IsNullOrWhiteSpace(filters?.Status)
            ? null
            : filters.Status.Trim();
        string? rawPrefix = !string.IsNullOrWhiteSpace(filters?.PathPrefix)
            ? filters.PathPrefix
            : query.PathPrefix;
        string? filterPrefix = string.IsNullOrWhiteSpace(rawPrefix)
            ? null
            : NormalizePath(rawPrefix);
        HashSet<string> filterUp = filters?.Up is { Length: > 0 }
            ? filters.Up
                .Where(static u => !string.IsNullOrWhiteSpace(u))
                .Select(NormalizePath)
                .ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool hasAnyFilterCriteria = filterTags.Count > 0 ||
                                    filterType is not null ||
                                    filterUsecase is not null ||
                                    filterStatus is not null ||
                                    filterPrefix is not null ||
                                    filterUp.Count > 0;

        bool canAttemptVector = useVector &&
                                _embeddingClient is not null &&
                                !string.IsNullOrWhiteSpace(query.Text);

        if (!useLexical && !canAttemptVector)
        {
            totalSw.Stop();
            return (
                new SearchResult(
                    Hits: Array.Empty<SearchHit>(),
                    Timing: new SearchTiming(0.0, 0.0, totalSw.Elapsed.TotalMilliseconds)),
                new SearchExplain(Array.Empty<RelaxationStep>(), 0.0, false, 0));
        }

        if (tokens.Count == 0 && !hasAnyFilterCriteria && !canAttemptVector)
        {
            totalSw.Stop();
            return (
                new SearchResult(
                    Hits: Array.Empty<SearchHit>(),
                    Timing: new SearchTiming(0.0, 0.0, totalSw.Elapsed.TotalMilliseconds)),
                new SearchExplain(Array.Empty<RelaxationStep>(), 0.0, false, 0));
        }

        await using SqliteConnection connection = await OpenConnectionAsync(loadVec0: false, ct).ConfigureAwait(false);
        connection.CreateFunction<string?, string>("ru_norm", RuNormalizer.NormalizeForSearch, isDeterministic: true);

        var candidateMap = new Dictionary<string, CandidateState>(StringComparer.Ordinal);
        var lexicalRanked = new List<RankedCandidate>();
        var lexicalSw = Stopwatch.StartNew();

        if (useLexical)
        {
            var rawLexical = new List<RawCandidate>();

            if (longTokens.Count > 0)
            {
                string ftsExpr = string.Join(
                    " OR ",
                    longTokens.Select(static t => $"\"{t.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));

                await using (SqliteCommand cmd = connection.CreateCommand())
                {
                    cmd.CommandText = """
                        SELECT
                            c.path,
                            n.title,
                            n.type,
                            n.usecase,
                            n.status,
                            n.tags_json,
                            n.up_json,
                            c.header_breadcrumb,
                            c.body,
                            (-bm25(chunks_fts)) AS base_score
                        FROM chunks_fts
                        INNER JOIN chunks c ON c.chunk_id = chunks_fts.chunk_id
                        INNER JOIN notes n ON n.path = c.path
                        WHERE chunks_fts MATCH @ftsExpr
                        ORDER BY base_score DESC
                        LIMIT @candidateLimit;
                        """;
                    cmd.Parameters.AddWithValue("@ftsExpr", ftsExpr);
                    cmd.Parameters.AddWithValue("@candidateLimit", candidateLimit);

                    await ReadCandidatesAsync(cmd, rawLexical, ct).ConfigureAwait(false);
                }

                HashSet<string> matchedPaths = rawLexical.Select(static c => c.Path).ToHashSet(StringComparer.Ordinal);
                await using SqliteCommand titleCmd = connection.CreateCommand();
                var titleClauses = new StringBuilder();
                for (int i = 0; i < longTokens.Count; i++)
                {
                    if (i > 0)
                    {
                        titleClauses.Append(" OR ");
                    }

                    string paramName = $"@ttok{i}";
                    titleClauses.Append($"ru_norm(n.title) LIKE '%' || {paramName} || '%'");
                    titleCmd.Parameters.AddWithValue(paramName, longTokens[i]);
                }

                titleCmd.CommandText = $"""
                    SELECT
                        n.path,
                        n.title,
                        n.type,
                        n.usecase,
                        n.status,
                        n.tags_json,
                        n.up_json,
                        COALESCE(c.header_breadcrumb, ''),
                        COALESCE(c.body, ''),
                        0.0 AS base_score
                    FROM notes n
                    LEFT JOIN chunks c ON c.path = n.path AND c.chunk_index = 0
                    WHERE ({titleClauses})
                    LIMIT @candidateLimit;
                    """;
                titleCmd.Parameters.AddWithValue("@candidateLimit", candidateLimit);

                var titleCandidates = new List<RawCandidate>();
                await ReadCandidatesAsync(titleCmd, titleCandidates, ct).ConfigureAwait(false);
                foreach (RawCandidate tc in titleCandidates)
                {
                    if (!matchedPaths.Contains(tc.Path))
                    {
                        rawLexical.Add(tc);
                    }
                }
            }
            else if (tokens.Count > 0)
            {
                await using SqliteCommand cmd = connection.CreateCommand();
                var clauses = new StringBuilder();
                for (int i = 0; i < tokens.Count; i++)
                {
                    if (i > 0)
                    {
                        clauses.Append(" OR ");
                    }

                    string paramName = $"@tok{i}";
                    clauses.Append(
                        $"(c.body_norm LIKE '%' || {paramName} || '%' " +
                        $"OR n.title LIKE '%' || {paramName} || '%' " +
                        $"OR ru_norm(n.title) LIKE '%' || {paramName} || '%')");
                    cmd.Parameters.AddWithValue(paramName, tokens[i]);
                }

                cmd.CommandText = $"""
                    SELECT
                        c.path,
                        n.title,
                        n.type,
                        n.usecase,
                        n.status,
                        n.tags_json,
                        n.up_json,
                        c.header_breadcrumb,
                        c.body,
                        1.0 AS base_score
                    FROM chunks c
                    INNER JOIN notes n ON n.path = c.path
                    WHERE ({clauses})
                    LIMIT @candidateLimit;
                    """;
                cmd.Parameters.AddWithValue("@candidateLimit", candidateLimit);

                await ReadCandidatesAsync(cmd, rawLexical, ct).ConfigureAwait(false);
            }
            else if (hasAnyFilterCriteria)
            {
                await using SqliteCommand cmd = connection.CreateCommand();
                cmd.CommandText = """
                    SELECT
                        c.path,
                        n.title,
                        n.type,
                        n.usecase,
                        n.status,
                        n.tags_json,
                        n.up_json,
                        c.header_breadcrumb,
                        c.body,
                        1.0 AS base_score
                    FROM chunks c
                    INNER JOIN notes n ON n.path = c.path;
                    """;

                await ReadCandidatesAsync(cmd, rawLexical, ct).ConfigureAwait(false);
            }

            foreach (RawCandidate raw in rawLexical)
            {
                ct.ThrowIfCancellationRequested();

                if (!candidateMap.TryGetValue(raw.Path, out CandidateState? state))
                {
                    state = CreateCandidateState(raw);
                    state.LexicalScore = raw.BaseScore;
                    candidateMap[raw.Path] = state;
                }
                else if (raw.BaseScore > state.LexicalScore)
                {
                    state.LexicalScore = raw.BaseScore;
                    state.HeaderBreadcrumb = raw.HeaderBreadcrumb;
                    state.Body = raw.Body;
                }
            }

            lexicalRanked = candidateMap.Values
                .OrderByDescending(static x => x.LexicalScore)
                .ThenBy(static x => x.Path, StringComparer.Ordinal)
                .Select(static x => new RankedCandidate(x.Path, x.LexicalScore))
                .ToList();
        }

        lexicalSw.Stop();

        var vectorSw = Stopwatch.StartNew();
        var vectorRanked = new List<RankedCandidate>();
        bool vectorUsed = false;

        if (canAttemptVector)
        {
            int vectorRowCount = await CountRowsAsync(connection, transaction: null, "chunk_vectors", ct)
                .ConfigureAwait(false);

            if (vectorRowCount > 0)
            {
                float[] queryVector = await _embeddingClient!.EmbedQueryAsync(query.Text, ct).ConfigureAwait(false);

                if (queryVector.Length > 0)
                {
                    vectorUsed = true;
                    var bestVectorByPath = new Dictionary<string, (double Score, RawCandidate Raw)>(StringComparer.Ordinal);

                    await using SqliteCommand vecCmd = connection.CreateCommand();
                    vecCmd.CommandText = """
                        SELECT
                            c.path,
                            n.title,
                            n.type,
                            n.usecase,
                            n.status,
                            n.tags_json,
                            n.up_json,
                            c.header_breadcrumb,
                            c.body,
                            cv.embedding
                        FROM chunk_vectors cv
                        INNER JOIN chunks c ON c.chunk_id = cv.chunk_id
                        INNER JOIN notes n ON n.path = c.path;
                        """;

                    await using SqliteDataReader reader = await vecCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false))
                    {
                        ct.ThrowIfCancellationRequested();

                        byte[] blob = (byte[])reader.GetValue(9);
                        if (blob.Length == 0 || blob.Length % sizeof(float) != 0)
                        {
                            continue;
                        }

                        float[] docVector = SimdCosine.FromBytes(blob);
                        if (docVector.Length != queryVector.Length)
                        {
                            continue;
                        }

                        double cos = SimdCosine.CosineNormalized(queryVector, docVector);
                        string path = reader.GetString(0);

                        if (!bestVectorByPath.TryGetValue(path, out (double Score, RawCandidate Raw) existingVec) ||
                            cos > existingVec.Score)
                        {
                            var raw = new RawCandidate(
                                Path: path,
                                Title: reader.GetString(1),
                                Type: reader.GetString(2),
                                Usecase: reader.GetString(3),
                                Status: reader.GetString(4),
                                TagsJson: reader.GetString(5),
                                UpJson: reader.GetString(6),
                                HeaderBreadcrumb: reader.GetString(7),
                                Body: reader.GetString(8),
                                BaseScore: cos);

                            bestVectorByPath[path] = (cos, raw);
                        }
                    }

                    foreach (KeyValuePair<string, (double Score, RawCandidate Raw)> kvp in bestVectorByPath)
                    {
                        if (!candidateMap.TryGetValue(kvp.Key, out CandidateState? state))
                        {
                            state = CreateCandidateState(kvp.Value.Raw);
                            state.VectorScore = kvp.Value.Score;
                            candidateMap[kvp.Key] = state;
                        }
                        else
                        {
                            state.VectorScore = kvp.Value.Score;
                            if (state.Body.Length == 0)
                            {
                                state.HeaderBreadcrumb = kvp.Value.Raw.HeaderBreadcrumb;
                                state.Body = kvp.Value.Raw.Body;
                            }
                        }
                    }

                    vectorRanked = bestVectorByPath.Values
                        .OrderByDescending(static x => x.Score)
                        .ThenBy(static x => x.Raw.Path, StringComparer.Ordinal)
                        .Take(candidateLimit)
                        .Select(static x => new RankedCandidate(x.Raw.Path, x.Score))
                        .ToList();
                }
            }
        }

        vectorSw.Stop();

        var directCandidates = new List<CandidateState>();
        if (useLexical && vectorUsed)
        {
            IReadOnlyList<RrfHit> fused = RrfMerger.Merge(
                lexicalRanked,
                vectorRanked,
                k: RrfConstantK,
                lexicalWeight: 1.0,
                vectorWeight: 1.0,
                limit: candidateLimit);

            foreach (RrfHit hit in fused)
            {
                if (candidateMap.TryGetValue(hit.Id, out CandidateState? state))
                {
                    state.Score = hit.Score;
                    state.IsDirectHit = true;
                    directCandidates.Add(state);
                }
            }
        }
        else if (useLexical)
        {
            foreach (RankedCandidate ranked in lexicalRanked)
            {
                if (candidateMap.TryGetValue(ranked.Id, out CandidateState? state))
                {
                    state.Score = ranked.Score;
                    state.IsDirectHit = true;
                    directCandidates.Add(state);
                }
            }
        }
        else if (vectorUsed)
        {
            foreach (RankedCandidate ranked in vectorRanked)
            {
                if (candidateMap.TryGetValue(ranked.Id, out CandidateState? state))
                {
                    state.Score = ranked.Score;
                    state.IsDirectHit = true;
                    directCandidates.Add(state);
                }
            }
        }

        var allCandidates = new List<CandidateState>(directCandidates);

        if (useGraph && directCandidates.Count > 0)
        {
            List<string> seedPaths = directCandidates
                .Take(3)
                .Select(static c => c.Path)
                .ToList();
            HashSet<string> directPathSet = directCandidates
                .Select(static c => c.Path)
                .ToHashSet(StringComparer.Ordinal);

            List<(string NeighborPath, double Weight)> neighbors = await FetchOneHopNeighborsAsync(
                connection,
                seedPaths,
                directPathSet,
                ct).ConfigureAwait(false);

            if (neighbors.Count > 0)
            {
                double minDirectScore = directCandidates.Min(static c => c.Score);
                double floor = minDirectScore > 0.0 ? minDirectScore * 0.5 : 0.001;
                double maxEdgeWeight = neighbors.Max(static n => n.Weight);
                if (maxEdgeWeight <= 0.0)
                {
                    maxEdgeWeight = 1.0;
                }

                Dictionary<string, RawCandidate> neighborNotes = await FetchNotesByPathsAsync(
                    connection,
                    neighbors.Select(static n => n.NeighborPath).ToList(),
                    ct).ConfigureAwait(false);

                foreach ((string neighborPath, double weight) in neighbors)
                {
                    if (!neighborNotes.TryGetValue(neighborPath, out RawCandidate? raw))
                    {
                        continue;
                    }

                    CandidateState neighborState = CreateCandidateState(raw);
                    double normalizedWeight = Math.Clamp(weight / maxEdgeWeight, 0.01, 1.0);
                    neighborState.Score = floor * normalizedWeight;
                    neighborState.IsDirectHit = false;
                    allCandidates.Add(neighborState);
                }
            }
        }

        int candidatesBeforeFilters = allCandidates.Count;
        var appliedRelaxations = new List<RelaxationStep>();
        List<CandidateState> selectedCandidates;

        if (filters is { Strict: true })
        {
            bool requireStatus = filterStatus is not null;
            bool requireType = filterType is not null;
            bool requireTags = filterTags.Count > 0;
            bool requirePrefix = filterPrefix is not null;
            bool requireUsecase = filterUsecase is not null;
            bool requireUp = filterUp.Count > 0;

            int minHits = filters.MinHits > 0 ? filters.MinHits : 1;

            selectedCandidates = ApplyStrictFilters(
                allCandidates,
                requireStatus ? filterStatus : null,
                requireType ? filterType : null,
                requireTags ? filterTags : null,
                requirePrefix ? filterPrefix : null,
                requireUsecase ? filterUsecase : null,
                requireUp ? filterUp : null);

            int order = 1;

            if (selectedCandidates.Count < minHits && requireStatus)
            {
                int before = selectedCandidates.Count;
                requireStatus = false;
                selectedCandidates = ApplyStrictFilters(
                    allCandidates,
                    null,
                    requireType ? filterType : null,
                    requireTags ? filterTags : null,
                    requirePrefix ? filterPrefix : null,
                    requireUsecase ? filterUsecase : null,
                    requireUp ? filterUp : null);
                appliedRelaxations.Add(new RelaxationStep(
                    order++,
                    "status",
                    $"Relaxed 'status' filter because hits ({before}) < MinHits ({minHits})."));
            }

            if (selectedCandidates.Count < minHits && requireType)
            {
                int before = selectedCandidates.Count;
                requireType = false;
                selectedCandidates = ApplyStrictFilters(
                    allCandidates,
                    null,
                    null,
                    requireTags ? filterTags : null,
                    requirePrefix ? filterPrefix : null,
                    requireUsecase ? filterUsecase : null,
                    requireUp ? filterUp : null);
                appliedRelaxations.Add(new RelaxationStep(
                    order++,
                    "type",
                    $"Relaxed 'type' filter because hits ({before}) < MinHits ({minHits})."));
            }

            if (selectedCandidates.Count < minHits && requireTags)
            {
                int before = selectedCandidates.Count;
                requireTags = false;
                selectedCandidates = ApplyStrictFilters(
                    allCandidates,
                    null,
                    null,
                    null,
                    requirePrefix ? filterPrefix : null,
                    requireUsecase ? filterUsecase : null,
                    requireUp ? filterUp : null);
                appliedRelaxations.Add(new RelaxationStep(
                    order++,
                    "tags",
                    $"Relaxed 'tags' filter because hits ({before}) < MinHits ({minHits})."));
            }

            if (selectedCandidates.Count < minHits && requirePrefix)
            {
                int before = selectedCandidates.Count;
                requirePrefix = false;
                selectedCandidates = ApplyStrictFilters(
                    allCandidates,
                    null,
                    null,
                    null,
                    null,
                    requireUsecase ? filterUsecase : null,
                    requireUp ? filterUp : null);
                appliedRelaxations.Add(new RelaxationStep(
                    order++,
                    "prefix",
                    $"Relaxed 'prefix' filter because hits ({before}) < MinHits ({minHits})."));
            }
        }
        else
        {
            selectedCandidates = allCandidates;
        }

        double boostScale = vectorUsed ? 0.005 : 1.0;
        double maxTagBoostApplied = 0.0;

        foreach (CandidateState candidate in selectedCandidates)
        {
            ct.ThrowIfCancellationRequested();

            if (!candidate.IsDirectHit)
            {
                continue;
            }

            if (filterType is not null &&
                string.Equals(candidate.Type, filterType, StringComparison.OrdinalIgnoreCase))
            {
                candidate.Score += 0.25 * boostScale;
            }

            if (filterPrefix is not null &&
                candidate.Path.StartsWith(filterPrefix, StringComparison.OrdinalIgnoreCase))
            {
                candidate.Score += 0.20 * boostScale;
            }

            if (filterTags.Count > 0 && candidate.Tags.Length > 0)
            {
                int matchedTagCount = 0;
                foreach (string noteTag in candidate.Tags)
                {
                    if (filterTags.Contains(noteTag.Trim().TrimStart('#')))
                    {
                        matchedTagCount++;
                    }
                }

                double tagBoost = Math.Min(0.45, matchedTagCount * 0.15) * boostScale;
                candidate.Score += tagBoost;
                if (tagBoost > maxTagBoostApplied)
                {
                    maxTagBoostApplied = tagBoost;
                }
            }

            if (filterUsecase is not null &&
                string.Equals(candidate.Usecase, filterUsecase, StringComparison.OrdinalIgnoreCase))
            {
                candidate.Score += 0.15 * boostScale;
            }

            if (filterStatus is not null &&
                string.Equals(candidate.Status, filterStatus, StringComparison.OrdinalIgnoreCase))
            {
                candidate.Score += 0.15 * boostScale;
            }

            if (filterUp.Count > 0 && candidate.Up.Length > 0 &&
                candidate.Up.Any(u => filterUp.Contains(NormalizePath(u))))
            {
                candidate.Score += 0.20 * boostScale;
            }

            if (tokens.Count > 0 && HasTitleMatch(candidate.Title, tokens))
            {
                candidate.Score += 0.30 * boostScale;
            }
        }

        List<CandidateState> topCandidates = selectedCandidates
            .OrderByDescending(static x => x.IsDirectHit)
            .ThenByDescending(static x => x.Score)
            .ThenBy(static x => x.Path, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var hits = new List<SearchHit>(topCandidates.Count);
        foreach (CandidateState item in topCandidates)
        {
            string snippet = BuildSnippet(item.Body, tokens, query.Text);
            string? header = string.IsNullOrWhiteSpace(item.HeaderBreadcrumb) ? null : item.HeaderBreadcrumb;

            hits.Add(new SearchHit(
                Path: item.Path,
                Title: item.Title,
                Snippet: snippet,
                Score: item.Score,
                Header: header,
                Tags: item.Tags));
        }

        totalSw.Stop();

        var result = new SearchResult(
            Hits: hits,
            Timing: new SearchTiming(
                LexicalMs: lexicalSw.Elapsed.TotalMilliseconds,
                VectorMs: vectorSw.Elapsed.TotalMilliseconds,
                TotalMs: totalSw.Elapsed.TotalMilliseconds));

        var explain = new SearchExplain(
            Applied: appliedRelaxations,
            TagBoost: maxTagBoostApplied,
            VectorUsed: vectorUsed,
            CandidatesBeforeFilters: candidatesBeforeFilters);

        return (result, explain);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _writeLock.Dispose();
    }

    private async Task<SqliteConnection> OpenConnectionAsync(bool loadVec0, CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA busy_timeout = 5000;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA mmap_size = 268435456;
            PRAGMA temp_store = MEMORY;
            PRAGMA foreign_keys = ON;
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        if (loadVec0)
        {
            try
            {
                connection.EnableExtensions(true);
                connection.LoadExtension("vec0", "sqlite3_vec_init");
            }
            catch (Exception)
            {
                // Keep operating via chunk_vectors when vec0 is unavailable.
            }
        }

        return connection;
    }

    private static async Task UpsertNoteCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NoteDocument doc,
        string nowIso,
        bool vec0Available,
        CancellationToken ct)
    {
        var normalizedPath = NormalizePath(doc.Path);
        var normalizedDoc = string.Equals(doc.Path, normalizedPath, StringComparison.Ordinal)
            ? doc
            : doc with { Path = normalizedPath };

        var policy = normalizedDoc.Metadata.Type == NoteType.Diary
            ? new ChunkPolicy(Strategy: "by_entry")
            : new ChunkPolicy(Strategy: "by_header");

        var chunks = MarkdownChunker.Chunk(normalizedDoc, policy);

        if (vec0Available)
        {
            try
            {
                await using var delVec0Cmd = connection.CreateCommand();
                delVec0Cmd.Transaction = transaction;
                delVec0Cmd.CommandText = """
                    DELETE FROM vec_chunks
                    WHERE chunk_id IN (SELECT chunk_id FROM chunks WHERE path = @path);
                    """;
                delVec0Cmd.Parameters.AddWithValue("@path", normalizedPath);
                await delVec0Cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // Ignore if vec_chunks virtual table does not exist in this database file.
            }
        }

        await using (var delFtsCmd = connection.CreateCommand())
        {
            delFtsCmd.Transaction = transaction;
            delFtsCmd.CommandText = """
                DELETE FROM chunks_fts
                WHERE chunk_id IN (SELECT chunk_id FROM chunks WHERE path = @path);
                """;
            delFtsCmd.Parameters.AddWithValue("@path", normalizedPath);
            await delFtsCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var delQueueCmd = connection.CreateCommand())
        {
            delQueueCmd.Transaction = transaction;
            delQueueCmd.CommandText = """
                DELETE FROM embedding_queue
                WHERE chunk_id IN (SELECT chunk_id FROM chunks WHERE path = @path);
                """;
            delQueueCmd.Parameters.AddWithValue("@path", normalizedPath);
            await delQueueCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var delChunksCmd = connection.CreateCommand())
        {
            delChunksCmd.Transaction = transaction;
            delChunksCmd.CommandText = "DELETE FROM chunks WHERE path = @path;";
            delChunksCmd.Parameters.AddWithValue("@path", normalizedPath);
            await delChunksCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var delEdgesCmd = connection.CreateCommand())
        {
            delEdgesCmd.Transaction = transaction;
            delEdgesCmd.CommandText = "DELETE FROM edges WHERE src_path = @path;";
            delEdgesCmd.Parameters.AddWithValue("@path", normalizedPath);
            await delEdgesCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var noteCmd = connection.CreateCommand())
        {
            noteCmd.Transaction = transaction;
            noteCmd.CommandText = """
                INSERT OR REPLACE INTO notes (
                    path,
                    id,
                    title,
                    type,
                    usecase,
                    status,
                    tags_json,
                    up_json,
                    content_hash,
                    modified_at,
                    review_date
                )
                VALUES (
                    @path,
                    @id,
                    @title,
                    @type,
                    @usecase,
                    @status,
                    @tags_json,
                    @up_json,
                    @content_hash,
                    @modified_at,
                    @review_date
                );
                """;

            var tagsJson = JsonSerializer.Serialize(normalizedDoc.Metadata.Tags, JsonOptions);
            var upJson = JsonSerializer.Serialize(normalizedDoc.Metadata.Up, JsonOptions);
            var modifiedAtIso = normalizedDoc.FileModifiedAt.ToString("O", CultureInfo.InvariantCulture);
            var reviewDateIso = normalizedDoc.Metadata.ReviewDate?.ToString("O", CultureInfo.InvariantCulture);

            noteCmd.Parameters.AddWithValue("@path", normalizedPath);
            noteCmd.Parameters.AddWithValue("@id", normalizedDoc.Metadata.Id);
            noteCmd.Parameters.AddWithValue("@title", normalizedDoc.Metadata.Title);
            noteCmd.Parameters.AddWithValue("@type", normalizedDoc.Metadata.Type.ToString().ToLowerInvariant());
            noteCmd.Parameters.AddWithValue("@usecase", normalizedDoc.Metadata.Usecase);
            noteCmd.Parameters.AddWithValue("@status", normalizedDoc.Metadata.Status.ToString().ToLowerInvariant());
            noteCmd.Parameters.AddWithValue("@tags_json", tagsJson);
            noteCmd.Parameters.AddWithValue("@up_json", upJson);
            noteCmd.Parameters.AddWithValue("@content_hash", normalizedDoc.ContentHash);
            noteCmd.Parameters.AddWithValue("@modified_at", modifiedAtIso);
            noteCmd.Parameters.AddWithValue("@review_date", (object?)reviewDateIso ?? DBNull.Value);

            await noteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (chunks.Count > 0)
        {
            await using var chunkCmd = connection.CreateCommand();
            chunkCmd.Transaction = transaction;
            chunkCmd.CommandText = """
                INSERT INTO chunks (
                    chunk_id,
                    path,
                    anchor,
                    header_breadcrumb,
                    body,
                    body_norm,
                    embed_text,
                    chunk_index
                )
                VALUES (
                    @chunk_id,
                    @path,
                    @anchor,
                    @header_breadcrumb,
                    @body,
                    @body_norm,
                    @embed_text,
                    @chunk_index
                );
                """;
            var pChunkId = chunkCmd.Parameters.Add("@chunk_id", SqliteType.Text);
            var pChunkPath = chunkCmd.Parameters.Add("@path", SqliteType.Text);
            var pAnchor = chunkCmd.Parameters.Add("@anchor", SqliteType.Text);
            var pBreadcrumb = chunkCmd.Parameters.Add("@header_breadcrumb", SqliteType.Text);
            var pBody = chunkCmd.Parameters.Add("@body", SqliteType.Text);
            var pBodyNorm = chunkCmd.Parameters.Add("@body_norm", SqliteType.Text);
            var pEmbedText = chunkCmd.Parameters.Add("@embed_text", SqliteType.Text);
            var pChunkIndex = chunkCmd.Parameters.Add("@chunk_index", SqliteType.Integer);

            await using var ftsCmd = connection.CreateCommand();
            ftsCmd.Transaction = transaction;
            ftsCmd.CommandText = """
                INSERT INTO chunks_fts (body_norm, chunk_id)
                VALUES (@body_norm, @chunk_id);
                """;
            var pFtsBodyNorm = ftsCmd.Parameters.Add("@body_norm", SqliteType.Text);
            var pFtsChunkId = ftsCmd.Parameters.Add("@chunk_id", SqliteType.Text);

            await using var queueCmd = connection.CreateCommand();
            queueCmd.Transaction = transaction;
            queueCmd.CommandText = """
                INSERT OR REPLACE INTO embedding_queue (
                    chunk_id,
                    status,
                    attempts,
                    last_error,
                    updated_at
                )
                VALUES (
                    @chunk_id,
                    'pending',
                    0,
                    NULL,
                    @updated_at
                );
                """;
            var pQueueChunkId = queueCmd.Parameters.Add("@chunk_id", SqliteType.Text);
            var pQueueUpdatedAt = queueCmd.Parameters.Add("@updated_at", SqliteType.Text);

            for (var i = 0; i < chunks.Count; i++)
            {
                var chunk = chunks[i];

                pChunkId.Value = chunk.ChunkId;
                pChunkPath.Value = normalizedPath;
                pAnchor.Value = chunk.Anchor;
                pBreadcrumb.Value = chunk.HeaderBreadcrumb;
                pBody.Value = chunk.Body;
                pBodyNorm.Value = chunk.BodyNorm;
                pEmbedText.Value = chunk.EmbedText;
                pChunkIndex.Value = i;
                await chunkCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                pFtsBodyNorm.Value = chunk.BodyNorm;
                pFtsChunkId.Value = chunk.ChunkId;
                await ftsCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                pQueueChunkId.Value = chunk.ChunkId;
                pQueueUpdatedAt.Value = nowIso;
                await queueCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
        }

        await InsertEdgesAsync(connection, transaction, normalizedPath, normalizedDoc.Metadata, ct).ConfigureAwait(false);
    }

    private static async Task InsertEdgesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string srcPath,
        NoteMetadata metadata,
        CancellationToken ct)
    {
        var hasEdges = metadata.Related.Count > 0 || metadata.Up.Count > 0 || metadata.Relations.Count > 0;
        if (!hasEdges)
        {
            return;
        }

        await using var edgeCmd = connection.CreateCommand();
        edgeCmd.Transaction = transaction;
        edgeCmd.CommandText = """
            INSERT OR REPLACE INTO edges (src_path, dst_path, kind, weight, origin)
            VALUES (@src_path, @dst_path, @kind, @weight, @origin);
            """;

        var pSrc = edgeCmd.Parameters.Add("@src_path", SqliteType.Text);
        var pDst = edgeCmd.Parameters.Add("@dst_path", SqliteType.Text);
        var pKind = edgeCmd.Parameters.Add("@kind", SqliteType.Text);
        var pWeight = edgeCmd.Parameters.Add("@weight", SqliteType.Real);
        var pOrigin = edgeCmd.Parameters.Add("@origin", SqliteType.Text);

        pSrc.Value = srcPath;

        foreach (var rawRelated in metadata.Related)
        {
            if (string.IsNullOrWhiteSpace(rawRelated))
            {
                continue;
            }

            var dstPath = NormalizePath(rawRelated);
            if (dstPath.Length == 0)
            {
                continue;
            }

            pDst.Value = dstPath;
            pKind.Value = "related";
            pWeight.Value = 1.0;
            pOrigin.Value = ResolveOrigin(metadata.LinksOrigin, rawRelated, dstPath);
            await edgeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var rawUp in metadata.Up)
        {
            if (string.IsNullOrWhiteSpace(rawUp))
            {
                continue;
            }

            var dstPath = NormalizePath(rawUp);
            if (dstPath.Length == 0)
            {
                continue;
            }

            pDst.Value = dstPath;
            pKind.Value = "up";
            pWeight.Value = 1.0;
            pOrigin.Value = ResolveOrigin(metadata.LinksOrigin, rawUp, dstPath);
            await edgeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        foreach (var relation in metadata.Relations)
        {
            if (string.IsNullOrWhiteSpace(relation.DstPath))
            {
                continue;
            }

            var dstPath = NormalizePath(relation.DstPath);
            if (dstPath.Length == 0)
            {
                continue;
            }

            var origin = string.IsNullOrWhiteSpace(relation.Origin)
                ? ResolveOrigin(metadata.LinksOrigin, relation.DstPath, dstPath)
                : relation.Origin.Trim();

            pDst.Value = dstPath;
            pKind.Value = FormatRelationKind(relation.Kind);
            pWeight.Value = relation.Weight;
            pOrigin.Value = origin;
            await edgeCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static string FormatRelationKind(RelationKind kind) => kind switch
    {
        RelationKind.Related => "related",
        RelationKind.Supports => "supports",
        RelationKind.Contradicts => "contradicts",
        RelationKind.PartOf => "part_of",
        RelationKind.DuplicateOf => "duplicate_of",
        RelationKind.Up => "up",
        _ => kind.ToString().ToLowerInvariant()
    };

    private static string ResolveOrigin(
        IReadOnlyDictionary<string, string> linksOrigin,
        string rawTarget,
        string normalizedTarget)
    {
        if (linksOrigin.TryGetValue(rawTarget, out var origin) && !string.IsNullOrWhiteSpace(origin))
        {
            return origin;
        }

        if (linksOrigin.TryGetValue(normalizedTarget, out origin) && !string.IsNullOrWhiteSpace(origin))
        {
            return origin;
        }

        return "frontmatter";
    }

    private static async Task DeleteNoteCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string normalizedPath,
        bool vec0Available,
        CancellationToken ct)
    {
        if (vec0Available)
        {
            try
            {
                await using var delVec0Cmd = connection.CreateCommand();
                delVec0Cmd.Transaction = transaction;
                delVec0Cmd.CommandText = """
                    DELETE FROM vec_chunks
                    WHERE chunk_id IN (SELECT chunk_id FROM chunks WHERE path = @path);
                    """;
                delVec0Cmd.Parameters.AddWithValue("@path", normalizedPath);
                await delVec0Cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }
            catch (SqliteException)
            {
                // Ignore if vec_chunks virtual table does not exist in this database file.
            }
        }

        await using (var delFtsCmd = connection.CreateCommand())
        {
            delFtsCmd.Transaction = transaction;
            delFtsCmd.CommandText = """
                DELETE FROM chunks_fts
                WHERE chunk_id IN (SELECT chunk_id FROM chunks WHERE path = @path);
                """;
            delFtsCmd.Parameters.AddWithValue("@path", normalizedPath);
            await delFtsCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var delChunksCmd = connection.CreateCommand())
        {
            delChunksCmd.Transaction = transaction;
            delChunksCmd.CommandText = "DELETE FROM chunks WHERE path = @path;";
            delChunksCmd.Parameters.AddWithValue("@path", normalizedPath);
            await delChunksCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var delEdgesCmd = connection.CreateCommand())
        {
            delEdgesCmd.Transaction = transaction;
            delEdgesCmd.CommandText = "DELETE FROM edges WHERE src_path = @path OR dst_path = @path;";
            delEdgesCmd.Parameters.AddWithValue("@path", normalizedPath);
            await delEdgesCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var delNoteCmd = connection.CreateCommand())
        {
            delNoteCmd.Transaction = transaction;
            delNoteCmd.CommandText = "DELETE FROM notes WHERE path = @path;";
            delNoteCmd.Parameters.AddWithValue("@path", normalizedPath);
            await delNoteCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await using (var delOrphanQueueCmd = connection.CreateCommand())
        {
            delOrphanQueueCmd.Transaction = transaction;
            delOrphanQueueCmd.CommandText = """
                DELETE FROM embedding_queue
                WHERE chunk_id NOT IN (SELECT chunk_id FROM chunks);
                """;
            await delOrphanQueueCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }
    }

    private static async Task<int> CountRowsAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string tableName,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = tableName switch
        {
            "chunks" => "SELECT COUNT(*) FROM chunks;",
            "chunk_vectors" => "SELECT COUNT(*) FROM chunk_vectors;",
            _ => throw new ArgumentOutOfRangeException(nameof(tableName), tableName, "Unsupported table.")
        };

        object? scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task<(string EmbeddingModel, int Dimensions)> ReadEmbeddingMetaAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        string model = "none";
        int dimensions = 0;

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            SELECT key, value
            FROM index_meta
            WHERE key IN (@modelKey, @dimsKey);
            """;
        cmd.Parameters.AddWithValue("@modelKey", EmbeddingModelMetaKey);
        cmd.Parameters.AddWithValue("@dimsKey", DimensionsMetaKey);

        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string key = reader.GetString(0);
            string val = reader.GetString(1);

            if (string.Equals(key, EmbeddingModelMetaKey, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(val))
            {
                model = val;
            }
            else if (string.Equals(key, DimensionsMetaKey, StringComparison.Ordinal) &&
                     int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedDims) &&
                     parsedDims > 0)
            {
                dimensions = parsedDims;
            }
        }

        return (model, dimensions);
    }

    private static async Task ReadCandidatesAsync(
        SqliteCommand cmd,
        List<RawCandidate> target,
        CancellationToken ct)
    {
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            target.Add(new RawCandidate(
                Path: reader.GetString(0),
                Title: reader.GetString(1),
                Type: reader.GetString(2),
                Usecase: reader.GetString(3),
                Status: reader.GetString(4),
                TagsJson: reader.GetString(5),
                UpJson: reader.GetString(6),
                HeaderBreadcrumb: reader.GetString(7),
                Body: reader.GetString(8),
                BaseScore: reader.IsDBNull(9) ? 0.0 : reader.GetDouble(9)));
        }
    }

    private static async Task<List<(string NeighborPath, double Weight)>> FetchOneHopNeighborsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> seedPaths,
        HashSet<string> excludedPaths,
        CancellationToken ct)
    {
        if (seedPaths.Count == 0)
        {
            return new List<(string, double)>();
        }

        await using SqliteCommand cmd = connection.CreateCommand();
        var placeholders = new string[seedPaths.Count];
        for (int i = 0; i < seedPaths.Count; i++)
        {
            string pName = $"@s{i}";
            placeholders[i] = pName;
            cmd.Parameters.AddWithValue(pName, seedPaths[i]);
        }

        string inClause = string.Join(", ", placeholders);
        cmd.CommandText = $"""
            SELECT src_path, dst_path, weight
            FROM edges
            WHERE src_path IN ({inClause}) OR dst_path IN ({inClause});
            """;

        HashSet<string> seedSet = seedPaths.ToHashSet(StringComparer.Ordinal);
        var bestByNeighbor = new Dictionary<string, double>(StringComparer.Ordinal);

        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string src = reader.GetString(0);
            string dst = reader.GetString(1);
            double weight = reader.GetDouble(2);

            if (seedSet.Contains(src) && !excludedPaths.Contains(dst))
            {
                if (!bestByNeighbor.TryGetValue(dst, out double existing) || weight > existing)
                {
                    bestByNeighbor[dst] = weight;
                }
            }

            if (seedSet.Contains(dst) && !excludedPaths.Contains(src))
            {
                if (!bestByNeighbor.TryGetValue(src, out double existing) || weight > existing)
                {
                    bestByNeighbor[src] = weight;
                }
            }
        }

        return bestByNeighbor
            .OrderByDescending(static kvp => kvp.Value)
            .ThenBy(static kvp => kvp.Key, StringComparer.Ordinal)
            .Select(static kvp => (kvp.Key, kvp.Value))
            .ToList();
    }

    private static async Task<Dictionary<string, RawCandidate>> FetchNotesByPathsAsync(
        SqliteConnection connection,
        IReadOnlyList<string> paths,
        CancellationToken ct)
    {
        var result = new Dictionary<string, RawCandidate>(StringComparer.Ordinal);
        if (paths.Count == 0)
        {
            return result;
        }

        await using SqliteCommand cmd = connection.CreateCommand();
        var placeholders = new string[paths.Count];
        for (int i = 0; i < paths.Count; i++)
        {
            string pName = $"@p{i}";
            placeholders[i] = pName;
            cmd.Parameters.AddWithValue(pName, paths[i]);
        }

        string inClause = string.Join(", ", placeholders);
        cmd.CommandText = $"""
            SELECT
                n.path,
                n.title,
                n.type,
                n.usecase,
                n.status,
                n.tags_json,
                n.up_json,
                COALESCE(c.header_breadcrumb, ''),
                COALESCE(c.body, ''),
                0.0 AS base_score
            FROM notes n
            LEFT JOIN chunks c ON c.path = n.path AND c.chunk_index = 0
            WHERE n.path IN ({inClause});
            """;

        var rows = new List<RawCandidate>();
        await ReadCandidatesAsync(cmd, rows, ct).ConfigureAwait(false);
        foreach (RawCandidate row in rows)
        {
            result[row.Path] = row;
        }

        return result;
    }

    private static List<CandidateState> ApplyStrictFilters(
        List<CandidateState> candidates,
        string? status,
        string? type,
        HashSet<string>? tags,
        string? prefix,
        string? usecase,
        HashSet<string>? up)
    {
        var filtered = new List<CandidateState>(candidates.Count);

        foreach (CandidateState candidate in candidates)
        {
            if (status is not null &&
                !string.Equals(candidate.Status, status, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (type is not null &&
                !string.Equals(candidate.Type, type, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (tags is { Count: > 0 })
            {
                HashSet<string> candidateTags = candidate.Tags
                    .Select(static t => t.Trim().TrimStart('#'))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!tags.All(candidateTags.Contains))
                {
                    continue;
                }
            }

            if (prefix is not null &&
                !candidate.Path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (usecase is not null &&
                !string.Equals(candidate.Usecase, usecase, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (up is { Count: > 0 })
            {
                HashSet<string> candidateUp = candidate.Up
                    .Select(NormalizePath)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                if (!up.Any(candidateUp.Contains))
                {
                    continue;
                }
            }

            filtered.Add(candidate);
        }

        return filtered;
    }

    private static CandidateState CreateCandidateState(RawCandidate raw)
    {
        return new CandidateState(
            Path: raw.Path,
            Title: raw.Title,
            Type: raw.Type,
            Usecase: raw.Usecase,
            Status: raw.Status,
            Tags: DeserializeStringArray(raw.TagsJson),
            Up: DeserializeStringArray(raw.UpJson),
            HeaderBreadcrumb: raw.HeaderBreadcrumb,
            Body: raw.Body);
    }

    private static string[] DeserializeStringArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? Array.Empty<string>();
    }

    private static bool HasTitleMatch(string title, IReadOnlyList<string> queryTokens)
    {
        if (string.IsNullOrWhiteSpace(title) || queryTokens.Count == 0)
        {
            return false;
        }

        var titleTokens = RuNormalizer.TokenizeNormalized(title);
        var titleNorm = string.Join(' ', titleTokens);

        foreach (var token in queryTokens)
        {
            if (token.Length < 3)
            {
                if (titleTokens.Contains(token, StringComparer.Ordinal) ||
                    (token.Length >= 2 && titleNorm.Contains(token, StringComparison.Ordinal)))
                {
                    return true;
                }
            }
            else if (titleNorm.Contains(token, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static string BuildSnippet(string body, IReadOnlyList<string> queryTokens, string? rawQueryText)
    {
        var collapsed = CollapseWhitespace(body);
        if (collapsed.Length == 0)
        {
            return string.Empty;
        }

        const int targetLength = 180;
        var matchIndex = FindFirstMatchOffset(collapsed, queryTokens, rawQueryText);

        if (collapsed.Length <= targetLength)
        {
            return collapsed;
        }

        var start = Math.Max(0, matchIndex - 60);
        if (start + targetLength > collapsed.Length)
        {
            start = Math.Max(0, collapsed.Length - targetLength);
        }

        if (start > 0)
        {
            var lookbackCount = Math.Min(12, start);
            var lookbackStart = Math.Min(start + lookbackCount, collapsed.Length - 1);
            var prevSpace = collapsed.LastIndexOf(' ', lookbackStart, Math.Min(lookbackCount, lookbackStart + 1));
            if (prevSpace >= 0 && prevSpace < matchIndex)
            {
                start = prevSpace + 1;
            }
        }

        var end = Math.Min(collapsed.Length, start + targetLength);
        if (end < collapsed.Length)
        {
            var searchCount = Math.Min(20, end - start);
            if (searchCount > 0)
            {
                var lastSpace = collapsed.LastIndexOf(' ', end - 1, searchCount);
                if (lastSpace > start + 20)
                {
                    end = lastSpace;
                }
            }
        }

        var slice = collapsed[start..end].Trim();
        var prefix = start > 0 ? "..." : string.Empty;
        var suffix = end < collapsed.Length ? "..." : string.Empty;
        return string.Concat(prefix, slice, suffix);
    }

    private static int FindFirstMatchOffset(string collapsed, IReadOnlyList<string> queryTokens, string? rawQueryText)
    {
        if (queryTokens.Count > 0)
        {
            var spans = new List<(int Start, string Norm)>();
            var span = collapsed.AsSpan();
            var i = 0;

            while (i < span.Length)
            {
                while (i < span.Length && !char.IsLetterOrDigit(span[i]))
                {
                    i++;
                }

                if (i >= span.Length)
                {
                    break;
                }

                var wordStart = i;
                while (i < span.Length && char.IsLetterOrDigit(span[i]))
                {
                    i++;
                }

                var wordNorm = RuNormalizer.NormalizeForSearch(collapsed[wordStart..i]);
                if (wordNorm.Length > 0)
                {
                    if (queryTokens.Contains(wordNorm, StringComparer.Ordinal))
                    {
                        return wordStart;
                    }

                    spans.Add((wordStart, wordNorm));
                }
            }

            foreach (var (wordStart, wordNorm) in spans)
            {
                foreach (var token in queryTokens)
                {
                    if (wordNorm.Contains(token, StringComparison.Ordinal))
                    {
                        return wordStart;
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(rawQueryText))
        {
            var rawIdx = collapsed.IndexOf(rawQueryText.Trim(), StringComparison.OrdinalIgnoreCase);
            if (rawIdx >= 0)
            {
                return rawIdx;
            }
        }

        return 0;
    }

    private static string CollapseWhitespace(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        var prevSpace = true;

        foreach (var ch in text)
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
        var unified = path.Replace('\\', '/').Trim();
        while (unified.StartsWith("./", StringComparison.Ordinal))
        {
            unified = unified[2..];
        }

        return unified.TrimStart('/');
    }

    private sealed record RawCandidate(
        string Path,
        string Title,
        string Type,
        string Usecase,
        string Status,
        string TagsJson,
        string UpJson,
        string HeaderBreadcrumb,
        string Body,
        double BaseScore);

    private sealed class CandidateState
    {
        public CandidateState(
            string Path,
            string Title,
            string Type,
            string Usecase,
            string Status,
            string[] Tags,
            string[] Up,
            string HeaderBreadcrumb,
            string Body)
        {
            this.Path = Path;
            this.Title = Title;
            this.Type = Type;
            this.Usecase = Usecase;
            this.Status = Status;
            this.Tags = Tags;
            this.Up = Up;
            this.HeaderBreadcrumb = HeaderBreadcrumb;
            this.Body = Body;
        }

        public string Path { get; }
        public string Title { get; }
        public string Type { get; }
        public string Usecase { get; }
        public string Status { get; }
        public string[] Tags { get; }
        public string[] Up { get; }
        public string HeaderBreadcrumb { get; set; }
        public string Body { get; set; }
        public double LexicalScore { get; set; }
        public double VectorScore { get; set; }
        public double Score { get; set; }
        public bool IsDirectHit { get; set; }
    }
}