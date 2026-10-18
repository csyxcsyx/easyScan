using EasyScan.Core;
using OpenCvSharp;
using System.IO;
using System.Windows.Media.Imaging;

namespace EasyScan.Desktop.Services;

public sealed record PreviewFrame(BitmapSource Original, BitmapSource Color, BitmapSource Final, byte[] ColorBytes);
public interface IPreviewProvider
{
    Task<PreviewFrame> GetAsync(ExportPage page, OutputColorMode mode, double binaryStrength, CancellationToken token = default, int maxDimension = 1600);
    Task<(int Width, int Height)> GetSizeAsync(ExportPage page, CancellationToken token = default);
}

public sealed class PreviewCache : IPreviewProvider, IDisposable
{
    private sealed record SourceStamp(long Length, long Modified);
    private sealed class Entry(Guid document, int page, int resolution, SourceStamp stamp, ScanSettings settings, string originalPath, string colorPath)
    {
        public Guid Document { get; } = document; public int Page { get; } = page;
        public int Resolution { get; } = resolution;
        public SourceStamp Stamp { get; } = stamp; public ScanSettings Settings { get; } = settings;
        public string OriginalPath { get; } = originalPath; public string ColorPath { get; } = colorPath;
        public byte[]? OriginalBytes, ColorBytes;
        public BitmapSource? Original, Color;
        public readonly Dictionary<(OutputColorMode, double), BitmapSource> Variants = [];
        public long Access, DiskSize;
        public int Width, Height;
        public long MemorySize => OriginalBytes is null ? 0 : OriginalBytes.Length + ColorBytes!.Length
            + (long)Original!.PixelWidth * Original.PixelHeight * 4 + (long)Color!.PixelWidth * Color.PixelHeight * 4
            + Variants.Where(x => x.Key.Item1 != OutputColorMode.Color).Sum(x => (long)x.Value.PixelWidth * x.Value.PixelHeight * 4);
        public void Detach() { OriginalBytes = null; ColorBytes = null; Original = null; Color = null; Variants.Clear(); }
    }
    private readonly ImageLoader _loader;
    private readonly DocumentProcessor _processor;
    private readonly PreviewCacheStorage _storage;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _sync = new();
    private readonly Dictionary<(Guid, int, int), Entry> _entries = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly long _memoryLimit, _diskLimit;
    private long _clock;
    private bool _disposed;
    public int ProcessingCount { get; private set; }
    public int DecodeCount { get; private set; }
    public int CacheHits { get; private set; }
    public string SessionDirectory => _storage.DirectoryPath;
    public long MemoryBytes { get { lock (_sync) return _entries.Values.Sum(x => x.MemorySize); } }
    public long DiskBytes { get { lock (_sync) return _entries.Values.Sum(x => x.DiskSize); } }
    public async Task<(int Width, int Height)> GetSizeAsync(ExportPage page, CancellationToken token = default)
    {
        var stamp = Stamp(page.Source);
        lock (_sync) if (_entries.TryGetValue(Id(page.Source), out var entry) && entry.Stamp == stamp && entry.Settings == page.Settings && entry.Width > 0)
            return (entry.Width, entry.Height);
        var frame = await GetAsync(page, OutputColorMode.Color, 0, token).ConfigureAwait(false);
        return (frame.Color.PixelWidth, frame.Color.PixelHeight);
    }

    public PreviewCache(ImageLoader loader, DocumentProcessor processor, string? root = null,
        long memoryLimit = 128L * 1024 * 1024, long diskLimit = 512L * 1024 * 1024)
    {
        _loader = loader; _processor = processor; _storage = new(root); _memoryLimit = memoryLimit; _diskLimit = diskLimit;
    }
    private static SourceStamp Stamp(PageSource source)
    {
        var file = new FileInfo(source.Document.Path); return new(file.Length, file.LastWriteTimeUtc.Ticks);
    }
    private static (Guid, int, int) Id(PageSource page, int resolution = 1600) => (page.Document.Id, page.PageIndex, resolution);
    private static (OutputColorMode, double) Variant(OutputColorMode mode, double strength) => (mode, mode == OutputColorMode.Binary ? strength : 0);

    public bool TryGet(ExportPage page, OutputColorMode mode, double strength, out PreviewFrame? frame, int maxDimension = 1600)
    {
        frame = null;
        try
        {
            var stamp = Stamp(page.Source);
            lock (_sync)
            {
                if (_disposed || !_entries.TryGetValue(Id(page.Source, maxDimension), out var entry) || entry.Stamp != stamp || entry.Settings != page.Settings
                    || entry.ColorBytes is null || !entry.Variants.TryGetValue(Variant(mode, strength), out var final)) return false;
                entry.Access = ++_clock; CacheHits++; frame = new(entry.Original!, entry.Color!, final, entry.ColorBytes); return true;
            }
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    public async Task SeedAsync(ExportPage page, Mat original, CancellationToken token)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        await _gate.WaitAsync(cancellation.Token).ConfigureAwait(false);
        try { var entry = await BuildAsync(page, original, cancellation.Token, 1600).ConfigureAwait(false); Trim(entry); }
        finally { Release(); }
    }

    public async Task<PreviewFrame> GetAsync(ExportPage page, OutputColorMode mode, double binaryStrength, CancellationToken token = default, int maxDimension = 1600)
    {
        if (maxDimension is not (1600 or 3200)) throw new ArgumentOutOfRangeException(nameof(maxDimension));
        if (TryGet(page, mode, binaryStrength, out var cached, maxDimension)) return cached!;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token); token = cancellation.Token;
        await _gate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            token.ThrowIfCancellationRequested();
            if (TryGet(page, mode, binaryStrength, out cached, maxDimension)) return cached!;
            Entry? entry; lock (_sync) _entries.TryGetValue(Id(page.Source, maxDimension), out entry);
            if (entry?.Stamp != Stamp(page.Source) || entry.Settings != page.Settings)
                entry = await BuildAsync(page, null, token, maxDimension).ConfigureAwait(false);
            else if (entry.ColorBytes is null)
            {
                var original = await File.ReadAllBytesAsync(entry.OriginalPath, token).ConfigureAwait(false);
                var color = await File.ReadAllBytesAsync(entry.ColorPath, token).ConfigureAwait(false);
                await AttachAsync(entry, original, color, token).ConfigureAwait(false);
                CacheHits++;
            }
            var key = Variant(mode, binaryStrength);
            if (!entry.Variants.TryGetValue(key, out var final))
            {
                var colorBytes = entry.ColorBytes!;
                final = await Task.Run(() =>
                {
                    using var color = Cv2.ImDecode(colorBytes, ImreadModes.Color);
                    using var converted = _processor.ConvertColor(color, mode, binaryStrength, token);
                    return BitmapHelper.FromBytes(converted.ToBytes(".png"));
                }, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                lock (_sync)
                {
                    foreach (var old in entry.Variants.Keys.Where(x => x.Item1 == mode).ToArray()) entry.Variants.Remove(old);
                    entry.Variants[key] = final;
                }
            }
            var frame = new PreviewFrame(entry.Original!, entry.Color!, final, entry.ColorBytes!);
            Trim(entry); return frame;
        }
        finally { Release(); }
    }

    private async Task<Entry> BuildAsync(ExportPage page, Mat? supplied, CancellationToken token, int maxDimension)
    {
        var stamp = Stamp(page.Source); var originalPath = _storage.PathFor(page.Source.Document.Id, page.Source.PageIndex, "original-" + maxDimension);
        var colorPath = _storage.PathFor(Guid.NewGuid(), page.Source.PageIndex, "color");
        Entry? previous; lock (_sync) _entries.TryGetValue(Id(page.Source, maxDimension), out previous);
        var committed = false;
        try
        {
            byte[] originalBytes, colorBytes;
            if (supplied is not null)
                originalBytes = await Task.Run(() =>
                {
                    using var resized = new Mat();
                    var scale = Math.Min(1, maxDimension / (double)Math.Max(supplied.Width, supplied.Height));
                    Cv2.Resize(supplied, resized, new Size(), scale, scale, InterpolationFlags.Area);
                    return resized.ToBytes(".png");
                }, token).ConfigureAwait(false);
            else if (previous?.Stamp == stamp && File.Exists(previous.OriginalPath))
                originalBytes = previous.OriginalBytes ?? await File.ReadAllBytesAsync(previous.OriginalPath, token).ConfigureAwait(false);
            else
            {
                using var original = await _loader.LoadAsync(page.Source, maxDimension, cancellationToken: token).ConfigureAwait(false);
                DecodeCount++; originalBytes = await Task.Run(() => original.ToBytes(".png"), token).ConfigureAwait(false);
            }
            colorBytes = await Task.Run(() =>
            {
                using var decoded = Cv2.ImDecode(originalBytes, ImreadModes.Color);
                using var color = _processor.ProcessColor(decoded, page.Settings, token); return color.ToBytes(".png");
            }, token).ConfigureAwait(false);
            ProcessingCount++; token.ThrowIfCancellationRequested();
            if (Stamp(page.Source) != stamp) throw new IOException("生成预览期间原文件发生变化，请重试。");
            await _storage.WriteAsync(originalPath, originalBytes, token).ConfigureAwait(false);
            await _storage.WriteAsync(colorPath, colorBytes, token).ConfigureAwait(false);
            var entry = new Entry(page.Source.Document.Id, page.Source.PageIndex, maxDimension, stamp, page.Settings, originalPath, colorPath)
                { DiskSize = originalBytes.Length + colorBytes.Length };
            await AttachAsync(entry, originalBytes, colorBytes, token).ConfigureAwait(false);
            lock (_sync) { token.ThrowIfCancellationRequested(); entry.Access = ++_clock; _entries[Id(page.Source, maxDimension)] = entry; committed = true; }
            if (previous is not null) try { File.Delete(previous.ColorPath); } catch (IOException) { }
            return entry;
        }
        finally
        {
            if (!committed)
            {
                try { File.Delete(colorPath); if (previous is null) File.Delete(originalPath); }
                catch (IOException) { }
            }
        }
    }
    private async Task AttachAsync(Entry entry, byte[] original, byte[] color, CancellationToken token)
    {
        var bitmaps = await Task.Run(() => (Original: BitmapHelper.FromBytes(original), Color: BitmapHelper.FromBytes(color)), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        lock (_sync)
        {
            entry.OriginalBytes = original; entry.ColorBytes = color; entry.Original = bitmaps.Original; entry.Color = bitmaps.Color;
            entry.Variants[Variant(OutputColorMode.Color, 0)] = bitmaps.Color; entry.Access = ++_clock;
            entry.Width = bitmaps.Color.PixelWidth; entry.Height = bitmaps.Color.PixelHeight;
        }
    }
    private void Trim(Entry keep)
    {
        lock (_sync)
        {
            foreach (var entry in _entries.Values.Where(x => x != keep).OrderBy(x => x.Access).ToArray())
            {
                if (_entries.Values.Sum(x => x.MemorySize) <= _memoryLimit) break;
                entry.Detach();
            }
            if (_entries.Values.Sum(x => x.MemorySize) > _memoryLimit) keep.Detach();
            foreach (var entry in _entries.Values.Where(x => x != keep).OrderBy(x => x.Access).ToArray())
            {
                if (_entries.Values.Sum(x => x.DiskSize) <= _diskLimit) break;
                Delete(entry);
            }
            if (_entries.Values.Sum(x => x.DiskSize) > _diskLimit) Delete(keep);
        }
    }
    private void Delete(Entry entry)
    {
        _entries.Remove((entry.Document, entry.Page, entry.Resolution));
        try { File.Delete(entry.OriginalPath); File.Delete(entry.ColorPath); } catch (IOException) { }
    }
    public void Remove(PageSource page) { lock (_sync) foreach (var entry in _entries.Values.Where(x => x.Document == page.Document.Id && x.Page == page.PageIndex).ToArray()) Delete(entry); }
    private void Release() { if (_disposed) _storage.Dispose(); _gate.Release(); }
    public void Dispose()
    {
        lock (_sync) { if (_disposed) return; _disposed = true; _entries.Clear(); }
        _lifetime.Cancel();
        if (_gate.Wait(0)) { try { _storage.Dispose(); } finally { _gate.Release(); } }
    }
}
