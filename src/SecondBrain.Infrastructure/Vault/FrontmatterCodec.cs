using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using SecondBrain.Core.Linking;
using SecondBrain.Core.Model;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;
using YamlDotNet.Serialization;

namespace SecondBrain.Infrastructure.Vault;

public static class FrontmatterCodec
{
    private static readonly ISerializer YamlSerializer = new SerializerBuilder().Build();

    private static readonly HashSet<string> KnownKeys = new(StringComparer.Ordinal)
    {
        "id", "title", "type", "usecase", "status",
        "tags", "tags_proposed", "aliases", "related", "up", "relations",
        "summary", "lang", "created", "updated", "schema_version",
        "source", "source_url", "content_hash", "mentions_raw", "links_origin",
        "llm", "privacy", "processed", "generated_notes", "stub",
        "archived_at", "review_date", "reviewed_at", "confidence",
        "slots", "decision_log", "project", "extra"
    };

    public static NoteDocument Parse(string path, string rawContent, DateTimeOffset fileModifiedAt)
    {
        if (!TryParse(path, rawContent, fileModifiedAt, out var document, out var error) || document is null)
        {
            throw new FormatException(error ?? $"Failed to parse frontmatter for '{path}'.");
        }

        return document;
    }

    public static bool TryParse(
        string path,
        string rawContent,
        DateTimeOffset fileModifiedAt,
        out NoteDocument? document,
        out string? error)
    {
        document = null;
        error = null;

        if (!TrySplitFrontmatter(rawContent, out var yamlText, out var body, out _, out error))
        {
            return false;
        }

        YamlMappingNode root;
        try
        {
            using var reader = new StringReader(yamlText);
            var stream = new YamlStream();
            stream.Load(reader);

            if (stream.Documents.Count == 0 || stream.Documents[0].RootNode is not YamlMappingNode mapping)
            {
                error = "Frontmatter must be a non-empty YAML mapping.";
                return false;
            }

            root = mapping;
        }
        catch (YamlException ex)
        {
            error = $"Invalid YAML in frontmatter: {ex.Message}";
            return false;
        }

        if (!TryGetRequiredString(root, "id", out var id, out error) ||
            !TryGetRequiredString(root, "title", out var title, out error))
        {
            return false;
        }

        if (!TryGetRequiredString(root, "type", out var typeRaw, out error) ||
            !TryParseNoteType(typeRaw, out var noteType))
        {
            error ??= $"Invalid or missing required field 'type'.";
            return false;
        }

        var usecase = GetOptionalString(root, "usecase") ?? "note";

        var status = NoteStatus.Active;
        var statusRaw = GetOptionalString(root, "status");
        if (!string.IsNullOrWhiteSpace(statusRaw) && !TryParseNoteStatus(statusRaw, out status))
        {
            error = $"Invalid 'status' value '{statusRaw}'.";
            return false;
        }

        if (!TryGetOptionalDateTimeOffset(root, "created", out var created, out error) ||
            !TryGetOptionalDateTimeOffset(root, "updated", out var updated, out error) ||
            !TryGetOptionalDateTimeOffset(root, "archived_at", out var archivedAt, out error) ||
            !TryGetOptionalDateTimeOffset(root, "review_date", out var reviewDate, out error) ||
            !TryGetOptionalDateTimeOffset(root, "reviewed_at", out var reviewedAt, out error))
        {
            return false;
        }

        var schemaVersion = NoteMetadata.CurrentSchemaVersion;
        if (TryGetScalar(root, "schema_version", out var schemaRaw) && !string.IsNullOrWhiteSpace(schemaRaw))
        {
            if (!int.TryParse(schemaRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out schemaVersion))
            {
                error = $"Invalid 'schema_version' value '{schemaRaw}'.";
                return false;
            }
        }

        if (!TryGetOptionalBool(root, "processed", out var processed, out error) ||
            !TryGetOptionalBool(root, "stub", out var stubOpt, out error) ||
            !TryGetOptionalDouble(root, "confidence", out var confidence, out error))
        {
            return false;
        }

        if (!TryParseRelations(root, out var relations, out error) ||
            !TryParseLlm(root, out var llm, out error) ||
            !TryParsePrivacy(root, out var privacy, out error))
        {
            return false;
        }

        var unknownKeys = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in root.Children)
        {
            if (entry.Key is YamlScalarNode keyScalar &&
                keyScalar.Value is { Length: > 0 } keyName &&
                !KnownKeys.Contains(keyName))
            {
                unknownKeys[keyName] = ConvertYamlNode(entry.Value);
            }
        }

        var metadata = new NoteMetadata
        {
            Id = id,
            Title = title,
            Type = noteType,
            Usecase = usecase,
            Status = status,
            Tags = GetStringList(root, "tags"),
            TagsProposed = GetStringList(root, "tags_proposed"),
            Aliases = GetStringList(root, "aliases"),
            Related = GetStringList(root, "related"),
            Up = GetStringList(root, "up"),
            Relations = relations,
            Summary = GetOptionalString(root, "summary"),
            Lang = GetOptionalString(root, "lang") ?? "ru",
            Created = created ?? default,
            Updated = updated ?? default,
            SchemaVersion = schemaVersion,
            Source = GetOptionalString(root, "source"),
            SourceUrl = GetOptionalString(root, "source_url"),
            ContentHash = GetOptionalString(root, "content_hash"),
            MentionsRaw = GetStringList(root, "mentions_raw"),
            LinksOrigin = GetStringDictionary(root, "links_origin"),
            Llm = llm,
            Privacy = privacy,
            Processed = processed,
            GeneratedNotes = GetStringList(root, "generated_notes"),
            Stub = stubOpt ?? false,
            ArchivedAt = archivedAt,
            ReviewDate = reviewDate,
            ReviewedAt = reviewedAt,
            Confidence = confidence,
            Slots = GetObjectDictionary(root, "slots"),
            DecisionLog = GetObjectDictionary(root, "decision_log"),
            Project = GetObjectDictionary(root, "project"),
            Extra = GetObjectDictionary(root, "extra"),
            UnknownKeys = unknownKeys
        };

        var contentHash = ComputeSha256Hex(rawContent);
        document = new NoteDocument(path, metadata, body, contentHash, fileModifiedAt);
        return true;
    }

    public static bool TrySplitFrontmatter(
        string rawContent,
        out string yaml,
        out string body,
        out string lineEnding,
        out string? error)
    {
        yaml = string.Empty;
        body = string.Empty;
        lineEnding = "\n";
        error = null;

        if (string.IsNullOrEmpty(rawContent))
        {
            error = "Content is empty; missing frontmatter.";
            return false;
        }

        var content = rawContent[0] == '\uFEFF' ? rawContent[1..] : rawContent;

        int yamlStart;
        if (content.StartsWith("---\r\n", StringComparison.Ordinal))
        {
            lineEnding = "\r\n";
            yamlStart = 5;
        }
        else if (content.StartsWith("---\n", StringComparison.Ordinal))
        {
            lineEnding = "\n";
            yamlStart = 4;
        }
        else
        {
            error = "Missing opening frontmatter delimiter '---'.";
            return false;
        }

        var pos = yamlStart;
        while (pos <= content.Length)
        {
            var nextLf = content.IndexOf('\n', pos);
            var lineEnd = nextLf >= 0 ? nextLf : content.Length;
            var lineWithoutCr = lineEnd > pos && content[lineEnd - 1] == '\r'
                ? content[pos..(lineEnd - 1)]
                : content[pos..lineEnd];

            if (lineWithoutCr is "---" or "...")
            {
                yaml = content[yamlStart..pos];
                body = nextLf >= 0 && nextLf + 1 <= content.Length
                    ? content[(nextLf + 1)..]
                    : string.Empty;
                return true;
            }

            if (nextLf < 0)
            {
                break;
            }

            pos = nextLf + 1;
        }

        error = "Missing closing frontmatter delimiter ('---' or '...').";
        return false;
    }

    public static string Serialize(NoteDocument document, string lineEnding = "\n")
        => Serialize(document.Metadata, document.Body, lineEnding);

    public static string Serialize(NoteMetadata metadata, string body, string lineEnding = "\n")
    {
        var normalizedEnding = lineEnding == "\r\n" ? "\r\n" : "\n";
        var ordered = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["id"] = metadata.Id,
            ["title"] = metadata.Title,
            ["type"] = FormatNoteType(metadata.Type),
            ["usecase"] = metadata.Usecase,
            ["status"] = FormatNoteStatus(metadata.Status)
        };

        AddIfNonEmpty(ordered, "tags", metadata.Tags);
        AddIfNonEmpty(ordered, "tags_proposed", metadata.TagsProposed);
        AddIfNonEmpty(ordered, "aliases", metadata.Aliases);
        AddIfNonEmpty(ordered, "related", metadata.Related);
        AddIfNonEmpty(ordered, "up", metadata.Up);

        if (metadata.Relations.Count > 0)
        {
            ordered["relations"] = metadata.Relations.Select(r => new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["src_path"] = r.SrcPath,
                ["dst_path"] = r.DstPath,
                ["kind"] = FormatRelationKind(r.Kind),
                ["weight"] = r.Weight,
                ["origin"] = r.Origin,
                ["reason"] = r.Reason
            }).ToList();
        }

        AddIfNotNull(ordered, "summary", metadata.Summary);
        ordered["lang"] = metadata.Lang;
        ordered["created"] = metadata.Created.ToString("O", CultureInfo.InvariantCulture);
        ordered["updated"] = metadata.Updated.ToString("O", CultureInfo.InvariantCulture);
        ordered["schema_version"] = metadata.SchemaVersion;

        AddIfNotNull(ordered, "source", metadata.Source);
        AddIfNotNull(ordered, "source_url", metadata.SourceUrl);
        AddIfNotNull(ordered, "content_hash", metadata.ContentHash);
        AddIfNonEmpty(ordered, "mentions_raw", metadata.MentionsRaw);

        if (metadata.LinksOrigin.Count > 0)
        {
            ordered["links_origin"] = metadata.LinksOrigin.ToDictionary(kv => kv.Key, kv => (object?)kv.Value, StringComparer.Ordinal);
        }

        if (metadata.Llm is not null)
        {
            ordered["llm"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["model"] = metadata.Llm.Model,
                ["profile"] = metadata.Llm.Profile,
                ["prompt_pack"] = metadata.Llm.PromptPack,
                ["prompt_hash"] = metadata.Llm.PromptHash,
                ["run_id"] = metadata.Llm.RunId,
                ["extracted_at"] = metadata.Llm.ExtractedAt.ToString("O", CultureInfo.InvariantCulture)
            };
        }

        if (metadata.Privacy.CloudAllowed || !string.Equals(metadata.Privacy.Sensitivity, "normal", StringComparison.Ordinal))
        {
            ordered["privacy"] = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["cloud_allowed"] = metadata.Privacy.CloudAllowed,
                ["sensitivity"] = metadata.Privacy.Sensitivity
            };
        }

        if (metadata.Processed.HasValue)
        {
            ordered["processed"] = metadata.Processed.Value;
        }

        AddIfNonEmpty(ordered, "generated_notes", metadata.GeneratedNotes);

        if (metadata.Stub)
        {
            ordered["stub"] = true;
        }

        if (metadata.ArchivedAt.HasValue)
        {
            ordered["archived_at"] = metadata.ArchivedAt.Value.ToString("O", CultureInfo.InvariantCulture);
        }

        if (metadata.ReviewDate.HasValue)
        {
            ordered["review_date"] = metadata.ReviewDate.Value.ToString("O", CultureInfo.InvariantCulture);
        }

        if (metadata.ReviewedAt.HasValue)
        {
            ordered["reviewed_at"] = metadata.ReviewedAt.Value.ToString("O", CultureInfo.InvariantCulture);
        }

        if (metadata.Confidence.HasValue)
        {
            ordered["confidence"] = metadata.Confidence.Value;
        }

        AddIfNonEmpty(ordered, "slots", metadata.Slots);
        AddIfNonEmpty(ordered, "decision_log", metadata.DecisionLog);
        AddIfNonEmpty(ordered, "project", metadata.Project);
        AddIfNonEmpty(ordered, "extra", metadata.Extra);

        foreach (var key in metadata.UnknownKeys.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (!KnownKeys.Contains(key))
            {
                ordered[key] = metadata.UnknownKeys[key];
            }
        }

        var yaml = YamlSerializer.Serialize(ordered)
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .TrimEnd('\n');

        if (normalizedEnding == "\r\n")
        {
            yaml = yaml.Replace("\n", "\r\n", StringComparison.Ordinal);
        }

        return $"---{normalizedEnding}{yaml}{normalizedEnding}---{normalizedEnding}{body}";
    }

    private static string ComputeSha256Hex(string rawContent)
    {
        var bytes = Encoding.UTF8.GetBytes(rawContent);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexStringLower(hash);
    }

    private static bool TryGetRequiredString(YamlMappingNode root, string key, out string value, out string? error)
    {
        value = string.Empty;
        error = null;

        if (!TryGetScalar(root, key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            error = $"Missing or empty required field '{key}'.";
            return false;
        }

        value = raw;
        return true;
    }

    private static string? GetOptionalString(YamlMappingNode root, string key)
        => TryGetScalar(root, key, out var raw) ? raw : null;

    private static bool TryGetScalar(YamlMappingNode root, string key, out string? value)
    {
        value = null;
        if (!TryGetNode(root, key, out var node) || node is not YamlScalarNode scalar)
        {
            return false;
        }

        if (scalar.Style == ScalarStyle.Plain && scalar.Value is null or "~" or "null")
        {
            return false;
        }

        value = scalar.Value;
        return true;
    }

    private static bool TryGetNode(YamlMappingNode root, string key, out YamlNode? value)
    {
        foreach (var entry in root.Children)
        {
            if (entry.Key is YamlScalarNode k && string.Equals(k.Value, key, StringComparison.Ordinal))
            {
                value = entry.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static bool TryGetOptionalDateTimeOffset(
        YamlMappingNode root,
        string key,
        out DateTimeOffset? result,
        out string? error)
    {
        result = null;
        error = null;

        if (!TryGetScalar(root, key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
        {
            error = $"Invalid date format for '{key}': '{raw}'.";
            return false;
        }

        result = parsed;
        return true;
    }

    private static bool TryGetOptionalBool(YamlMappingNode root, string key, out bool? result, out string? error)
    {
        result = null;
        error = null;

        if (!TryGetScalar(root, key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!bool.TryParse(raw, out var parsed))
        {
            error = $"Invalid boolean value for '{key}': '{raw}'.";
            return false;
        }

        result = parsed;
        return true;
    }

    private static bool TryGetOptionalDouble(YamlMappingNode root, string key, out double? result, out string? error)
    {
        result = null;
        error = null;

        if (!TryGetScalar(root, key, out var raw) || string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!double.TryParse(raw, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var parsed))
        {
            error = $"Invalid numeric value for '{key}': '{raw}'.";
            return false;
        }

        result = parsed;
        return true;
    }

    private static IReadOnlyList<string> GetStringList(YamlMappingNode root, string key)
    {
        if (!TryGetNode(root, key, out var node) || node is not YamlSequenceNode seq)
        {
            return [];
        }

        var list = new List<string>(seq.Children.Count);
        foreach (var child in seq.Children)
        {
            if (child is YamlScalarNode scalar && scalar.Value is not null)
            {
                list.Add(scalar.Value);
            }
        }

        return list;
    }

    private static IReadOnlyDictionary<string, string> GetStringDictionary(YamlMappingNode root, string key)
    {
        if (!TryGetNode(root, key, out var node) || node is not YamlMappingNode map)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal);
        }

        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in map.Children)
        {
            if (entry.Key is YamlScalarNode k && k.Value is { Length: > 0 } keyName &&
                entry.Value is YamlScalarNode v && v.Value is not null)
            {
                dict[keyName] = v.Value;
            }
        }

        return dict;
    }

    private static IReadOnlyDictionary<string, object?> GetObjectDictionary(YamlMappingNode root, string key)
    {
        if (!TryGetNode(root, key, out var node) || node is not YamlMappingNode map)
        {
            return new Dictionary<string, object?>(StringComparer.Ordinal);
        }

        return ConvertMapping(map);
    }

    private static bool TryParseRelations(YamlMappingNode root, out IReadOnlyList<ResolvedRelation> relations, out string? error)
    {
        relations = [];
        error = null;

        if (!TryGetNode(root, "relations", out var node) || node is not YamlSequenceNode seq)
        {
            return true;
        }

        var list = new List<ResolvedRelation>(seq.Children.Count);
        foreach (var item in seq.Children)
        {
            if (item is not YamlMappingNode map)
            {
                error = "Each item in 'relations' must be a YAML mapping.";
                return false;
            }

            var srcPath = GetOptionalString(map, "src_path") ?? "";
            var dstPath = GetOptionalString(map, "dst_path") ?? "";
            var kindRaw = GetOptionalString(map, "kind") ?? "related";
            if (!TryParseRelationKind(kindRaw, out var kind))
            {
                error = $"Invalid relation kind '{kindRaw}'.";
                return false;
            }

            if (!TryGetOptionalDouble(map, "weight", out var weight, out error))
            {
                return false;
            }

            var origin = GetOptionalString(map, "origin") ?? "";
            var reason = GetOptionalString(map, "reason") ?? "";

            list.Add(new ResolvedRelation(srcPath, dstPath, kind, weight ?? 0.0, origin, reason));
        }

        relations = list;
        return true;
    }

    private static bool TryParseLlm(YamlMappingNode root, out LlmProvenance? llm, out string? error)
    {
        llm = null;
        error = null;

        if (!TryGetNode(root, "llm", out var node) || node is not YamlMappingNode map)
        {
            return true;
        }

        if (!TryGetOptionalDateTimeOffset(map, "extracted_at", out var extractedAt, out error))
        {
            return false;
        }

        llm = new LlmProvenance
        {
            Model = GetOptionalString(map, "model") ?? "",
            Profile = GetOptionalString(map, "profile") ?? "",
            PromptPack = GetOptionalString(map, "prompt_pack") ?? "",
            PromptHash = GetOptionalString(map, "prompt_hash") ?? "",
            RunId = GetOptionalString(map, "run_id") ?? "",
            ExtractedAt = extractedAt ?? default
        };
        return true;
    }

    private static bool TryParsePrivacy(YamlMappingNode root, out PrivacyInfo privacy, out string? error)
    {
        privacy = new PrivacyInfo();
        error = null;

        if (!TryGetNode(root, "privacy", out var node) || node is not YamlMappingNode map)
        {
            return true;
        }

        if (!TryGetOptionalBool(map, "cloud_allowed", out var cloudAllowed, out error))
        {
            return false;
        }

        privacy = new PrivacyInfo
        {
            CloudAllowed = cloudAllowed ?? false,
            Sensitivity = GetOptionalString(map, "sensitivity") ?? "normal"
        };
        return true;
    }

    private static object? ConvertYamlNode(YamlNode node) => node switch
    {
        YamlMappingNode map => ConvertMapping(map),
        YamlSequenceNode seq => seq.Children.Select(ConvertYamlNode).ToList(),
        YamlScalarNode scalar => ConvertScalar(scalar),
        _ => null
    };

    private static Dictionary<string, object?> ConvertMapping(YamlMappingNode map)
    {
        var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var entry in map.Children)
        {
            if (entry.Key is YamlScalarNode k && k.Value is { Length: > 0 } keyName)
            {
                dict[keyName] = ConvertYamlNode(entry.Value);
            }
        }

        return dict;
    }

    private static object? ConvertScalar(YamlScalarNode scalar)
    {
        if (scalar.Style != ScalarStyle.Plain)
        {
            return scalar.Value ?? string.Empty;
        }

        var val = scalar.Value;
        if (string.IsNullOrEmpty(val) || val is "~" or "null")
        {
            return null;
        }

        if (string.Equals(val, "true", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (string.Equals(val, "false", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (long.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longVal))
        {
            return longVal;
        }

        if (double.TryParse(val, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture, out var doubleVal))
        {
            return doubleVal;
        }

        return val;
    }

    private static bool TryParseNoteType(string raw, out NoteType value)
    {
        var normalized = raw.Replace("_", "", StringComparison.Ordinal);
        return Enum.TryParse(normalized, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    private static bool TryParseNoteStatus(string raw, out NoteStatus value)
    {
        var normalized = raw.Replace("_", "", StringComparison.Ordinal);
        return Enum.TryParse(normalized, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    private static bool TryParseRelationKind(string raw, out RelationKind value)
    {
        var normalized = raw.Replace("_", "", StringComparison.Ordinal);
        return Enum.TryParse(normalized, ignoreCase: true, out value) && Enum.IsDefined(value);
    }

    private static string FormatNoteType(NoteType type) => type.ToString().ToLowerInvariant();

    private static string FormatNoteStatus(NoteStatus status) => status switch
    {
        NoteStatus.OnHold => "on_hold",
        _ => status.ToString().ToLowerInvariant()
    };

    private static string FormatRelationKind(RelationKind kind) => kind switch
    {
        RelationKind.PartOf => "part_of",
        RelationKind.DuplicateOf => "duplicate_of",
        _ => kind.ToString().ToLowerInvariant()
    };

    private static void AddIfNotNull(Dictionary<string, object?> target, string key, string? value)
    {
        if (value is not null)
        {
            target[key] = value;
        }
    }

    private static void AddIfNonEmpty<T>(Dictionary<string, object?> target, string key, IReadOnlyCollection<T> items)
    {
        if (items.Count > 0)
        {
            target[key] = items;
        }
    }

    private static void AddIfNonEmpty(Dictionary<string, object?> target, string key, IReadOnlyDictionary<string, object?> dict)
    {
        if (dict.Count > 0)
        {
            target[key] = dict;
        }
    }
}