using OpenCvSharp;

namespace EasyScan.Core;

public sealed partial class PaperDetector
{
    private static bool Contains(Point[] outer, Point[] inner)
        => inner.All(p => Cv2.PointPolygonTest(outer, p, true) >= -4);

    private static void AddLineCandidates(Mat gray, Mat color, Mat lab, List<Candidate> candidates)
    {
        using var edge = new Mat(); Cv2.Canny(gray, edge, 30, 100);
        var shortSide = Math.Min(gray.Width, gray.Height);
        var lines = Cv2.HoughLinesP(edge, 1, Math.PI / 180, 35, shortSide * .14, shortSide * .025);
        using var mask = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.All(0));
        foreach (var line in lines.OrderByDescending(l => l.P1.DistanceTo(l.P2)).Take(40))
        {
            var dx = line.P2.X - line.P1.X; var dy = line.P2.Y - line.P1.Y;
            var length = Math.Max(1, Math.Sqrt(dx * dx + dy * dy));
            var extend = shortSide * .012;
            Cv2.Line(mask, new Point((int)(line.P1.X - dx / length * extend), (int)(line.P1.Y - dy / length * extend)),
                new Point((int)(line.P2.X + dx / length * extend), (int)(line.P2.Y + dy / length * extend)), Scalar.All(255), 2);
        }
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        Cv2.MorphologyEx(mask, mask, MorphTypes.Close, kernel);
        AddCandidates(mask, color, lab, candidates);
    }

    private static CropQuad RefineCorners(Mat original, CropQuad crop, CancellationToken token)
    {
        using var image = new Mat();
        var scale = Math.Min(1, 2400d / Math.Max(original.Width, original.Height));
        Cv2.Resize(original, image, new Size(), scale, scale, InterpolationFlags.Area);
        using var gray = new Mat(); Cv2.CvtColor(image, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, gray, new Size(3, 3), .6);
        var rows = gray.AsRows<byte>();
        var p = crop.Points.Select(x => new Point2d(x.X * (image.Width - 1), x.Y * (image.Height - 1))).ToArray();
        var lines = new Line2D[4];
        var radius = Math.Clamp(Math.Min(image.Width, image.Height) * .012, 4, 24);
        for (var side = 0; side < 4; side++)
        {
            token.ThrowIfCancellationRequested();
            var a = p[side]; var b = p[(side + 1) % 4]; var dx = b.X - a.X; var dy = b.Y - a.Y;
            var length = Math.Sqrt(dx * dx + dy * dy); var nx = -dy / length; var ny = dx / length;
            var samples = new List<Point2f>();
            for (var i = 4; i <= 36; i++)
            {
                var t = i / 40d; var x = a.X + t * dx; var y = a.Y + t * dy;
                var best = 5d; Point2f? chosen = null;
                for (var offset = -(int)radius; offset <= radius; offset++)
                {
                    var cx = x + offset * nx; var cy = y + offset * ny;
                    var ix = (int)Math.Round(cx + 2 * nx); var iy = (int)Math.Round(cy + 2 * ny);
                    var ox = (int)Math.Round(cx - 2 * nx); var oy = (int)Math.Round(cy - 2 * ny);
                    if (Math.Min(ix, ox) < 0 || Math.Min(iy, oy) < 0 || Math.Max(ix, ox) >= image.Width || Math.Max(iy, oy) >= image.Height) continue;
                    var score = Math.Abs(rows[iy][ix] - rows[oy][ox]) - Math.Abs(offset) * .65;
                    if (score > best) { best = score; chosen = new((float)cx, (float)cy); }
                }
                if (chosen.HasValue) samples.Add(chosen.Value);
            }
            if (samples.Count < 18) return crop;
            lines[side] = Cv2.FitLine(samples, DistanceTypes.Huber, 0, .01, .01);
        }
        var result = new NormalizedPoint[4];
        for (var i = 0; i < 4; i++)
        {
            var a = lines[(i + 3) % 4]; var b = lines[i];
            var cross = a.Vx * b.Vy - a.Vy * b.Vx;
            if (Math.Abs(cross) < .1) return crop;
            var t = ((b.X1 - a.X1) * b.Vy - (b.Y1 - a.Y1) * b.Vx) / cross;
            var x = a.X1 + t * a.Vx; var y = a.Y1 + t * a.Vy;
            if (Math.Sqrt(Math.Pow(x - p[i].X, 2) + Math.Pow(y - p[i].Y, 2)) > radius * 2) return crop;
            result[i] = new(Math.Clamp(x / (image.Width - 1), 0, 1), Math.Clamp(y / (image.Height - 1), 0, 1));
        }
        var refined = new CropQuad(result[0], result[1], result[2], result[3]);
        return refined.IsValid && Math.Abs(refined.Area - crop.Area) < .04 ? refined : crop;
    }
}
