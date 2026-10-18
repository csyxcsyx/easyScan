using ImageMagick;
using ImageMagick.Drawing;
using OpenCvSharp;
using System.Text;

namespace EasyScan.Core;

public sealed class WatermarkComposer
{
    private static readonly int[,] Dither = { { 0, 8, 2, 10 }, { 12, 4, 14, 6 }, { 3, 11, 1, 9 }, { 15, 7, 13, 5 } };
    private static byte Ink(WatermarkSettings settings) => settings.Color switch { WatermarkColor.Black => 0, WatermarkColor.White => 255, _ => 102 };

    public Mat Apply(Mat image, WatermarkSettings settings, OutputColorMode mode, CancellationToken token = default)
    {
        settings.Validate();
        var output = image.Clone();
        if (!settings.Enabled) return output;
        try
        {
            using var mask = CreateMask(image.Width, image.Height, settings);
            var alpha = mask.AsRows<byte>(); var ink = Ink(settings); var width = image.Width; var height = image.Height;
            if (mode == OutputColorMode.Binary) ink = ink < 128 ? (byte)0 : (byte)255;
            if (image.Channels() == 1)
            {
                var pixels = output.AsRows<byte>();
                for (var y = 0; y < height; y++)
                {
                    if (y % 64 == 0) token.ThrowIfCancellationRequested();
                    var row = pixels[y]; var coverage = alpha[y];
                    for (var x = 0; x < width; x++)
                    {
                        var a = coverage[x] / 255d * settings.Opacity;
                        if (mode == OutputColorMode.Binary) { if (a > (Dither[y & 3, x & 3] + .5) / 16) row[x] = ink; }
                        else row[x] = (byte)Math.Round(row[x] * (1 - a) + ink * a);
                    }
                }
            }
            else
            {
                var pixels = output.AsRows<Vec3b>();
                for (var y = 0; y < height; y++)
                {
                    if (y % 64 == 0) token.ThrowIfCancellationRequested();
                    var row = pixels[y]; var coverage = alpha[y];
                    for (var x = 0; x < width; x++)
                    {
                        var a = coverage[x] / 255d * settings.Opacity; var p = row[x];
                        row[x] = new((byte)Math.Round(p.Item0 * (1 - a) + ink * a), (byte)Math.Round(p.Item1 * (1 - a) + ink * a), (byte)Math.Round(p.Item2 * (1 - a) + ink * a));
                    }
                }
            }
            return output;
        }
        catch { output.Dispose(); throw; }
    }

    public byte[] CreateOverlay(int width, int height, WatermarkSettings settings, OutputColorMode mode, CancellationToken token = default)
    {
        using var mask = CreateMask(width, height, settings);
        using var overlay = new Mat(height, width, MatType.CV_8UC4, Scalar.All(0));
        var pixels = overlay.AsRows<Vec4b>(); var masks = mask.AsRows<byte>(); var ink = Ink(settings);
        if (mode == OutputColorMode.Binary) ink = ink < 128 ? (byte)0 : (byte)255;
        for (var y = 0; y < height; y++)
        {
            if (y % 64 == 0) token.ThrowIfCancellationRequested();
            var row = pixels[y]; var coverage = masks[y];
            for (var x = 0; x < width; x++)
            {
                var a = coverage[x] / 255d * settings.Opacity;
                var opacity = mode == OutputColorMode.Binary ? a > (Dither[y & 3, x & 3] + .5) / 16 ? 255 : 0 : (int)Math.Round(a * 255);
                row[x] = new(ink, ink, ink, (byte)opacity);
            }
        }
        return overlay.ToBytes(".png");
    }

    private static Mat CreateMask(int width, int height, WatermarkSettings settings)
    {
        settings.Validate();
        var font = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), "msyh.ttc");
        if (!File.Exists(font)) throw new InvalidOperationException("缺少微软雅黑字体，请在 Windows 中安装中文字体后使用水印。");
        var size = Math.Max(8, Math.Min(width, height) * settings.FontSizePercent / 100);
        var draw = new Drawables().Font(font).FontPointSize(size).TextEncoding(Encoding.UTF8).FillColor(MagickColors.White);
        var metrics = draw.FontTypeMetrics(settings.Text) ?? throw new InvalidOperationException("无法测量水印字体。");
        var maxWidth = width * (settings.Style == WatermarkStyle.Tiled ? .45 : .85);
        if (metrics.TextWidth > maxWidth) { size *= maxWidth / metrics.TextWidth; draw.FontPointSize(size); metrics = draw.FontTypeMetrics(settings.Text) ?? throw new InvalidOperationException("无法测量水印字体。"); }
        using var text = new MagickImage(MagickColors.Transparent, (uint)Math.Max(1, Math.Ceiling(metrics.TextWidth + 12)),
            (uint)Math.Max(1, Math.Ceiling(metrics.Ascent - metrics.Descent + 12)));
        draw.Text(6, metrics.Ascent + 6, settings.Text).Draw(text);
        text.BackgroundColor = MagickColors.Transparent;
        if (settings.Style != WatermarkStyle.Corner) text.Rotate(settings.Angle);
        using var glyph = Cv2.ImDecode(text.ToByteArray(MagickFormat.Png), ImreadModes.Unchanged);
        using var alpha = new Mat(); Cv2.ExtractChannel(glyph, alpha, 3);
        var mask = new Mat(height, width, MatType.CV_8UC1, Scalar.All(0));
        void Stamp(int x, int y)
        {
            var left = Math.Max(0, x); var top = Math.Max(0, y); var right = Math.Min(width, x + alpha.Width); var bottom = Math.Min(height, y + alpha.Height);
            if (right <= left || bottom <= top) return;
            using var source = new Mat(alpha, new Rect(left - x, top - y, right - left, bottom - top));
            using var target = new Mat(mask, new Rect(left, top, right - left, bottom - top)); Cv2.Max(target, source, target);
        }
        if (settings.Style == WatermarkStyle.Tiled)
        {
            var stepX = Math.Max(alpha.Width + (int)size * 2, width / 3); var stepY = Math.Max(alpha.Height + (int)size * 3, height / 5);
            var row = 0;
            for (var y = -alpha.Height / 2; y < height; y += stepY, row++)
                for (var x = -alpha.Width / 2 - (row % 2) * stepX / 2; x < width; x += stepX) Stamp(x, y);
        }
        else if (settings.Style == WatermarkStyle.Corner)
        {
            var margin = Math.Max(4, (int)(Math.Min(width, height) * .04));
            var x = settings.Corner is WatermarkCorner.BottomLeft or WatermarkCorner.TopLeft ? margin : width - margin - alpha.Width;
            var y = settings.Corner is WatermarkCorner.TopLeft or WatermarkCorner.TopRight ? margin : height - margin - alpha.Height;
            Stamp(x, y);
        }
        else Stamp((width - alpha.Width) / 2, (height - alpha.Height) / 2);
        return mask;
    }
}
