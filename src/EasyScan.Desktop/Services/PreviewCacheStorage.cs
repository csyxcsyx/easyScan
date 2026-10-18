using System.IO;

namespace EasyScan.Desktop.Services;

internal sealed class PreviewCacheStorage : IDisposable
{
    private FileStream? _sessionLock;
    public string DirectoryPath { get; }

    public PreviewCacheStorage(string? root = null)
    {
        root = Path.GetFullPath(root ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "easyScan", "PreviewCache"));
        Directory.CreateDirectory(root);
        foreach (var directory in Directory.GetDirectories(root))
        {
            if (!Guid.TryParseExact(Path.GetFileName(directory), "N", out _) || !File.Exists(Path.Combine(directory, "session.lock"))) continue;
            if (Directory.GetLastWriteTimeUtc(directory) > DateTime.UtcNow.AddMinutes(-1)) continue;
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            try
            {
                using (File.Open(Path.Combine(directory, "session.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None)) { }
                Directory.Delete(directory, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        DirectoryPath = Path.Combine(root, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(DirectoryPath);
        _sessionLock = File.Open(Path.Combine(DirectoryPath, "session.lock"), FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
    }

    public string PathFor(Guid document, int page, string kind) => Path.Combine(DirectoryPath, $"{document:N}-{page}-{kind}.png");
    public async Task WriteAsync(string path, byte[] bytes, CancellationToken token)
    {
        var temporary = path + ".tmp";
        try { await File.WriteAllBytesAsync(temporary, bytes, token).ConfigureAwait(false); token.ThrowIfCancellationRequested(); File.Move(temporary, path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public void Dispose()
    {
        _sessionLock?.Dispose(); _sessionLock = null;
        try { Directory.Delete(DirectoryPath, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
