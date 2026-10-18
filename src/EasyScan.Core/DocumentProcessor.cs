using OpenCvSharp;

namespace EasyScan.Core;

public sealed class DocumentProcessor
{
    public CropDetection DetectCrop(Mat image, CancellationToken cancellationToken = default)
        => new PaperDetector().Detect(image, cancellationToken);

    public PhotoQuality Analyze(Mat image) => PhotoAnalysis.Analyze(image);

    public Mat ProcessColor(Mat original, ScanSettings settings, CancellationToken cancellationToken = default)
    {
        using var prepared = Prepare(original, settings, cancellationToken);
        return prepared.Enhance(settings, cancellationToken);
    }

    internal PreparedPage Prepare(Mat original, ScanSettings settings, CancellationToken cancellationToken = default)
    {
        if (!settings.Crop.IsValid) throw new ArgumentException("裁剪区域无效，请重新调整四角。");
        cancellationToken.ThrowIfCancellationRequested();
        if (settings.CurvedEdges is { } edges && !edges.IsValidFor(settings.Crop))
            throw new ArgumentException("弯边范围无效，请重新调整。");
        using var cropped = new PageRectifier().Rectify(original, settings, cancellationToken);
        var rotated = new Mat();
        try
        {
            switch (((settings.QuarterTurns % 4) + 4) % 4)
            {
                case 1: Cv2.Rotate(cropped, rotated, RotateFlags.Rotate90Clockwise); break;
                case 2: Cv2.Rotate(cropped, rotated, RotateFlags.Rotate180); break;
                case 3: Cv2.Rotate(cropped, rotated, RotateFlags.Rotate90Counterclockwise); break;
                default: cropped.CopyTo(rotated); break;
            }
            return new PreparedPage(rotated);
        }
        catch { rotated.Dispose(); throw; }
    }

    public Mat ConvertColor(Mat color, OutputColorMode mode, double binaryStrength = .5, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (mode == OutputColorMode.Color) return color.Clone();
        var gray = new Mat();
        Cv2.CvtColor(color, gray, ColorConversionCodes.BGR2GRAY);
        if (mode == OutputColorMode.Grayscale) return gray;
        using (gray)
        {
            var output = new Mat();
            try
            {
                if (Math.Min(gray.Width, gray.Height) < 3) Cv2.Threshold(gray, output, 127, 255, ThresholdTypes.Binary);
                else
                {
                    output.Create(gray.Size(), MatType.CV_8UC1);
                    var block = Math.Min(Math.Clamp((int)Math.Round(51d * Math.Max(gray.Width, gray.Height) / 1600), 3, 201), Math.Min(gray.Width, gray.Height));
                    if (block % 2 == 0) block--;
                    // Keep the byte denoise plane, but bound the three floating-point statistic planes.
                    // Denoising once avoids recomputing bilateral halos for every stripe.
                    using var smoothFull = new Mat(); Cv2.BilateralFilter(gray, smoothFull, 5, 10, 2);
                    var halo = block / 2;
                    var imageHeight = gray.Height;
                    for (var y = 0; y < imageHeight; y += 1024)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var end = Math.Min(y + 1024, gray.Height);
                        var top = Math.Max(0, y - halo); var bottom = Math.Min(gray.Height, end + halo);
                        using var source = new Mat(gray, new Rect(0, top, gray.Width, bottom - top));
                        using var smooth = new Mat(smoothFull, new Rect(0, top, gray.Width, bottom - top));
                        using var light = new Mat(); using var mean = new Mat();
                        using var variance = new Mat();
                        // Direct byte box/squared-box statistics avoid a float image and square plane.
                        Cv2.BoxFilter(smooth, mean, MatType.CV_32FC1, new Size(block, block));
                        Cv2.SqrBoxFilter(smooth, variance, (int)MatType.CV_32FC1, new Size(block, block));
                        Cv2.Multiply(mean, mean, light);
                        Cv2.Subtract(variance, light, variance);
                        Cv2.Max(variance, 0, variance); Cv2.Sqrt(variance, variance);
                        Cv2.Multiply(variance, Scalar.All(1d / 128), variance);
                        Cv2.Subtract(variance, Scalar.All(1), variance);
                        Cv2.Multiply(variance, Scalar.All(.35 - .28 * Math.Clamp(binaryStrength, 0, 1)), variance);
                        Cv2.Add(variance, Scalar.All(1), variance);
                        Cv2.Multiply(mean, variance, mean);
                        source.ConvertTo(light, MatType.CV_32FC1);
                        using var binary = new Mat(); Cv2.Compare(light, mean, binary, CmpTypes.GT);
                        using var interior = new Mat(binary, new Rect(0, y - top, gray.Width, end - y));
                        using var destination = new Mat(output, new Rect(0, y, gray.Width, end - y));
                        interior.CopyTo(destination);
                    }
                }
                cancellationToken.ThrowIfCancellationRequested();
                return output;
            }
            catch { output.Dispose(); throw; }
        }
    }
}

/// <summary>Request-scoped geometry shared by scan and OCR. Never stored in a cache.</summary>
internal sealed class PreparedPage(Mat image) : IDisposable
{
    internal Mat Enhance(ScanSettings settings, CancellationToken token)
        => new PhotoEnhancer().Enhance(image, settings, token);
    public void Dispose() => image.Dispose();
}
