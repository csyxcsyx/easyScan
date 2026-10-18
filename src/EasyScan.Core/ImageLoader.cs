using ImageMagick;
using OpenCvSharp;
using PDFtoImage;

namespace EasyScan.Core;

public sealed class ImageLoader
{
    public static readonly string[] SupportedExtensions = [".jpg", ".jpeg", ".png", ".heic", ".heif", ".pdf"];
    // PDFium calls are serialized independently of the UI and image-processing workers.
    private static readonly SemaphoreSlim PdfGate = new(1, 1);

    public async Task<IReadOnlyList<PageSource>> DescribeAsync(string path, CancellationToken cancellationToken = default)
    {
        path = System.IO.Path.GetFullPath(path);
        if (!SupportedExtensions.Contains(System.IO.Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException("支持 JPG、PNG、HEIC 和 PDF 文件。");
        if (!File.Exists(path)) throw new FileNotFoundException("找不到文件，可能尚未从 iCloud 下载。", path);
        var document = new SourceDocument(Guid.NewGuid(), path,
            System.IO.Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (document.IsPdf)
        {
            await PdfGate.WaitAsync(cancellationToken);
            try
            {
                var count = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(path);
                    return Conversion.GetPageCount(stream);
                }, cancellationToken);
                if (count < 1) throw new InvalidDataException("PDF 没有可读取的页面。");
                return Enumerable.Range(0, count).Select(i => new PageSource(document, i)).ToArray();
            }
            finally { PdfGate.Release(); }
        }
        return await Task.Run<IReadOnlyList<PageSource>>(() =>
        {
            using var metadata = new MagickImage();
            metadata.Ping(path);
            var width = (int)metadata.Width; var height = (int)metadata.Height;
            var orientation = metadata.GetExifProfile()?.GetValue(ExifTag.Orientation)?.Value ?? 1;
            if (orientation is >= 5 and <= 8) (width, height) = (height, width);
            return [new PageSource(document, 0, width, height)];
        }, cancellationToken);
    }

    public async Task<Mat> LoadAsync(PageSource page, int? maxDimension = null, int dpi = 300,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (page.Document.IsPdf) await PdfGate.WaitAsync(cancellationToken);
        try
        {
            return await Task.Run(() =>
            {
                Mat image;
                if (page.Document.IsPdf)
                {
                    using var stream = File.OpenRead(page.Document.Path);
                    var pageSize = Conversion.GetPageSize(stream, page.PageIndex, leaveOpen: true);
                    var renderDpi = maxDimension.HasValue
                        ? Math.Max(1, Math.Min(dpi, (int)Math.Ceiling(maxDimension.Value * 72d / Math.Max(pageSize.Width, pageSize.Height)))) : dpi;
                    if (pageSize.Width * pageSize.Height * renderDpi * renderDpi / (72d * 72) > 100_000_000)
                        throw new InvalidDataException("PDF 页面超过 1 亿像素，请先缩小页面。");
                    stream.Position = 0;
                    using var bitmap = Conversion.ToImage(stream, page: page.PageIndex,
                        options: new RenderOptions(Dpi: renderDpi));
                    using var encoded = bitmap.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
                    image = Cv2.ImDecode(encoded.ToArray(), ImreadModes.Color);
                }
                else
                {
                    // Bundled Magick.NET reads HEIC without depending on the OS HEIF extension.
                    using var magick = new MagickImage();
                    magick.Ping(page.Document.Path);
                    if ((long)magick.Width * magick.Height > 100_000_000)
                        throw new InvalidDataException("图片超过 1 亿像素，请先缩小图片。");
                    magick.Read(page.Document.Path);
                    magick.AutoOrient();
                    if (magick.GetColorProfile() is not null) magick.TransformColorSpace(ColorProfiles.SRGB);
                    else magick.ColorSpace = ColorSpace.sRGB;
                    magick.BackgroundColor = MagickColors.White;
                    magick.Alpha(AlphaAction.Remove);
                    if (maxDimension.HasValue && Math.Max(magick.Width, magick.Height) > maxDimension.Value)
                    {
                        var scale = maxDimension.Value / (double)Math.Max(magick.Width, magick.Height);
                        magick.Resize((uint)Math.Round(magick.Width * scale), (uint)Math.Round(magick.Height * scale));
                    }
                    image = Cv2.ImDecode(magick.ToByteArray(MagickFormat.Png), ImreadModes.Color);
                }
                if (image.Empty()) { image.Dispose(); throw new InvalidDataException("无法解码此文件。"); }
                if (maxDimension.HasValue && Math.Max(image.Width, image.Height) > maxDimension.Value)
                {
                    using var original = image;
                    image = new Mat();
                    var scale = maxDimension.Value / (double)Math.Max(original.Width, original.Height);
                    Cv2.Resize(original, image, new Size(), scale, scale, InterpolationFlags.Area);
                }
                if (cancellationToken.IsCancellationRequested) { image.Dispose(); cancellationToken.ThrowIfCancellationRequested(); }
                return image;
            }, cancellationToken);
        }
        finally { if (page.Document.IsPdf) PdfGate.Release(); }
    }
}
