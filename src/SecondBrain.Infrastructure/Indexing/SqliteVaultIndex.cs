using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Linking;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;
using SecondBrain.Core.Recipes;

namespace SecondBrain.Infrastructure.Indexing;

public sealed class SqliteVaultIndex : IVaultIndex, IDisposable
{
    private const string BuiltAtMetaKey = "built_at";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IVaultStore _vaultStore;
    private readonly TimeProvider _timeProvider;
    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private volatile bool _initialized;
    private bool _disposed;

    public SqliteVaultIndex(
        string vaultRoot,
        IVaultStore vaultStore,
        TimeProvider? timeProvider = null,
        string? dbPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(vaultRoot);
        ArgumentNullException.ThrowIfNull(vaultStore);

        var fullVaultRoot = Path.GetFullPath(vaultRoot);
        _vaultStore = vaultStore;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dbPath = string.IsNullOrWhiteSpace(dbPath)
            ? Path.Combine(fullVaultRoot, ".brain", "index.db")
            : Path.GetFullPath(dbPath);

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

            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
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
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            var nowIso = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            await UpsertNoteCoreAsync(connection, transaction, doc, nowIso, ct).ConfigureAwait(false);

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
            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await DeleteNoteCoreAsync(connection, transaction, normalizedPath, ct).ConfigureAwait(false);

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

            await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
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
                    await DeleteNoteCoreAsync(connection, transaction, existingPath, ct).ConfigureAwait(false);
                }
            }

            foreach (var doc in docs)
            {
                ct.ThrowIfCancellationRequested();
                await UpsertNoteCoreAsync(connection, transaction, doc, builtAtIso, ct).ConfigureAwait(false);
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

            var chunkCount = await CountChunksAsync(connection, transaction, ct).ConfigureAwait(false);

            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return new IndexStats(
                Chunks: chunkCount,
                Vectors: 0,
                BuiltAt: builtAt,
                EmbeddingModel: "none",
                Dimensions: 0);
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

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var chunkCount = await CountChunksAsync(connection, transaction: null, ct).ConfigureAwait(false);

        var builtAt = DateTimeOffset.MinValue;
        await using (var metaCmd = connection.CreateCommand())
        {
            metaCmd.CommandText = "SELECT value FROM index_meta WHERE key = @key;";
            metaCmd.Parameters.AddWithValue("@key", BuiltAtMetaKey);
            var scalar = await metaCmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
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
            Vectors: 0,
            BuiltAt: builtAt,
            EmbeddingModel: "none",
            Dimensions: 0);
    }

    public async Task<SearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(query);

        var totalSw = Stopwatch.StartNew();
        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        var lexicalSw = Stopwatch.StartNew();

        var limit = query.Limit > 0 ? query.Limit : 8;
        var tokens = RuNormalizer.TokenizeNormalized(query.Text)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var longTokens = tokens.Where(static t => t.Length >= 3).ToList();

        var queryTags = query.Tags is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : query.Tags
                .Where(static t => !string.IsNullOrWhiteSpace(t))
                .Select(static t => t.Trim().TrimStart('#'))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var hasSoftFilters = query.Type.HasValue ||
                             queryTags.Count > 0 ||
                             !string.IsNullOrWhiteSpace(query.PathPrefix);

        if (tokens.Count == 0 && !hasSoftFilters)
        {
            lexicalSw.Stop();
            totalSw.Stop();
            return new SearchResult(
                Hits: [],
                Timing: new SearchTiming(
                    LexicalMs: lexicalSw.Elapsed.TotalMilliseconds,
                    VectorMs: 0,
                    TotalMs: totalSw.Elapsed.TotalMilliseconds));
        }

        var normalizedPrefix = string.IsNullOrWhiteSpace(query.PathPrefix)
            ? null
            : NormalizePath(query.PathPrefix);

        await using var connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        connection.CreateFunction<string?, string>("ru_norm", RuNormalizer.NormalizeForSearch, isDeterministic: true);

        var candidates = new List<RawCandidate>();
        var candidateLimit = Math.Max(limit * 20, 500);

        if (longTokens.Count > 0)
        {
            var ftsExpr = string.Join(
                " OR ",
                longTokens.Select(static t => $"\"{t.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));

            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = """
                    SELECT
                        c.path,
                        n.title,
                        n.type,
                        n.tags_json,
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

                await ReadCandidatesAsync(cmd, candidates, ct).ConfigureAwait(false);
            }

            // Include notes whose title matches a query token even if the body did not match FTS5.
            var matchedPaths = candidates.Select(static c => c.Path).ToHashSet(StringComparer.Ordinal);
            await using var titleCmd = connection.CreateCommand();
            var titleClauses = new StringBuilder();
            for (var i = 0; i < longTokens.Count; i++)
            {
                if (i > 0)
                {
                    titleClauses.Append(" OR ");
                }

                var paramName = $"@ttok{i}";
                titleClauses.Append($"ru_norm(n.title) LIKE '%' || {paramName} || '%'");
                titleCmd.Parameters.AddWithValue(paramName, longTokens[i]);
            }

            titleCmd.CommandText = $"""
                SELECT
                    n.path,
                    n.title,
                    n.type,
                    n.tags_json,
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
            foreach (var tc in titleCandidates)
            {
                if (!matchedPaths.Contains(tc.Path))
                {
                    candidates.Add(tc);
                }
            }
        }
        else if (tokens.Count > 0)
        {
            // All normalized tokens are shorter than 3 chars: trigram MATCH always yields 0 hits.
            await using var cmd = connection.CreateCommand();
            var clauses = new StringBuilder();
            for (var i = 0; i < tokens.Count; i++)
            {
                if (i > 0)
                {
                    clauses.Append(" OR ");
                }

                var paramName = $"@tok{i}";
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
                    n.tags_json,
                    c.header_breadcrumb,
                    c.body,
                    1.0 AS base_score
                FROM chunks c
                INNER JOIN notes n ON n.path = c.path
                WHERE ({clauses})
                LIMIT @candidateLimit;
                """;
            cmd.Parameters.AddWithValue("@candidateLimit", candidateLimit);

            await ReadCandidatesAsync(cmd, candidates, ct).ConfigureAwait(false);
        }
        else
        {
            // Empty query text with soft filters: select candidates without MATCH and rank by filter boosts.
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                SELECT
                    c.path,
                    n.title,
                    n.type,
                    n.tags_json,
                    c.header_breadcrumb,
                    c.body,
                    1.0 AS base_score
                FROM chunks c
                INNER JOIN notes n ON n.path = c.path;
                """;

            await ReadCandidatesAsync(cmd, candidates, ct).ConfigureAwait(false);
        }

        var bestByPath = new Dictionary<string, ScoredCandidate>(StringComparer.Ordinal);
        var expectedTypeName = query.Type?.ToString();

        foreach (var candidate in candidates)
        {
            ct.ThrowIfCancellationRequested();

            var noteTags = DeserializeTags(candidate.TagsJson);
            var score = candidate.BaseScore;

            if (expectedTypeName is not null &&
                string.Equals(candidate.Type, expectedTypeName, StringComparison.OrdinalIgnoreCase))
            {
                score += 0.25;
            }

            if (normalizedPrefix is not null &&
                candidate.Path.StartsWith(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                score += 0.20;
            }

            if (queryTags.Count > 0 && noteTags.Length > 0)
            {
                var matchedTagCount = 0;
                foreach (var noteTag in noteTags)
                {
                    if (queryTags.Contains(noteTag.Trim().TrimStart('#')))
                    {
                        matchedTagCount++;
                    }
                }

                score += Math.Min(0.45, matchedTagCount * 0.15);
            }

            if (tokens.Count > 0 && HasTitleMatch(candidate.Title, tokens))
            {
                score += 0.30;
            }

            if (!bestByPath.TryGetValue(candidate.Path, out var existing) || score > existing.Score)
            {
                bestByPath[candidate.Path] = new ScoredCandidate(
                    Path: candidate.Path,
                    Title: candidate.Title,
                    HeaderBreadcrumb: candidate.HeaderBreadcrumb,
                    Body: candidate.Body,
                    Tags: noteTags,
                    Score: score);
            }
        }

        var topCandidates = bestByPath.Values
            .OrderByDescending(static x => x.Score)
            .ThenBy(static x => x.Path, StringComparer.Ordinal)
            .Take(limit)
            .ToList();

        var hits = new List<SearchHit>(topCandidates.Count);
        foreach (var item in topCandidates)
        {
            var snippet = BuildSnippet(item.Body, tokens, query.Text);
            var header = string.IsNullOrWhiteSpace(item.HeaderBreadcrumb) ? null : item.HeaderBreadcrumb;

            hits.Add(new SearchHit(
                Path: item.Path,
                Title: item.Title,
                Snippet: snippet,
                Score: item.Score,
                Header: header,
                Tags: item.Tags));
        }

        lexicalSw.Stop();
        totalSw.Stop();

        return new SearchResult(
            Hits: hits,
            Timing: new SearchTiming(
                LexicalMs: lexicalSw.Elapsed.TotalMilliseconds,
                VectorMs: 0,
                TotalMs: totalSw.Elapsed.TotalMilliseconds));
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

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
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

        return connection;
    }

    private static async Task UpsertNoteCoreAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        NoteDocument doc,
        string nowIso,
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
        CancellationToken ct)
    {
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

    private static async Task<int> CountChunksAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken ct)
    {
        await using var cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = "SELECT COUNT(*) FROM chunks;";
        var scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
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
                TagsJson: reader.GetString(3),
                HeaderBreadcrumb: reader.GetString(4),
                Body: reader.GetString(5),
                BaseScore: reader.IsDBNull(6) ? 0.0 : reader.GetDouble(6)));
        }
    }

    private static string[] DeserializeTags(string tagsJson)
    {
        if (string.IsNullOrWhiteSpace(tagsJson))
        {
            return [];
        }

        return JsonSerializer.Deserialize<string[]>(tagsJson, JsonOptions) ?? [];
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
        string TagsJson,
        string HeaderBreadcrumb,
        string Body,
        double BaseScore);

    private sealed record ScoredCandidate(
        string Path,
        string Title,
        string HeaderBreadcrumb,
        string Body,
        string[] Tags,
        double Score);
}