using OpenCvSharp;

namespace EasyScan.Core;

/// <summary>One CPU worker, bounded text-only cache. No source pixels survive a request.</summary>
public sealed class OcrService(ImageLoader loader, DocumentProcessor processor, IOcrEngine? engine = null) : IDisposable
{
    private sealed record Key(string Path, int Page, long Length, long Modified, ScanSettings Settings, string Engine, int Dpi);
    private readonly IOcrEngine _engine = engine ?? new OnnxOcrEngine();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<Key, OcrPageResult> _cache = [];
    private readonly Queue<Key> _order = [];
    private bool _disposed;

    public string Version => _engine.Version + "/scan-color-aware-2";
    public Task<OcrPageResult> RecognizeAsync(ExportPage page, IProgress<OcrProgress>? progress = null,
        CancellationToken token = default, int dpi = 300)
        => RecognizeCoreAsync(page, null, null, progress, token, dpi);

    internal Task<OcrPageResult> RecognizePreparedAsync(ExportPage page, PreparedPage prepared, (long Length, long Modified) sourceVersion, IProgress<OcrProgress>? progress,
        CancellationToken token, int dpi)
        => RecognizeCoreAsync(page, prepared, sourceVersion, progress, token, dpi);

    private async Task<OcrPageResult> RecognizeCoreAsync(ExportPage page, PreparedPage? prepared, (long Length, long Modified)? sourceVersion, IProgress<OcrProgress>? progress,
        CancellationToken token, int dpi)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        token = cancellation.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var info = new FileInfo(page.Source.Document.Path);
            var key = new Key(info.FullName, page.Source.PageIndex, info.Length, info.LastWriteTimeUtc.Ticks, page.Settings, Version, dpi);
            if (sourceVersion is { } expected && (key.Length != expected.Length || key.Modified != expected.Modified))
                throw new IOException("识别期间原文件发生变化，请重试。");
            if (_cache.TryGetValue(key, out var cached)) { progress?.Report(new("已复用文字结果")); return cached; }
            progress?.Report(new("正在读取原图…"));
            using var ownedPrepared = prepared is null ? await processor.PrepareAsync(loader, page, dpi, token).ConfigureAwait(false) : null;
            using var color = await Task.Run(() => (prepared ?? ownedPrepared!).EnhanceForOcr(page.Settings, token), token).ConfigureAwait(false);
            var result = await _engine.RecognizeAsync(color, progress, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            info.Refresh();
            if (info.Length != key.Length || info.LastWriteTimeUtc.Ticks != key.Modified)
                throw new IOException("识别期间原文件发生变化，请重试。");
            _cache.Add(key, result); _order.Enqueue(key);
            while (_order.Count > 32) _cache.Remove(_order.Dequeue());
            return result;
        }
        finally { _gate.Release(); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _lifetime.Cancel();
        // Disposing a native session must never race with a running inference.
        _ = CleanupAsync();
    }
    private async Task CleanupAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _engine.Dispose(); _cache.Clear(); _order.Clear(); }
        finally { _gate.Release(); _lifetime.Dispose(); }
    }
}
