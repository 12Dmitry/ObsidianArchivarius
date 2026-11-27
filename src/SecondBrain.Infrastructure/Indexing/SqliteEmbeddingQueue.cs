using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Ports;

namespace SecondBrain.Infrastructure.Indexing;

public sealed class SqliteEmbeddingQueue : IEmbeddingQueue, IDisposable
{
    private const int MaxAttempts = 3;

    private readonly string _dbPath;
    private readonly string _connectionString;
    private readonly IEmbeddingClient _embeddingClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    private volatile bool _initialized;
    private volatile bool _vec0Available;
    private bool _disposed;

    public SqliteEmbeddingQueue(
        string dbPath,
        IEmbeddingClient embeddingClient,
        TimeProvider? timeProvider = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(embeddingClient);

        _dbPath = Path.GetFullPath(dbPath);
        _embeddingClient = embeddingClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    public async Task EnqueueAsync(IEnumerable<string> chunkIds, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(chunkIds);

        List<string> ids = chunkIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (ids.Count == 0)
        {
            return;
        }

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string nowIso = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

            await using SqliteConnection connection = await OpenConnectionAsync(loadVec0: false, ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                INSERT INTO embedding_queue (chunk_id, status, attempts, last_error, updated_at)
                VALUES (@chunk_id, 'pending', 0, NULL, @updated_at)
                ON CONFLICT(chunk_id) DO UPDATE SET
                    status = 'pending',
                    attempts = 0,
                    last_error = NULL,
                    updated_at = excluded.updated_at;
                """;

            SqliteParameter pChunkId = cmd.Parameters.Add("@chunk_id", SqliteType.Text);
            SqliteParameter pUpdatedAt = cmd.Parameters.Add("@updated_at", SqliteType.Text);
            pUpdatedAt.Value = nowIso;

            foreach (string id in ids)
            {
                ct.ThrowIfCancellationRequested();
                pChunkId.Value = id;
                await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            }

            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<EmbedProgress> DrainAsync(
        int batchSize,
        int maxDegreeOfParallelism,
        CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(batchSize, 0);

        await EnsureInitializedAsync(ct).ConfigureAwait(false);

        int parallelism = Math.Max(1, maxDegreeOfParallelism);
        var sw = Stopwatch.StartNew();
        int totalDone = 0;
        int totalFailed = 0;
        var attemptedIds = new HashSet<string>(StringComparer.Ordinal);

        while (true)
        {
            ct.ThrowIfCancellationRequested();

            int fetchLimit = batchSize * parallelism;
            List<QueueItem> candidates = await FetchEligibleItemsAsync(fetchLimit, attemptedIds, ct)
                .ConfigureAwait(false);

            if (candidates.Count == 0)
            {
                break;
            }

            foreach (QueueItem item in candidates)
            {
                attemptedIds.Add(item.ChunkId);
            }

            if (parallelism <= 1 || candidates.Count <= batchSize)
            {
                for (int offset = 0; offset < candidates.Count; offset += batchSize)
                {
                    int count = Math.Min(batchSize, candidates.Count - offset);
                    List<QueueItem> batch = candidates.GetRange(offset, count);
                    (int done, int failed) = await ProcessBatchAsync(batch, ct).ConfigureAwait(false);
                    totalDone += done;
                    totalFailed += failed;
                }
            }
            else
            {
                var batches = new List<List<QueueItem>>();
                for (int offset = 0; offset < candidates.Count; offset += batchSize)
                {
                    int count = Math.Min(batchSize, candidates.Count - offset);
                    batches.Add(candidates.GetRange(offset, count));
                }

                using var gate = new SemaphoreSlim(parallelism, parallelism);
                Task<(int Done, int Failed)>[] tasks = batches
                    .Select(async batch =>
                    {
                        await gate.WaitAsync(ct).ConfigureAwait(false);
                        try
                        {
                            return await ProcessBatchAsync(batch, ct).ConfigureAwait(false);
                        }
                        finally
                        {
                            gate.Release();
                        }
                    })
                    .ToArray();

                (int Done, int Failed)[] results = await Task.WhenAll(tasks).ConfigureAwait(false);
                foreach ((int done, int failed) in results)
                {
                    totalDone += done;
                    totalFailed += failed;
                }
            }
        }

        int remaining = await CountRemainingAsync(ct).ConfigureAwait(false);
        sw.Stop();

        double elapsedSeconds = sw.Elapsed.TotalSeconds;
        double chunksPerSecond = elapsedSeconds > 0.0 ? totalDone / elapsedSeconds : 0.0;

        return new EmbedProgress(
            Done: totalDone,
            Failed: totalFailed,
            Remaining: remaining,
            ChunksPerSecond: chunksPerSecond);
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

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
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

            string? directory = Path.GetDirectoryName(_dbPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using SqliteConnection connection = await OpenConnectionAsync(loadVec0: false, ct).ConfigureAwait(false);
            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS chunk_vectors (
                    chunk_id TEXT PRIMARY KEY,
                    embedding BLOB NOT NULL,
                    model TEXT NOT NULL,
                    updated_at TEXT NOT NULL,
                    FOREIGN KEY(chunk_id) REFERENCES chunks(chunk_id) ON DELETE CASCADE
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
                """;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

            _vec0Available = await TryInitializeVec0TableAsync(connection, ct).ConfigureAwait(false);
            _initialized = true;
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<bool> TryInitializeVec0TableAsync(SqliteConnection connection, CancellationToken ct)
    {
        try
        {
            connection.EnableExtensions(true);
            connection.LoadExtension("vec0", "sqlite3_vec_init");

            int dims = _embeddingClient.Dimensions > 0 ? _embeddingClient.Dimensions : 1024;
            await using SqliteCommand vecCmd = connection.CreateCommand();
            vecCmd.CommandText = $"""
                CREATE VIRTUAL TABLE IF NOT EXISTS vec_chunks USING vec0(
                    chunk_id TEXT PRIMARY KEY,
                    embedding float[{dims}] distance_metric=cosine
                );
                """;
            await vecCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<List<QueueItem>> FetchEligibleItemsAsync(
        int limit,
        HashSet<string> excludedIds,
        CancellationToken ct)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(loadVec0: false, ct).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT eq.chunk_id, c.embed_text
            FROM embedding_queue eq
            INNER JOIN chunks c ON c.chunk_id = eq.chunk_id
            WHERE eq.status = 'pending' OR (eq.status = 'failed' AND eq.attempts < @maxAttempts)
            ORDER BY eq.updated_at ASC, eq.chunk_id ASC;
            """;
        cmd.Parameters.AddWithValue("@maxAttempts", MaxAttempts);

        var items = new List<QueueItem>(limit);
        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string chunkId = reader.GetString(0);
            if (excludedIds.Contains(chunkId))
            {
                continue;
            }

            items.Add(new QueueItem(chunkId, reader.GetString(1)));
            if (items.Count >= limit)
            {
                break;
            }
        }

        return items;
    }

    private async Task<(int Done, int Failed)> ProcessBatchAsync(List<QueueItem> batch, CancellationToken ct)
    {
        try
        {
            string[] texts = batch.Select(static x => x.EmbedText).ToArray();
            IReadOnlyList<float[]> vectors = await _embeddingClient.EmbedDocumentsAsync(texts, ct).ConfigureAwait(false);

            if (vectors is null || vectors.Count != batch.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding client returned {vectors?.Count ?? 0} vectors for batch of size {batch.Count}.");
            }

            await PersistSuccessBatchAsync(batch, vectors, ct).ConfigureAwait(false);
            return (batch.Count, 0);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            int done = 0;
            int failed = 0;

            foreach (QueueItem item in batch)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    IReadOnlyList<float[]> singleResult = await _embeddingClient
                        .EmbedDocumentsAsync(new[] { item.EmbedText }, ct)
                        .ConfigureAwait(false);

                    if (singleResult is null || singleResult.Count != 1)
                    {
                        throw new InvalidOperationException(
                            $"Embedding client returned {singleResult?.Count ?? 0} vectors for single chunk '{item.ChunkId}'.");
                    }

                    await PersistSuccessBatchAsync(new[] { item }, singleResult, ct).ConfigureAwait(false);
                    done++;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    await MarkFailedAsync(item.ChunkId, ex.Message, ct).ConfigureAwait(false);
                    failed++;
                }
            }

            return (done, failed);
        }
    }

    private async Task PersistSuccessBatchAsync(
        IReadOnlyList<QueueItem> items,
        IReadOnlyList<float[]> vectors,
        CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string nowIso = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);
            string modelId = string.IsNullOrWhiteSpace(_embeddingClient.ModelId)
                ? "unknown"
                : _embeddingClient.ModelId;
            int dimensions = _embeddingClient.Dimensions > 0
                ? _embeddingClient.Dimensions
                : (vectors.Count > 0 ? vectors[0].Length : 1024);

            await using SqliteConnection connection = await OpenConnectionAsync(_vec0Available, ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using SqliteCommand vecInsertCmd = connection.CreateCommand();
            vecInsertCmd.Transaction = transaction;
            vecInsertCmd.CommandText = """
                INSERT OR REPLACE INTO chunk_vectors (chunk_id, embedding, model, updated_at)
                VALUES (@chunk_id, @embedding, @model, @updated_at);
                """;
            SqliteParameter pVecChunkId = vecInsertCmd.Parameters.Add("@chunk_id", SqliteType.Text);
            SqliteParameter pVecBlob = vecInsertCmd.Parameters.Add("@embedding", SqliteType.Blob);
            SqliteParameter pVecModel = vecInsertCmd.Parameters.Add("@model", SqliteType.Text);
            SqliteParameter pVecUpdated = vecInsertCmd.Parameters.Add("@updated_at", SqliteType.Text);
            pVecModel.Value = modelId;
            pVecUpdated.Value = nowIso;

            await using SqliteCommand queueUpdateCmd = connection.CreateCommand();
            queueUpdateCmd.Transaction = transaction;
            queueUpdateCmd.CommandText = """
                UPDATE embedding_queue
                SET status = 'done',
                    last_error = NULL,
                    updated_at = @updated_at
                WHERE chunk_id = @chunk_id;
                """;
            SqliteParameter pQueueChunkId = queueUpdateCmd.Parameters.Add("@chunk_id", SqliteType.Text);
            SqliteParameter pQueueUpdated = queueUpdateCmd.Parameters.Add("@updated_at", SqliteType.Text);
            pQueueUpdated.Value = nowIso;

            SqliteCommand? vec0InsertCmd = null;
            SqliteParameter? pVec0ChunkId = null;
            SqliteParameter? pVec0Blob = null;

            if (_vec0Available)
            {
                vec0InsertCmd = connection.CreateCommand();
                vec0InsertCmd.Transaction = transaction;
                vec0InsertCmd.CommandText = """
                    INSERT OR REPLACE INTO vec_chunks (chunk_id, embedding)
                    VALUES (@chunk_id, @embedding);
                    """;
                pVec0ChunkId = vec0InsertCmd.Parameters.Add("@chunk_id", SqliteType.Text);
                pVec0Blob = vec0InsertCmd.Parameters.Add("@embedding", SqliteType.Blob);
            }

            try
            {
                for (int i = 0; i < items.Count; i++)
                {
                    string chunkId = items[i].ChunkId;
                    byte[] blob = SimdCosine.ToBytes(vectors[i]);

                    pVecChunkId.Value = chunkId;
                    pVecBlob.Value = blob;
                    await vecInsertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                    pQueueChunkId.Value = chunkId;
                    await queueUpdateCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

                    if (vec0InsertCmd is not null && pVec0ChunkId is not null && pVec0Blob is not null)
                    {
                        try
                        {
                            pVec0ChunkId.Value = chunkId;
                            pVec0Blob.Value = blob;
                            await vec0InsertCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                        }
                        catch (SqliteException)
                        {
                            // Fallback cleanly to chunk_vectors when vec_chunks schema or dimension differs.
                        }
                    }
                }

                await UpsertMetaAsync(connection, transaction, "embedding_model", modelId, ct).ConfigureAwait(false);
                await UpsertMetaAsync(
                    connection,
                    transaction,
                    "dimensions",
                    dimensions.ToString(CultureInfo.InvariantCulture),
                    ct).ConfigureAwait(false);

                await transaction.CommitAsync(ct).ConfigureAwait(false);
            }
            finally
            {
                if (vec0InsertCmd is not null)
                {
                    await vec0InsertCmd.DisposeAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task MarkFailedAsync(string chunkId, string errorMessage, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            string nowIso = _timeProvider.GetUtcNow().ToString("O", CultureInfo.InvariantCulture);

            await using SqliteConnection connection = await OpenConnectionAsync(loadVec0: false, ct).ConfigureAwait(false);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(ct).ConfigureAwait(false);

            await using SqliteCommand cmd = connection.CreateCommand();
            cmd.Transaction = transaction;
            cmd.CommandText = """
                UPDATE embedding_queue
                SET status = 'failed',
                    attempts = attempts + 1,
                    last_error = @last_error,
                    updated_at = @updated_at
                WHERE chunk_id = @chunk_id;
                """;
            cmd.Parameters.AddWithValue("@chunk_id", chunkId);
            cmd.Parameters.AddWithValue("@last_error", errorMessage);
            cmd.Parameters.AddWithValue("@updated_at", nowIso);

            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task<int> CountRemainingAsync(CancellationToken ct)
    {
        await using SqliteConnection connection = await OpenConnectionAsync(loadVec0: false, ct).ConfigureAwait(false);
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
                          SELECT COUNT(*)
                          FROM embedding_queue eq
                          INNER JOIN chunks c ON c.chunk_id = eq.chunk_id
                          WHERE eq.status = 'pending' OR (eq.status = 'failed' AND eq.attempts < @maxAttempts);
                          """;
        cmd.Parameters.AddWithValue("@maxAttempts", MaxAttempts);

        object? scalar = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
        return scalar is null or DBNull ? 0 : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
    }

    private static async Task UpsertMetaAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string key,
        string value,
        CancellationToken ct)
    {
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.Transaction = transaction;
        cmd.CommandText = """
            INSERT OR REPLACE INTO index_meta (key, value)
            VALUES (@key, @value);
            """;
        cmd.Parameters.AddWithValue("@key", key);
        cmd.Parameters.AddWithValue("@value", value);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private async Task<SqliteConnection> OpenConnectionAsync(bool loadVec0, CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using (SqliteCommand pragmaCmd = connection.CreateCommand())
        {
            pragmaCmd.CommandText = """
                PRAGMA busy_timeout = 5000;
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                PRAGMA temp_store = MEMORY;
                PRAGMA foreign_keys = ON;
                """;
            await pragmaCmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        if (loadVec0)
        {
            try
            {
                connection.EnableExtensions(true);
                connection.LoadExtension("vec0", "sqlite3_vec_init");
            }
            catch (Exception)
            {
                // Keep operating via chunk_vectors if vec0 cannot be loaded.
            }
        }

        return connection;
    }

    private sealed record QueueItem(string ChunkId, string EmbedText);
}