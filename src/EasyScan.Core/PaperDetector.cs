using OpenCvSharp;

namespace EasyScan.Core;

public sealed partial class PaperDetector
{
    private sealed record Candidate(Point[] Quad, Point[] Contour, double Area, double Score, double Separation, int SupportedSides);

    public CropDetection Detect(Mat image, CancellationToken cancellationToken = default)
    {
        if (Math.Min(image.Width, image.Height) < 16) return new(CropQuad.FullFrame, .35, "请确认边界");
        using var small = new Mat();
        var scale = Math.Min(1, 1000d / Math.Max(image.Width, image.Height));
        Cv2.Resize(image, small, new Size(), scale, scale, InterpolationFlags.Area);
        using var gray = new Mat(); using var blur = new Mat(); using var local = new Mat();
        using var lab = new Mat(); Cv2.CvtColor(small, lab, ColorConversionCodes.BGR2Lab);
        Cv2.CvtColor(small, gray, ColorConversionCodes.BGR2GRAY);
        Cv2.GaussianBlur(gray, blur, new Size(5, 5), 0);
        using var clahe = Cv2.CreateCLAHE(2, new Size(8, 8)); clahe.Apply(blur, local);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Rect, new Size(5, 5));
        var candidates = new List<Candidate>();
        foreach (var thresholds in new[] { (18d, 60d), (45d, 135d) })
        {
            using var edges = new Mat();
            Cv2.Canny(local, edges, thresholds.Item1, thresholds.Item2);
            Cv2.MorphologyEx(edges, edges, MorphTypes.Close, kernel);
            AddCandidates(edges, small, lab, candidates);
        }
        AddLineCandidates(local, small, lab, candidates);
        var labChannels = Cv2.Split(lab);
        try
        {
            foreach (var channel in labChannels.Skip(1))
            {
                using var colorEdges = new Mat(); using var normalizedChroma = new Mat();
                Cv2.MeanStdDev(channel, out _, out var deviation);
                if (deviation.Val0 < 2) continue;
                Cv2.GaussianBlur(channel, normalizedChroma, new Size(5, 5), 0);
                Cv2.Normalize(normalizedChroma, normalizedChroma, 0, 255, NormTypes.MinMax);
                Cv2.Canny(normalizedChroma, colorEdges, 18, 60);
                Cv2.MorphologyEx(colorEdges, colorEdges, MorphTypes.Close, kernel);
                AddCandidates(colorEdges, small, lab, candidates);
            }
        }
        finally { foreach (var channel in labChannels) channel.Dispose(); }
        using (var threshold = new Mat())
        {
            Cv2.Threshold(blur, threshold, 0, 255, ThresholdTypes.Binary | ThresholdTypes.Otsu);
            Cv2.MorphologyEx(threshold, threshold, MorphTypes.Close, kernel);
            AddCandidates(threshold, small, lab, candidates);
            Cv2.BitwiseNot(threshold, threshold); AddCandidates(threshold, small, lab, candidates);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var best = candidates.OrderByDescending(x => x.Score).FirstOrDefault();
        if (best is null || best.Score < .40 || best.SupportedSides < 3 || best.Separation < 7 || (best.Separation < 18 && best.Area > .70))
            return new(CropQuad.FullFrame, .50, "边界不明确，请确认");
        var competing = candidates.Where(x => x != best && Math.Abs(x.Area - best.Area) > .12 && x.Score > best.Score - .025).FirstOrDefault();
        if (competing is not null && !Contains(best.Quad, competing.Quad) && !Contains(competing.Quad, best.Quad))
            return new(CropQuad.FullFrame, .55, "多个边界接近，请确认");
        if (best.Area > .70 && IsSamePaperOutside(small, best.Quad))
            return new(CropQuad.FullFrame, .58, "保留整页，请确认");
        if (best.Area > .97) return new(CropQuad.FullFrame, .68, "整页画面");

        var contour = RefineForeground(small, best, cancellationToken);
        var quad = best.Quad;
        // Preserve perspective corners; silhouette fitting supplies the curved edge midpoints.
        var normalized = quad.Select(p => Normalize(p, small)).ToArray();
        var crop = new CropQuad(normalized[0], normalized[1], normalized[2], normalized[3]);
        var curves = FitCurves(contour, quad, small);
        if (curves is not null && (!curves.IsValidFor(crop) || curves.Deviation(crop) < .004)) curves = null;
        if (curves is null) crop = RefineCorners(image, crop, cancellationToken);
        var confidence = Math.Clamp(.48 + best.Score * .48, .45, .95);
        return new(crop, confidence, curves is null ? "纸张边缘" : "纸张边缘 · 检出弯边", curves);
    }

    private static bool IsSamePaperOutside(Mat image, Point[] quad)
    {
        using var lab = new Mat(); using var mask = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.CvtColor(image, lab, ColorConversionCodes.BGR2Lab); Cv2.FillConvexPoly(mask, quad, Scalar.All(255));
        var pixels = lab.AsRows<Vec3b>(); var labels = mask.AsRows<byte>();
        var histograms = Enumerable.Range(0, 6).Select(_ => new int[256]).ToArray();
        var insideCount = 0; var outsideCount = 0; var width = image.Width; var height = image.Height;
        for (var y = 0; y < height; y += 2)
        {
            var row = pixels[y]; var flags = labels[y];
            for (var x = 0; x < width; x += 2)
            {
                var p = row[x];
                // Ignore colored ornaments and ink when comparing paper stock on either side.
                if (Math.Abs(p.Item1 - 128) + Math.Abs(p.Item2 - 128) > 24) continue;
                var offset = flags[x] > 0 ? 0 : 3;
                histograms[offset][p.Item0]++; histograms[offset + 1][p.Item1]++; histograms[offset + 2][p.Item2]++;
                if (offset == 0) insideCount++; else outsideCount++;
            }
        }
        if (insideCount < width * height * .025 || outsideCount < width * height * .008) return false;
        static double Median(int[] values, int count)
        {
            var sum = 0;
            for (var i = 0; i < values.Length; i++) { sum += values[i]; if (sum >= count / 2) return i; }
            return 255;
        }
        var light = Math.Abs(Median(histograms[0], insideCount) - Median(histograms[3], outsideCount));
        var chroma = Math.Abs(Median(histograms[1], insideCount) - Median(histograms[4], outsideCount))
            + Math.Abs(Median(histograms[2], insideCount) - Median(histograms[5], outsideCount));
        // Wide ornamental frames and uneven lighting can darken otherwise identical paper stock.
        // Prefer an explicit confirmation to losing content on a nearly full-frame document.
        return light < 45 && chroma < 10;
    }

    private static void AddCandidates(Mat mask, Mat color, Mat lab, List<Candidate> candidates)
    {
        Cv2.FindContours(mask, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxSimple);
        var total = color.Width * (double)color.Height;
        foreach (var contour in contours.OrderByDescending(x => Cv2.ContourArea(x)).Take(60))
        {
            var contourArea = Cv2.ContourArea(contour);
            if (contourArea / total < .07) continue;
            var hull = Cv2.ConvexHull(contour);
            foreach (var epsilon in new[] { .015, .025, .045, .065 })
            {
                var approx = Cv2.ApproxPolyDP(hull, epsilon * Cv2.ArcLength(hull, true), true);
                if (approx.Length != 4) continue;
                var quad = Order(approx);
                var p = quad.Select(x => Normalize(x, color)).ToArray();
                var crop = new CropQuad(p[0], p[1], p[2], p[3]);
                if (!crop.IsValid) continue;
                var area = Cv2.ContourArea(quad) / total;
                var lengths = Enumerable.Range(0, 4).Select(i => (double)(quad[i] - quad[(i + 1) % 4]).DistanceTo(new Point())).ToArray();
                if (lengths.Min() < 35 || lengths.Max() / lengths.Min() > 5) continue;
                if (candidates.Any(c => c.Quad.Zip(quad).Sum(x => (x.First - x.Second).DistanceTo(new Point())) < 12)) continue;
                var separation = EdgeSeparation(lab, quad, contour, out var support);
                var rectangularity = Math.Clamp(contourArea / Math.Max(1, Cv2.ContourArea(quad)), 0, 1);
                var borderTouches = quad.Count(x => x.X < color.Width * .01 || x.Y < color.Height * .01 || x.X > color.Width * .99 || x.Y > color.Height * .99);
                var score = .28 * Math.Sqrt(area) + .28 * rectangularity + .44 * Math.Min(1, separation / 40);
                if (borderTouches > 2) score -= .25;
                score -= (4 - support) * .035;
                candidates.Add(new(quad, contour, area, score, separation, support));
                break;
            }
        }
    }

    private static double EdgeSeparation(Mat image, Point[] quad, Point[] contour, out int supportedSides)
    {
        var pixels = image.AsRows<Vec3b>();
        var differences = new List<double>();
        supportedSides = 0;
        var distance = Math.Max(5, Math.Min(image.Width, image.Height) * .017);
        var fitted = FitCurves(contour, quad, image)!;
        var corners = quad.Select(p => Normalize(p, image)).ToArray();
        for (var side = 0; side < 4; side++)
        {
            var sideDifferences = new List<double>();
            var a = quad[side]; var b = quad[(side + 1) % 4];
            var length = Math.Sqrt(Math.Pow(b.X - a.X, 2) + Math.Pow(b.Y - a.Y, 2));
            var nx = -(b.Y - a.Y) / length; var ny = (b.X - a.X) / length;
            for (var i = 2; i <= 10; i++)
            {
                var t = i / 12d;
                var point = CurvedEdges.Evaluate(corners[side], corners[(side + 1) % 4], fitted.Points[side], t);
                var x = point.X * (image.Width - 1); var y = point.Y * (image.Height - 1);
                var ix = (int)Math.Round(x + nx * distance); var iy = (int)Math.Round(y + ny * distance);
                var ox = (int)Math.Round(x - nx * distance); var oy = (int)Math.Round(y - ny * distance);
                if (ix < 0 || iy < 0 || ox < 0 || oy < 0 || ix >= image.Width || ox >= image.Width || iy >= image.Height || oy >= image.Height) continue;
                var inside = pixels[iy][ix]; var outside = pixels[oy][ox];
                var difference = Math.Sqrt(Math.Pow(inside.Item0 - outside.Item0, 2) + 2 * Math.Pow(inside.Item1 - outside.Item1, 2) + 2 * Math.Pow(inside.Item2 - outside.Item2, 2));
                differences.Add(difference); sideDifferences.Add(difference);
            }
            if (sideDifferences.Count >= 4 && sideDifferences.Order().ElementAt(sideDifferences.Count / 2) >= 6) supportedSides++;
        }
        return differences.Count < 8 ? 0 : differences.Order().ElementAt(differences.Count / 2);
    }

    private static Point[] RefineForeground(Mat image, Candidate candidate, CancellationToken token)
    {
        if (candidate.Area > .94 || candidate.Separation < 11) return candidate.Contour;
        using var inside = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.FillConvexPoly(inside, candidate.Quad, Scalar.All(255));
        var distance = Math.Clamp((int)(Math.Min(image.Width, image.Height) * .055), 13, 55);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(distance * 2 + 1, distance * 2 + 1));
        using var outside = new Mat(); using var center = new Mat();
        Cv2.Dilate(inside, outside, kernel); Cv2.Erode(inside, center, kernel);
        using var labels = new Mat(image.Size(), MatType.CV_8UC1, Scalar.All(0));
        labels.SetTo(Scalar.All(2), outside); labels.SetTo(Scalar.All(3), inside); labels.SetTo(Scalar.All(1), center);
        using var bg = new Mat(); using var fg = new Mat();
        try
        {
            token.ThrowIfCancellationRequested();
            Cv2.GrabCut(image, labels, new Rect(), bg, fg, 2, GrabCutModes.InitWithMask);
            token.ThrowIfCancellationRequested();
            using var foreground = new Mat(); using var probable = new Mat();
            Cv2.Compare(labels, 1, foreground, CmpTypes.EQ); Cv2.Compare(labels, 3, probable, CmpTypes.EQ);
            Cv2.BitwiseOr(foreground, probable, foreground);
            Cv2.FindContours(foreground, out Point[][] contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxSimple);
            var contour = contours.OrderByDescending(x => Cv2.ContourArea(x)).FirstOrDefault();
            if (contour is null) return candidate.Contour;
            var area = Cv2.ContourArea(contour) / (image.Width * (double)image.Height);
            return Math.Abs(area - candidate.Area) < .15 ? contour : candidate.Contour;
        }
        catch (OpenCVException) { return candidate.Contour; }
    }

    private static CurvedEdges? FitCurves(Point[] contour, Point[] quad, Mat image)
    {
        var mids = new NormalizedPoint[4];
        for (var side = 0; side < 4; side++)
        {
            var a = quad[side]; var b = quad[(side + 1) % 4];
            var dx = b.X - a.X; var dy = b.Y - a.Y; var length = Math.Sqrt(dx * dx + dy * dy);
            var offsets = new List<double>();
            foreach (var point in contour)
            {
                var t = ((point.X - a.X) * dx + (point.Y - a.Y) * dy) / (length * length);
                var offset = ((point.X - a.X) * -dy + (point.Y - a.Y) * dx) / length;
                if (t is > .15 and < .85 && Math.Abs(offset) < length * .10)
                    offsets.Add(offset / (4 * t * (1 - t)));
            }
            var bow = offsets.Count > 3 ? offsets.Order().ElementAt(offsets.Count / 2) : 0;
            bow = Math.Clamp(bow, -length * .08, length * .08);
            mids[side] = new(Math.Clamp(((a.X + b.X) / 2d - dy / length * bow) / (image.Width - 1), 0, 1),
                Math.Clamp(((a.Y + b.Y) / 2d + dx / length * bow) / (image.Height - 1), 0, 1));
        }
        return new(mids[0], mids[1], mids[2], mids[3]);
    }

    private static NormalizedPoint Normalize(Point p, Mat image) => new(p.X / (double)(image.Width - 1), p.Y / (double)(image.Height - 1));
    private static Point[] Order(Point[] points)
    {
        var x = points.Average(p => p.X); var y = points.Average(p => p.Y);
        var sorted = points.OrderBy(p => Math.Atan2(p.Y - y, p.X - x)).ToArray();
        var first = Array.FindIndex(sorted, p => p.X + p.Y == sorted.Min(q => q.X + q.Y));
        return Enumerable.Range(0, 4).Select(i => sorted[(first + i) % 4]).ToArray();
    }
}
