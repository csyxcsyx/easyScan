using OpenCvSharp;

namespace EasyScan.Core;

/// <summary>Owns corrected pixels for one request; shared by export and OCR, never cached.</summary>
internal sealed class PreparedPage(Mat image) : IDisposable
{
    internal Mat Enhance(ScanSettings settings, CancellationToken token)
        => new PhotoEnhancer().Enhance(image, settings, token);

    internal Mat EnhanceForOcr(ScanSettings settings, CancellationToken token)
        => Enhance(settings with { DetailEnhancement = 0, NoiseReduction = Math.Min(settings.NoiseReduction, .15) }, token);

    public void Dispose() => image.Dispose();
}
