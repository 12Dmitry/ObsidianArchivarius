
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using SecondBrain.Core.Model;
using SecondBrain.Core.Ports;

namespace SecondBrain.Infrastructure.Graph;

public sealed class SqliteMapGraph : IMapGraph
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly IVaultStore _vaultStore;
    private readonly string _connectionString;

    public SqliteMapGraph(string dbPath, IVaultStore vaultStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dbPath);
        ArgumentNullException.ThrowIfNull(vaultStore);

        string fullDbPath = Path.GetFullPath(dbPath);
        _vaultStore = vaultStore;
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullDbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
    }

    public async Task<IReadOnlyList<GraphEdge>> ExpandAsync(
        IReadOnlyList<string> paths,
        int hops = 1,
        int limit = 4,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (paths.Count == 0 || hops <= 0 || limit <= 0)
        {
            return Array.Empty<GraphEdge>();
        }

        List<string> normalizedSeeds = paths
            .Where(static p => !string.IsNullOrWhiteSpace(p))
            .Select(NormalizePath)
            .Where(static p => p.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (normalizedSeeds.Count == 0)
        {
            return Array.Empty<GraphEdge>();
        }

        var seedSet = new HashSet<string>(normalizedSeeds, StringComparer.Ordinal);
        var visited = new HashSet<string>(normalizedSeeds, StringComparer.Ordinal);
        var frontier = new HashSet<string>(normalizedSeeds, StringComparer.Ordinal);
        var collected = new List<GraphEdge>();
        var seenEdges = new HashSet<(string Src, string Dst, string Kind)>();

        await using SqliteConnection connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        for (int hop = 0; hop < hops && frontier.Count > 0; hop++)
        {
            ct.ThrowIfCancellationRequested();

            List<string> frontierList = frontier.ToList();
            var nextFrontier = new HashSet<string>(StringComparer.Ordinal);

            await using SqliteCommand cmd = connection.CreateCommand();
            var placeholders = new string[frontierList.Count];
            for (int i = 0; i < frontierList.Count; i++)
            {
                string paramName = $"@f{i}";
                placeholders[i] = paramName;
                cmd.Parameters.AddWithValue(paramName, frontierList[i]);
            }

            string inClause = string.Join(", ", placeholders);
            cmd.CommandText = $"""
                SELECT src_path, dst_path, kind, weight, origin
                FROM edges
                WHERE src_path IN ({inClause}) OR dst_path IN ({inClause});
                """;

            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string src = reader.GetString(0);
                string dst = reader.GetString(1);
                string kind = reader.GetString(2);
                double weight = reader.GetDouble(3);
                string origin = reader.GetString(4);

                bool srcInFrontier = frontier.Contains(src);
                bool dstInFrontier = frontier.Contains(dst);

                if (srcInFrontier && !seedSet.Contains(dst))
                {
                    if (seenEdges.Add((src, dst, kind)))
                    {
                        collected.Add(new GraphEdge(src, dst, kind, weight, origin));
                    }

                    if (!visited.Contains(dst))
                    {
                        nextFrontier.Add(dst);
                    }
                }
                else if (dstInFrontier && !seedSet.Contains(src))
                {
                    if (seenEdges.Add((src, dst, kind)))
                    {
                        collected.Add(new GraphEdge(src, dst, kind, weight, origin));
                    }

                    if (!visited.Contains(src))
                    {
                        nextFrontier.Add(src);
                    }
                }
            }

            foreach (string node in nextFrontier)
            {
                visited.Add(node);
            }

            frontier = nextFrontier;
        }

        return collected
            .OrderByDescending(static e => e.Weight)
            .ThenBy(static e => e.Src, StringComparer.Ordinal)
            .ThenBy(static e => e.Dst, StringComparer.Ordinal)
            .ThenBy(static e => e.Kind, StringComparer.Ordinal)
            .Take(limit)
            .ToArray();
    }

    public async Task<IReadOnlyList<MapCandidate>> ProposeMembershipAsync(
        string notePath,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(notePath);

        string normalizedNotePath = NormalizePath(notePath);
        await using SqliteConnection connection = await OpenConnectionAsync(ct).ConfigureAwait(false);

        (string[] noteTags, string[] noteUp) = await LoadNoteTagsAndUpAsync(
            connection,
            normalizedNotePath,
            ct).ConfigureAwait(false);

        var explicitUpTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string up in noteUp)
        {
            string normUp = NormalizePath(up);
            if (normUp.Length > 0)
            {
                explicitUpTargets.Add(normUp);
                explicitUpTargets.Add(StripMdExtension(normUp));
            }
        }

        var normalizedNoteTags = noteTags
            .Select(NormalizeTag)
            .Where(static t => t.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var candidates = new List<MapCandidate>();

        await using SqliteCommand mapsCmd = connection.CreateCommand();
        mapsCmd.CommandText = """
            SELECT path, title, tags_json
            FROM notes
            WHERE LOWER(type) = 'map';
            """;

        await using SqliteDataReader reader = await mapsCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string mapPath = NormalizePath(reader.GetString(0));
            if (string.Equals(mapPath, normalizedNotePath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string mapTitle = reader.GetString(1);
            string[] mapTags = DeserializeStringArray(reader.GetString(2));

            if (explicitUpTargets.Contains(mapPath) ||
                explicitUpTargets.Contains(StripMdExtension(mapPath)))
            {
                candidates.Add(new MapCandidate(mapPath, mapTitle, 1.0, "explicit_up"));
                continue;
            }

            if (normalizedNoteTags.Count > 0 && mapTags.Length > 0)
            {
                int overlapCount = mapTags
                    .Select(NormalizeTag)
                    .Where(static t => t.Length > 0)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(normalizedNoteTags.Contains);

                if (overlapCount > 0)
                {
                    double score = 0.5 + (0.1 * overlapCount);
                    candidates.Add(new MapCandidate(mapPath, mapTitle, score, "tag_rule"));
                }
            }
        }

        return candidates
            .OrderByDescending(static c => c.Score)
            .ThenBy(static c => c.MapPath, StringComparer.Ordinal)
            .ToArray();
    }

    public async Task<WritePlan> PlanMapSyncAsync(string mapPath, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(mapPath);

        string normalizedMapPath = NormalizePath(mapPath);
        string strippedMapPath = StripMdExtension(normalizedMapPath);

        NoteDocument? mapDoc = await _vaultStore.ReadAsync(normalizedMapPath, ct).ConfigureAwait(false);

        await using SqliteConnection connection = await OpenConnectionAsync(ct).ConfigureAwait(false);
        var linkedNotes = new Dictionary<string, string>(StringComparer.Ordinal);

        await using (SqliteCommand notesCmd = connection.CreateCommand())
        {
            notesCmd.CommandText = """
                SELECT path, title, up_json
                FROM notes;
                """;

            await using SqliteDataReader reader = await notesCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                string path = NormalizePath(reader.GetString(0));
                if (string.Equals(path, normalizedMapPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string title = reader.GetString(1);
                string[] ups = DeserializeStringArray(reader.GetString(2));

                bool referencesMap = ups.Any(u =>
                {
                    string normUp = NormalizePath(u);
                    return string.Equals(normUp, normalizedMapPath, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(normUp, strippedMapPath, StringComparison.OrdinalIgnoreCase);
                });

                if (referencesMap)
                {
                    linkedNotes[path] = title;
                }
            }
        }

        await using (SqliteCommand edgesCmd = connection.CreateCommand())
        {
            edgesCmd.CommandText = """
                SELECT e.src_path, COALESCE(n.title, e.src_path)
                FROM edges e
                LEFT JOIN notes n ON n.path = e.src_path
                WHERE e.kind = 'up' AND (e.dst_path = @mapPath OR e.dst_path = @strippedMapPath);
                """;
            edgesCmd.Parameters.AddWithValue("@mapPath", normalizedMapPath);
            edgesCmd.Parameters.AddWithValue("@strippedMapPath", strippedMapPath);

            await using SqliteDataReader edgeReader = await edgesCmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await edgeReader.ReadAsync(ct).ConfigureAwait(false))
            {
                string srcPath = NormalizePath(edgeReader.GetString(0));
                if (string.Equals(srcPath, normalizedMapPath, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!linkedNotes.ContainsKey(srcPath))
                {
                    linkedNotes[srcPath] = edgeReader.GetString(1);
                }
            }
        }

        List<KeyValuePair<string, string>> orderedLinks = linkedNotes
            .OrderBy(static kvp => kvp.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static kvp => kvp.Key, StringComparer.Ordinal)
            .ToList();

        var sb = new StringBuilder();
        sb.Append("<!-- brain:links:start -->\n");
        foreach (KeyValuePair<string, string> entry in orderedLinks)
        {
            string display = string.IsNullOrWhiteSpace(entry.Value) ? entry.Key : entry.Value;
            sb.Append("- [[").Append(entry.Key).Append('|').Append(display).Append("]]\n");
        }
        sb.Append("<!-- brain:links:end -->");

        var operation = new WriteOperation(
            Kind: "replace_managed_block",
            Path: normalizedMapPath,
            ExpectedHash: mapDoc?.ContentHash,
            Payload: sb.ToString(),
            Reason: "Sync brain:links managed block from notes referencing map via up");

        string[] warnings = mapDoc is null
            ? new[] { $"Map note '{normalizedMapPath}' was not found in vault store." }
            : Array.Empty<string>();

        return new WritePlan(
            RunId: Guid.NewGuid().ToString("N"),
            Operations: new[] { operation },
            Warnings: warnings);
    }

    private async Task<(string[] Tags, string[] Up)> LoadNoteTagsAndUpAsync(
        SqliteConnection connection,
        string normalizedNotePath,
        CancellationToken ct)
    {
        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            SELECT tags_json, up_json
            FROM notes
            WHERE path = @path;
            """;
        cmd.Parameters.AddWithValue("@path", normalizedNotePath);

        await using SqliteDataReader reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        if (await reader.ReadAsync(ct).ConfigureAwait(false))
        {
            string[] tags = DeserializeStringArray(reader.GetString(0));
            string[] up = DeserializeStringArray(reader.GetString(1));
            return (tags, up);
        }

        NoteDocument? doc = await _vaultStore.ReadAsync(normalizedNotePath, ct).ConfigureAwait(false);
        if (doc is not null)
        {
            return (doc.Metadata.Tags.ToArray(), doc.Metadata.Up.ToArray());
        }

        return (Array.Empty<string>(), Array.Empty<string>());
    }

    private async Task<SqliteConnection> OpenConnectionAsync(CancellationToken ct)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await using SqliteCommand cmd = connection.CreateCommand();
        cmd.CommandText = """
            PRAGMA busy_timeout = 5000;
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;
            PRAGMA temp_store = MEMORY;
            PRAGMA foreign_keys = ON;
            """;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);

        return connection;
    }

    private static string[] DeserializeStringArray(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Array.Empty<string>();
        }

        return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? Array.Empty<string>();
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

    private static string StripMdExtension(string path)
    {
        return path.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
            ? path[..^3]
            : path;
    }

    private static string NormalizeTag(string tag)
    {
        return tag.Trim().TrimStart('#');
    }
}