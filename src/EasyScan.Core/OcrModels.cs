using OpenCvSharp;

namespace EasyScan.Core;

public sealed record OcrExportSettings(bool ExportText = false, bool SearchablePdf = false);
public sealed record OcrCharacter(string Text, double Confidence, NormalizedPoint[] Polygon);
public sealed record OcrTextBlock(string Text, double Confidence, NormalizedPoint[] Polygon, IReadOnlyList<OcrCharacter> Characters);
public sealed record OcrPageResult(int Width, int Height, IReadOnlyList<OcrTextBlock> Blocks)
{
    public string Text => string.Join(Environment.NewLine, Blocks.Select(x => x.Text));
}
public sealed record OcrProgress(string Stage, int Completed = 0, int Total = 0);

public interface IOcrEngine : IDisposable
{
    string Version { get; }
    Task<OcrPageResult> RecognizeAsync(Mat image, IProgress<OcrProgress>? progress = null, CancellationToken token = default);
}
