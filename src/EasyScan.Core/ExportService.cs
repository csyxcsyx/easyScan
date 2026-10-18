using OpenCvSharp;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace EasyScan.Core;

public sealed class ExportService(ImageLoader loader, DocumentProcessor processor, OcrService? ocr = null)
{
    public async Task<ExportResult> ExportAsync(IReadOnlyList<ExportPage> pages, ExportSettings settings,
        IProgress<ExportProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (pages.Count == 0) throw new ArgumentException("请选择需要导出的页面。");
        var watermark = settings.Watermark ?? new(); watermark.Validate();
        var ocrOptions = settings.Ocr ?? new();
        using var ownedOcr = ocr is null && (ocrOptions.ExportText || ocrOptions.SearchablePdf) ? new OcrService(loader, processor) : null;
        var recognition = ocr ?? ownedOcr;
        if (File.Exists(settings.Folder)) throw new InvalidOperationException("保存位置是文件，请选择文件夹。");
        Directory.CreateDirectory(settings.Folder);
        var files = new List<string>();
        var failures = new List<ExportFailure>();
        var completed = 0;
        var groups = settings.Organization == ExportOrganization.CombinedPdf
            ? new[] { pages.ToArray() }
            : pages.GroupBy(x => x.Source.Document.Id).Select(g => g.ToArray()).ToArray();
        try
        {
            foreach (var group in groups)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pdfOutput = settings.Organization == ExportOrganization.CombinedPdf
                    || settings.Format == ExportFormat.Pdf
                    || (settings.Format == ExportFormat.Automatic && group[0].Source.Document.IsPdf);
                using var pdf = pdfOutput ? new PdfDocument() : null;
                if (pdf is not null) pdf.Info.Title = settings.Organization == ExportOrganization.CombinedPdf ? "easyScan 扫描件" : group[0].Source.Document.Name;
                var groupFailed = false;
                var textPages = new List<string>();
                var textWriter = pdf is not null && ocrOptions.SearchablePdf ? new PdfTextLayerWriter(pdf) : null;
                var layout = new PdfLayoutPlanner(settings.PdfLayout);
                var composer = new WatermarkComposer();
                foreach (var page in group)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    try
                    {
                        var stamp = new FileInfo(page.Source.Document.Path);
                        var sourceLength = stamp.Length; var sourceTime = stamp.LastWriteTimeUtc;
                        using var original = await loader.LoadAsync(page.Source, dpi: settings.Dpi, cancellationToken: cancellationToken);
                        using var prepared = await Task.Run(() => processor.Prepare(original, page.Settings, cancellationToken), cancellationToken);
                        var text = ocrOptions.ExportText || pdfOutput && ocrOptions.SearchablePdf
                            ? await recognition!.RecognizePreparedAsync(page, prepared, (sourceLength, sourceTime.Ticks), new RecognitionProgress(p =>
                                progress?.Report(new(completed, pages.Count, page.Source.DisplayName, p.Stage))), cancellationToken, settings.Dpi) : null;
                        stamp.Refresh();
                        if (!stamp.Exists || stamp.Length != sourceLength || stamp.LastWriteTimeUtc != sourceTime)
                            throw new IOException("导出期间原文件发生变化，请重试。");
                        var bytes = await Task.Run(() =>
                        {
                            using var enhanced = prepared.Enhance(page.Settings, cancellationToken);
                            using var converted = processor.ConvertColor(enhanced, settings.Color, settings.BinaryStrength, cancellationToken);
                            using var output = !pdfOutput || watermark.Target == WatermarkTarget.Scan
                                ? composer.Apply(converted, watermark, settings.Color, cancellationToken) : converted.Clone();
                            if (pdfOutput)
                            {
                                // PNG retains the grayscale/binary values without JPEG ringing.
                                var png = output.ToBytes(".png");
                                using var stream = new MemoryStream(png);
                                using var image = XImage.FromStream(stream);
                                var placement = layout.PlaceNext(output.Width, output.Height);
                                if (pdf!.PageCount <= placement.SheetIndex)
                                {
                                    var added = pdf.AddPage(); added.Width = XUnit.FromPoint(layout.PageWidth); added.Height = XUnit.FromPoint(layout.PageHeight);
                                }
                                var pdfPage = pdf.Pages[placement.SheetIndex];
                                using (var graphics = XGraphics.FromPdfPage(pdfPage))
                                    graphics.DrawImage(image, placement.X, placement.Y, placement.Width, placement.Height);
                                if (text is not null) textWriter?.Add(pdfPage, placement, text);
                                return Array.Empty<byte>();
                            }
                            return settings.Format == ExportFormat.Png ? output.ToBytes(".png")
                                : output.ToBytes(".jpg", [new ImageEncodingParam(ImwriteFlags.JpegQuality, settings.JpegQuality)]);
                        }, cancellationToken);
                        if (!pdfOutput)
                        {
                            var stem = page.Source.Document.Name + (page.Source.Document.IsPdf ? $"-p{page.Source.PageIndex + 1:000}" : "-扫描");
                            var extension = settings.Format == ExportFormat.Png ? ".png" : ".jpg";
                            var imagePath = await WriteAtomicAsync(settings.Folder, stem, extension, bytes, cancellationToken);
                            files.Add(imagePath);
                            if (ocrOptions.ExportText && text is not null)
                                files.Add(await WriteAtomicAsync(settings.Folder, Path.GetFileNameWithoutExtension(imagePath), ".txt", System.Text.Encoding.UTF8.GetBytes(text.Text), cancellationToken));
                        }
                        else if (text is not null) textPages.Add(text.Text);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        groupFailed = true;
                        failures.Add(new(page.Source.DisplayName, FriendlyError(ex)));
                    }
                    progress?.Report(new(++completed, pages.Count, page.Source.DisplayName));
                }
                if (pdf is not null && !groupFailed && pdf.PageCount > 0)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (watermark.Enabled && watermark.Target == WatermarkTarget.Paper)
                    {
                        foreach (var sheet in pdf.Pages.Cast<PdfPage>())
                        {
                            var overlay = composer.CreateOverlay((int)Math.Round(layout.PageWidth * settings.Dpi / 72),
                                (int)Math.Round(layout.PageHeight * settings.Dpi / 72), watermark, settings.Color, cancellationToken);
                            using var overlayStream = new MemoryStream(overlay); using var image = XImage.FromStream(overlayStream);
                            using var graphics = XGraphics.FromPdfPage(sheet); graphics.DrawImage(image, 0, 0, layout.PageWidth, layout.PageHeight);
                        }
                    }
                    using var stream = new MemoryStream();
                    pdf.Save(stream, false);
                    var stem = settings.Organization == ExportOrganization.CombinedPdf ? "合并扫描件" : group[0].Source.Document.Name + "-扫描";
                    var pdfPath = await WriteAtomicAsync(settings.Folder, stem, ".pdf", stream.ToArray(), cancellationToken);
                    files.Add(pdfPath);
                    if (ocrOptions.ExportText)
                        files.Add(await WriteAtomicAsync(settings.Folder, Path.GetFileNameWithoutExtension(pdfPath), ".txt", System.Text.Encoding.UTF8.GetBytes(string.Join("\n\f\n", textPages)), cancellationToken));
                }
                // A failed PDF group is never saved with silently missing pages.
            }
            return new(files, failures, false);
        }
        catch (OperationCanceledException) { return new(files, failures, true); }
    }

    private sealed class RecognitionProgress(Action<OcrProgress> report) : IProgress<OcrProgress>
    {
        public void Report(OcrProgress value) => report(value);
    }

    private static async Task<string> WriteAtomicAsync(string folder, string stem, string extension,
        byte[] bytes, CancellationToken cancellationToken)
    {
        var temporary = System.IO.Path.Combine(folder, $".easyScan-{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            for (var n = 0; ; n++)
            {
                var path = System.IO.Path.Combine(folder, stem + (n == 0 ? "" : $" ({n})") + extension);
                try { File.Move(temporary, path, false); return path; }
                catch (IOException) when (File.Exists(path)) { }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string FriendlyError(Exception exception) => exception switch
    {
        UnauthorizedAccessException => "没有读取或写入权限，请选择其他位置。",
        FileNotFoundException => "找不到文件，请确认文件已下载到本机。",
        DllNotFoundException => "缺少运行组件，请保留便携包中的全部文件。",
        ImageMagick.MagickException => "图片无法读取，请确认文件完整并已下载到本机。",
        _ when exception.Message.Contains("password", StringComparison.OrdinalIgnoreCase) => "此 PDF 已加密，请先解密后导入。",
        IOException => "文件无法读取或写入，请检查文件是否被占用及磁盘空间。",
        ArgumentException => "设置无效，请检查保存位置和裁剪范围。",
        _ => exception.Message.Length > 180 ? exception.Message[..180] : exception.Message
    };
}
