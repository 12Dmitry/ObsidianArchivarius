namespace SecondBrain.Infrastructure.Vault;

public static class VaultPathPolicy
{
    private static readonly string[] ForbiddenSystemSegments = [".brain", ".obsidian", ".git"];

    public static (string NormalizedRelativePath, string FullPath) NormalizeAndResolve(
        string vaultRoot,
        string relativePath)
    {
        ValidateVaultRoot(vaultRoot);

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            throw new ArgumentException("Relative path cannot be null or whitespace.", nameof(relativePath));
        }

        if (relativePath.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Relative path cannot contain null bytes.", nameof(relativePath));
        }

        if (IsRootedOrDriveQualified(relativePath))
        {
            throw new ArgumentException($"Rooted or drive-qualified path is not allowed: '{relativePath}'.", nameof(relativePath));
        }

        var unified = relativePath.Replace('\\', '/');
        if (IsRootedOrDriveQualified(unified))
        {
            throw new ArgumentException($"Rooted or drive-qualified path is not allowed: '{relativePath}'.", nameof(relativePath));
        }

        var segments = unified.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (string.IsNullOrWhiteSpace(segment))
            {
                throw new ArgumentException($"Path contains an empty or whitespace segment: '{relativePath}'.", nameof(relativePath));
            }

            if (segment is "." or "..")
            {
                throw new ArgumentException($"Path traversal segment '{segment}' is not allowed: '{relativePath}'.", nameof(relativePath));
            }

            if (IsForbiddenSystemSegment(segment))
            {
                throw new UnauthorizedAccessException($"Access to system directory '{segment}' is forbidden: '{relativePath}'.");
            }
        }

        var rootFull = Path.GetFullPath(vaultRoot);
        var rootWithSeparator = Path.EndsInDirectorySeparator(rootFull)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;

        var normalizedRelativePath = string.Join('/', segments);
        var relativeOsPath = string.Join(Path.DirectorySeparatorChar, segments);
        var combinedPath = Path.Combine(rootFull, relativeOsPath);
        var fullPath = Path.GetFullPath(combinedPath);

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!fullPath.StartsWith(rootWithSeparator, comparison) ||
            string.Equals(fullPath, rootWithSeparator, comparison))
        {
            throw new UnauthorizedAccessException($"Resolved path '{fullPath}' escapes vault root '{rootFull}'.");
        }

        return (normalizedRelativePath, fullPath);
    }

    public static string? NormalizePrefix(string? prefix)
    {
        if (string.IsNullOrWhiteSpace(prefix))
        {
            return null;
        }

        if (prefix.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Prefix cannot contain null bytes.", nameof(prefix));
        }

        if (IsRootedOrDriveQualified(prefix))
        {
            throw new ArgumentException($"Rooted or drive-qualified prefix is not allowed: '{prefix}'.", nameof(prefix));
        }

        var unified = prefix.Replace('\\', '/');
        if (IsRootedOrDriveQualified(unified))
        {
            throw new ArgumentException($"Rooted or drive-qualified prefix is not allowed: '{prefix}'.", nameof(prefix));
        }

        var hasTrailingSlash = unified.EndsWith('/');
        var trimmed = hasTrailingSlash ? unified[..^1] : unified;
        if (trimmed.Length == 0)
        {
            throw new ArgumentException($"Invalid prefix '{prefix}'.", nameof(prefix));
        }

        var segments = trimmed.Split('/');
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            if (string.IsNullOrWhiteSpace(segment))
            {
                throw new ArgumentException($"Prefix contains an empty or whitespace segment: '{prefix}'.", nameof(prefix));
            }

            if (segment is "." or "..")
            {
                throw new ArgumentException($"Prefix traversal segment '{segment}' is not allowed: '{prefix}'.", nameof(prefix));
            }

            if (IsForbiddenSystemSegment(segment))
            {
                throw new UnauthorizedAccessException($"Access to system directory '{segment}' is forbidden: '{prefix}'.");
            }
        }

        var normalized = string.Join('/', segments);
        return hasTrailingSlash ? normalized + "/" : normalized;
    }

    public static bool IsForbiddenSystemSegment(string segment)
    {
        for (var i = 0; i < ForbiddenSystemSegments.Length; i++)
        {
            if (string.Equals(segment, ForbiddenSystemSegments[i], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static void ValidateVaultRoot(string vaultRoot)
    {
        if (string.IsNullOrWhiteSpace(vaultRoot))
        {
            throw new ArgumentException("Vault root cannot be null or whitespace.", nameof(vaultRoot));
        }

        if (vaultRoot.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("Vault root cannot contain null bytes.", nameof(vaultRoot));
        }
    }

    private static bool IsRootedOrDriveQualified(string path)
    {
        if (Path.IsPathRooted(path))
        {
            return true;
        }

        if (path.StartsWith('/') || path.StartsWith('\\'))
        {
            return true;
        }

        return path.Length >= 2 && char.IsAsciiLetter(path[0]) && path[1] == ':';
    }
}