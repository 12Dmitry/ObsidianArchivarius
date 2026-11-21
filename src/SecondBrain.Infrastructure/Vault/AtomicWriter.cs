using System.Text;

namespace SecondBrain.Infrastructure.Vault;

public static class AtomicWriter
{
    private const int MaxReplaceAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(15);
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public static async Task WriteAllTextAtomicAsync(string fullPath, string content, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var bytes = Utf8NoBom.GetBytes(content);
        await WriteAllBytesAtomicAsync(fullPath, bytes, ct).ConfigureAwait(false);
    }

    public static async Task WriteAllBytesAtomicAsync(
        string fullPath,
        ReadOnlyMemory<byte> bytes,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullPath);
        ct.ThrowIfCancellationRequested();

        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var tempPath = $"{fullPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            await using (var stream = new FileStream(
                tempPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                useAsync: true))
            {
                await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                await stream.FlushAsync(ct).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            ct.ThrowIfCancellationRequested();

            await CommitTempFileWithRetryAsync(tempPath, fullPath, ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteTempFile(tempPath);
        }
    }

    private static async Task CommitTempFileWithRetryAsync(string tempPath, string fullPath, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (File.Exists(fullPath))
                {
                    File.Replace(tempPath, fullPath, destinationBackupFileName: null, ignoreMetadataErrors: true);
                }
                else
                {
                    File.Move(tempPath, fullPath, overwrite: false);
                }

                return;
            }
            catch (Exception ex) when (attempt < MaxReplaceAttempts && (ex is IOException or UnauthorizedAccessException))
            {
                await Task.Delay(RetryDelay, ct).ConfigureAwait(false);
            }
        }
    }

    private static void TryDeleteTempFile(string tempPath)
    {
        try
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
        catch
        {
            // Best-effort cleanup in finally; never mask the primary exception.
        }
    }
}