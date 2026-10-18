using Clipper2Lib;
using OpenCvSharp;

namespace EasyScan.Core;

public sealed partial class OnnxOcrEngine
{
    private List<Point2f[]> Detect(Mat image, CancellationToken token)
    {
        var scale = Math.Min(1, 2000d / Math.Max(image.Width, image.Height));
        if (Math.Min(image.Width, image.Height) * scale < 736)
            scale = Math.Min(736d / Math.Min(image.Width, image.Height), 2000d / Math.Max(image.Width, image.Height));
        var w = Math.Max(32, (int)Math.Round(image.Width * scale / 32) * 32);
        var h = Math.Max(32, (int)Math.Round(image.Height * scale / 32) * 32);
        var result = Run(_detector!, Tensor(image, h, w, true, out _), token);
        var mapHeight = result.Shape[^2]; var mapWidth = result.Shape[^1];
        using var probabilities = new Mat(mapHeight, mapWidth, MatType.CV_32FC1); probabilities.SetArray(result.Data);
        using var bitmap = new Mat(); Cv2.Compare(probabilities, .3, bitmap, CmpTypes.GT);
        using var dilation = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(2, 2)); Cv2.Dilate(bitmap, bitmap, dilation);
        Cv2.FindContours(bitmap, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var boxes = new List<Point2f[]>();
        foreach (var contour in contours.OrderByDescending(x => Cv2.ContourArea(x)).Take(1000))
        {
            token.ThrowIfCancellationRequested();
            var rect = Cv2.MinAreaRect(contour);
            if (Math.Min(rect.Size.Width, rect.Size.Height) < 3) continue;
            var points = OrderBox(rect.Points());
            var bounds = Cv2.BoundingRect(points).Intersect(new Rect(0, 0, mapWidth, mapHeight));
            if (bounds.Width < 1 || bounds.Height < 1) continue;
            using var roi = new Mat(probabilities, bounds);
            using var mask = new Mat(bounds.Size, MatType.CV_8UC1, Scalar.All(0));
            Cv2.FillConvexPoly(mask, points.Select(p => new Point((int)p.X - bounds.X, (int)p.Y - bounds.Y)), Scalar.All(255));
            if (Cv2.Mean(roi, mask).Val0 < .5) continue;
            var path = new PathD(points.Select(p => new PointD(p.X, p.Y)));
            var perimeter = Enumerable.Range(0, 4).Sum(i => points[i].DistanceTo(points[(i + 1) % 4]));
            var distance = Math.Abs(Clipper.Area(path)) * 1.6 / Math.Max(1, perimeter);
            var expanded = Clipper.InflatePaths(new PathsD { path }, distance, JoinType.Round, EndType.Polygon);
            if (expanded.Count != 1 || expanded[0].Count < 4) continue;
            var outer = Cv2.MinAreaRect(expanded[0].Select(p => new Point2f((float)p.x, (float)p.y)));
            if (Math.Min(outer.Size.Width, outer.Size.Height) < 5) continue;
            var box = OrderBox(outer.Points()).Select(p => new Point2f(
                Math.Clamp(p.X / mapWidth * image.Width, 0, image.Width - 1),
                Math.Clamp(p.Y / mapHeight * image.Height, 0, image.Height - 1))).ToArray();
            boxes.Add(box);
        }
        // Stable row grouping adapts to text size instead of a fixed ten-pixel cutoff.
        var ordered = boxes.OrderBy(b => b.Min(p => p.Y)).ToList();
        var output = new List<Point2f[]>();
        while (ordered.Count > 0)
        {
            var first = ordered[0]; var y = first.Average(p => p.Y);
            var height = Math.Max(4, first[0].DistanceTo(first[3]));
            var row = ordered.Where(b => Math.Abs(b.Average(p => p.Y) - y) < height * .5).OrderBy(b => b.Min(p => p.X)).ToArray();
            output.AddRange(row); foreach (var item in row) ordered.Remove(item);
        }
        return output;
    }

    private static Point2f[] OrderBox(Point2f[] points)
    {
        var sorted = points.OrderBy(p => p.X).ToArray();
        var left = sorted.Take(2).OrderBy(p => p.Y).ToArray(); var right = sorted.Skip(2).OrderBy(p => p.Y).ToArray();
        return [left[0], right[0], right[1], left[1]];
    }
}
