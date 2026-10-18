using OpenCvSharp;

namespace EasyScan.Core;

public sealed class PageRectifier
{
    public Mat Rectify(Mat image, ScanSettings settings, CancellationToken token = default)
    {
        var crop = settings.Crop;
        if (crop == CropQuad.FullFrame && settings.CurvedEdges is null) return ApplyInset(image.Clone(), settings.EdgeInset);
        var p = crop.Points.Select(x => new Point2f((float)(x.X * (image.Width - 1)), (float)(x.Y * (image.Height - 1)))).ToArray();
        static double Distance(Point2f a, Point2f b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));
        var width = Math.Max(2, (int)Math.Round(Math.Max(Distance(p[0], p[1]), Distance(p[3], p[2]))));
        var height = Math.Max(2, (int)Math.Round(Math.Max(Distance(p[0], p[3]), Distance(p[1], p[2]))));
        Point2f[] destination = [new(0, 0), new(width - 1, 0), new(width - 1, height - 1), new(0, height - 1)];
        using var transform = Cv2.GetPerspectiveTransform(p, destination);
        var output = new Mat();
        var edges = settings.CurvedEdges;
        if (settings.FlattenCurvedEdges && edges is not null && edges.IsValidFor(crop) && edges.Deviation(crop) > .0005)
        {
            using var inverse = Cv2.GetPerspectiveTransform(destination, p);
            RemapCurves(image, output, crop, edges, inverse, width, height, token);
        }
        else Cv2.WarpPerspective(image, output, transform, new Size(width, height), InterpolationFlags.Cubic, BorderTypes.Replicate);
        return ApplyInset(output, settings.EdgeInset);
    }

    private static Mat ApplyInset(Mat output, double amount)
    {
        var width = output.Width; var height = output.Height;
        var inset = (int)Math.Round(Math.Min(width, height) * Math.Clamp(amount, 0, .03));
        if (inset > 0 && width > inset * 2 + 2 && height > inset * 2 + 2)
        {
            using var padded = output;
            using var interior = new Mat(padded, new Rect(inset, inset, width - inset * 2, height - inset * 2));
            return interior.Clone();
        }
        return output;
    }

    private static void RemapCurves(Mat image, Mat output, CropQuad crop, CurvedEdges curves,
        Mat inverse, int width, int height, CancellationToken token)
    {
        var h = Enumerable.Range(0, 9).Select(i => inverse.At<double>(i / 3, i % 3)).ToArray();
        var corners = crop.Points.Select(x => new Point2d(x.X * (image.Width - 1), x.Y * (image.Height - 1))).ToArray();
        var mids = curves.Points.Select(x => new Point2d(x.X * (image.Width - 1), x.Y * (image.Height - 1))).ToArray();
        var offsets = Enumerable.Range(0, 4).Select(i => new Point2d(mids[i].X - (corners[i].X + corners[(i + 1) % 4].X) / 2,
            mids[i].Y - (corners[i].Y + corners[(i + 1) % 4].Y) / 2)).ToArray();
        Point2d Project(double x, double y)
        {
            var z = h[6] * x + h[7] * y + h[8];
            return new((h[0] * x + h[1] * y + h[2]) / z, (h[3] * x + h[4] * y + h[5]) / z);
        }
        Point2d Offset(double x, double y, int side)
        {
            var a = corners[side]; var b = corners[(side + 1) % 4]; var point = Project(x, y);
            var dx = b.X - a.X; var dy = b.Y - a.Y;
            var t = Math.Clamp(((point.X - a.X) * dx + (point.Y - a.Y) * dy) / (dx * dx + dy * dy), 0, 1);
            var bow = 4 * t * (1 - t); return new(offsets[side].X * bow, offsets[side].Y * bow);
        }
        var top = Enumerable.Range(0, width).Select(x => Offset(x, 0, 0)).ToArray();
        var bottom = Enumerable.Range(0, width).Select(x => Offset(x, height - 1, 2)).ToArray();
        var left = Enumerable.Range(0, height).Select(y => Offset(0, y, 3)).ToArray();
        var right = Enumerable.Range(0, height).Select(y => Offset(width - 1, y, 1)).ToArray();
        using var mapX = new Mat(height, width, MatType.CV_32FC1);
        using var mapY = new Mat(height, width, MatType.CV_32FC1);
        var rowsX = mapX.AsRows<float>(); var rowsY = mapY.AsRows<float>();
        for (var y = 0; y < height; y++)
        {
            if (y % 64 == 0) token.ThrowIfCancellationRequested();
            var v = y / (double)(height - 1); var rowX = rowsX[y]; var rowY = rowsY[y];
            for (var x = 0; x < width; x++)
            {
                var u = x / (double)(width - 1); var point = Project(x, y);
                rowX[x] = (float)(point.X + top[x].X * (1 - v) + bottom[x].X * v + left[y].X * (1 - u) + right[y].X * u);
                rowY[x] = (float)(point.Y + top[x].Y * (1 - v) + bottom[x].Y * v + left[y].Y * (1 - u) + right[y].Y * u);
            }
        }
        token.ThrowIfCancellationRequested();
        Cv2.Remap(image, output, mapX, mapY, InterpolationFlags.Cubic, BorderTypes.Replicate);
    }
}
