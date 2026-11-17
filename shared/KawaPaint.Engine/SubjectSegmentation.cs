// KawaPaint - finds the subject(s) of an image and separates them from the background, with no
// trained model: GrabCut (Rother, Kolmogorov & Blake, SIGGRAPH 2004) on a reduced copy of the
// image, then the mask is scaled back up with soft edges.
//
// GrabCut models the foreground and the background colours as two Gaussian mixtures and finds
// the labelling that best fits them while keeping the boundary on strong colour edges (a graph
// cut, see GraphCut). It alternates: fit the mixtures to the current labels, cut, repeat. It
// only needs to be told roughly where the background is:
//   - with a hint (the user's selection), everything outside it is background for sure;
//   - without one, the outermost ring of pixels is, and the band along the borders, plus any
//     border-connected region of the border's own colours, starts out as probable background.
// Transparent pixels are always background, so an already cut-out image keeps its cut.
//
// It works on at most WorkingSize pixels per side: a 512 px copy is enough to find the subject,
// and keeps the run around a second even single-threaded in the browser. Everything is plain
// managed code (no native library, no model to download), so it behaves the same everywhere.

namespace KawaPaint.Engine;

public sealed class SubjectOptions
{
    /// <summary>Longest side of the reduced copy the segmentation runs on.</summary>
    public int WorkingSize { get; init; } = 512;

    /// <summary>GrabCut fit/cut rounds.</summary>
    public int Iterations { get; init; } = 5;

    /// <summary>Weight of the edge-following term against the colour models (GrabCut's gamma).</summary>
    public double Smoothness { get; init; } = 50;

    /// <summary>A subject smaller than this share of the image, or than <see cref="MinRelativeSize"/>
    /// of the largest subject, is dropped as noise.</summary>
    public double MinSubjectShare { get; init; } = 0.002;

    public double MinRelativeSize { get; init; } = 0.05;

    /// <summary>Background pockets inside a subject smaller than this share of the image are filled.</summary>
    public double MaxHoleShare { get; init; } = 0.0005;
}

/// <summary>The outcome of <see cref="SubjectSegmentation.Find"/>.</summary>
public sealed class SubjectMask
{
    internal SubjectMask(int width, int height, byte[] coverage, int lowWidth, int lowHeight, int[] labels, int[] componentAreas)
    {
        Width = width; Height = height; Coverage = coverage;
        LowWidth = lowWidth; LowHeight = lowHeight; _labels = labels; _componentAreas = componentAreas;
    }

    private readonly int[] _labels;          // per low-res pixel: 0 = background, k = subject k
    private readonly int[] _componentAreas;  // [k-1] = low-res area of subject k

    public int Width { get; }
    public int Height { get; }

    /// <summary>Full-size coverage, 0 = background, 255 = subject, in between on the edges.</summary>
    public byte[] Coverage { get; }

    internal int LowWidth { get; }
    internal int LowHeight { get; }

    /// <summary>Number of separate subjects found, largest first.</summary>
    public int SubjectCount => _componentAreas.Length;

    public bool IsEmpty => SubjectCount == 0;

    /// <summary>Full-size coverage of one subject (0-based, largest first), and its bounds.</summary>
    public (byte[] Coverage, int X, int Y, int W, int H) SubjectCoverage(int index)
    {
        if ((uint)index >= (uint)SubjectCount) throw new ArgumentOutOfRangeException(nameof(index));
        int label = index + 1;
        var low = new float[LowWidth * LowHeight];
        int minX = LowWidth, minY = LowHeight, maxX = -1, maxY = -1;
        for (int y = 0; y < LowHeight; y++)
            for (int x = 0; x < LowWidth; x++)
                if (_labels[y * LowWidth + x] == label)
                {
                    low[y * LowWidth + x] = 1f;
                    if (x < minX) minX = x; if (x > maxX) maxX = x;
                    if (y < minY) minY = y; if (y > maxY) maxY = y;
                }
        var full = SubjectSegmentation.Upsample(low, LowWidth, LowHeight, Width, Height,
            minX - 2, minY - 2, maxX + 2, maxY + 2, out var bounds);
        // Never wider than the combined mask: a subject's edge is the overall edge.
        for (int i = 0; i < full.Length; i++)
            if (full[i] > Coverage[i]) full[i] = Coverage[i];
        return (full, bounds.X, bounds.Y, bounds.W, bounds.H);
    }
}

public static class SubjectSegmentation
{
    private const int Components = 5;
    private const double HardCost = 1e9;

    /// <summary>
    /// Finds the subject(s) of <paramref name="source"/>. <paramref name="hint"/>, when given, is a
    /// coverage mask the size of the image (a selection): only what it covers can be a subject.
    /// </summary>
    public static SubjectMask Find(Surface source, ReadOnlySpan<byte> hint = default, SubjectOptions? options = null,
        CancellationToken cancel = default)
    {
        options ??= new SubjectOptions();
        int fullW = source.Width, fullH = source.Height;
        if (!hint.IsEmpty && hint.Length != fullW * fullH)
            throw new ArgumentException("The hint must match the image's size.", nameof(hint));

        double scale = Math.Min(1.0, (double)Math.Max(8, options.WorkingSize) / Math.Max(fullW, fullH));
        int w = Math.Max(1, (int)Math.Round(fullW * scale)), h = Math.Max(1, (int)Math.Round(fullH * scale));
        int n = w * h;

        // --- Reduced copy: colours, opacity, and the hint scaled the same way ----------------
        var color = new float[n * 3];
        var opaque = new bool[n];
        Surface small = scale < 1 ? source.Resized(w, h) : source;
        try { ReadPixels(small, color, opaque); }
        finally { if (!ReferenceEquals(small, source)) small.Dispose(); }

        // 0 = sure background, 1 = probable background, 2 = probable foreground, 3 = sure foreground
        var trimap = new byte[n];
        bool hinted = !hint.IsEmpty;
        if (hinted) SeedFromHint(hint, fullW, fullH, w, h, trimap);
        else SeedFromBorders(color, w, h, trimap);
        for (int i = 0; i < n; i++) if (!opaque[i]) trimap[i] = 0;

        var labels = new bool[n];   // true = foreground
        for (int i = 0; i < n; i++) labels[i] = trimap[i] >= 2;
        if (!HasBoth(labels, opaque)) return Empty(fullW, fullH, w, h);

        // --- GrabCut ------------------------------------------------------------------------
        var fg = new Gmm(); var bg = new Gmm();
        fg.InitWithKMeans(color, labels, opaque, wantForeground: true);
        bg.InitWithKMeans(color, labels, opaque, wantForeground: false);
        var neighbours = EdgeWeights(color, w, h, options.Smoothness);
        var componentOf = new int[n];

        for (int round = 0; round < options.Iterations; round++)
        {
            cancel.ThrowIfCancellationRequested();
            if (round > 0)
            {
                if (!HasBoth(labels, opaque)) break;
                fg.Refit(color, labels, opaque, componentOf, wantForeground: true);
                bg.Refit(color, labels, opaque, componentOf, wantForeground: false);
            }

            var graph = new GraphCut(n, n * 8);
            for (int i = 0; i < n; i++)
            {
                double toFg, toBg;   // source = foreground
                switch (trimap[i])
                {
                    case 0: toFg = 0; toBg = HardCost; break;
                    case 3: toFg = HardCost; toBg = 0; break;
                    default:
                        double r = color[i * 3], g = color[i * 3 + 1], b = color[i * 3 + 2];
                        toFg = -Math.Log(Math.Max(bg.Probability(r, g, b), 1e-300));   // cost of calling it background
                        toBg = -Math.Log(Math.Max(fg.Probability(r, g, b), 1e-300));   // cost of calling it foreground
                        break;
                }
                graph.AddTerminalWeights(i, toFg, toBg);
            }
            AddNeighbourEdges(graph, neighbours, w, h);
            graph.Solve();

            bool changed = false;
            for (int i = 0; i < n; i++)
            {
                if (trimap[i] is 0 or 3) continue;
                bool isFg = graph.IsSourceSide(i);
                if (isFg != labels[i]) { labels[i] = isFg; changed = true; }
            }
            if (!changed && round > 0) break;
        }

        // --- Clean-up: drop specks, fill pinholes, number the subjects ---------------------
        var (componentLabels, areas) = CleanUp(labels, w, h, options);
        if (areas.Length == 0) return Empty(fullW, fullH, w, h);

        // The sure-background ring cannot be a subject; a subject that runs off the image edge
        // would lose that line, so it borrows the label of the pixel just inside.
        if (!hinted) ExtendToEdges(componentLabels, w, h);

        var lowMask = new float[n];
        for (int i = 0; i < n; i++) lowMask[i] = componentLabels[i] > 0 ? 1f : 0f;
        var coverage = Upsample(lowMask, w, h, fullW, fullH, 0, 0, w - 1, h - 1, out _);

        // Transparent pixels stay out, and so does anything the hint leaves out.
        unsafe
        {
            for (int y = 0; y < fullH; y++)
            {
                ColorBgra* row = (ColorBgra*)source.GetRowPointer(y);
                int o = y * fullW;
                for (int x = 0; x < fullW; x++)
                {
                    if (row[x].A == 0) coverage[o + x] = 0;
                    else if (hinted && hint[o + x] < coverage[o + x]) coverage[o + x] = hint[o + x];
                }
            }
        }
        return new SubjectMask(fullW, fullH, coverage, w, h, componentLabels, areas);
    }

    /// <summary>Applies a mask to a selection: replaces it with the subject coverage.</summary>
    public static void ApplyTo(Selection selection, byte[] coverage)
    {
        if (coverage.Length != selection.Width * selection.Height)
            throw new ArgumentException("The mask must match the selection's size.", nameof(coverage));
        selection.Restore(coverage, active: true);
    }

    /// <summary>Multiplies each pixel's opacity by the coverage: the background becomes transparent.</summary>
    public static unsafe void ApplyAsAlpha(Surface surface, byte[] coverage)
    {
        int w = surface.Width;
        for (int y = 0; y < surface.Height; y++)
        {
            ColorBgra* row = (ColorBgra*)surface.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                byte c = coverage[y * w + x];
                if (c == 255) continue;
                if (c == 0) { row[x] = ColorBgra.Transparent; continue; }
                row[x].A = (byte)((row[x].A * c + 127) / 255);
            }
        }
    }

    // ---------------------------------------------------------------------------------------

    private static SubjectMask Empty(int fullW, int fullH, int w, int h)
        => new(fullW, fullH, new byte[fullW * fullH], w, h, new int[w * h], []);

    private static bool HasBoth(bool[] labels, bool[] opaque)
    {
        bool anyFg = false, anyBg = false;
        for (int i = 0; i < labels.Length && !(anyFg && anyBg); i++)
        {
            if (!opaque[i]) continue;
            if (labels[i]) anyFg = true; else anyBg = true;
        }
        return anyFg;   // background may be all transparent; the models then fall back
    }

    private static unsafe void ReadPixels(Surface s, float[] color, bool[] opaque)
    {
        int w = s.Width;
        for (int y = 0; y < s.Height; y++)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                var c = row[x];
                color[i * 3] = c.R; color[i * 3 + 1] = c.G; color[i * 3 + 2] = c.B;
                opaque[i] = c.A >= 128;
            }
        }
    }

    private static void SeedFromHint(ReadOnlySpan<byte> hint, int fullW, int fullH, int w, int h, byte[] trimap)
    {
        // A low-res pixel can be a subject if any full-res pixel it stands for is in the hint.
        for (int y = 0; y < h; y++)
        {
            int y0 = y * fullH / h, y1 = Math.Max(y0 + 1, (y + 1) * fullH / h);
            for (int x = 0; x < w; x++)
            {
                int x0 = x * fullW / w, x1 = Math.Max(x0 + 1, (x + 1) * fullW / w);
                bool any = false;
                for (int yy = y0; yy < y1 && !any; yy++)
                    for (int xx = x0; xx < x1; xx++)
                        if (hint[yy * fullW + xx] != 0) { any = true; break; }
                trimap[y * w + x] = any ? (byte)2 : (byte)0;
            }
        }
    }

    private static void SeedFromBorders(float[] color, int w, int h, byte[] trimap)
    {
        Array.Fill(trimap, (byte)2);
        var ring = new List<int>();
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) ring.Add(y * w + x);
        if (w < 3 || h < 3) { foreach (int i in ring) trimap[i] = 0; return; }

        // The frame's typical colours, grouped so that one backdrop split by noise or a gradient
        // into several clusters counts as one colour.
        var centers = KMeans(color, ring, Math.Min(Components, ring.Count), 8);
        double spread = 0;
        foreach (int i in ring) spread += NearestDistance2(color, i, centers);
        double threshold = Math.Clamp(spread / ring.Count * 4, 15 * 15, 40 * 40);
        double groupThreshold = Math.Max(4 * threshold, 40 * 40);
        var group = new int[centers.Length];
        for (int c = 0; c < centers.Length; c++) group[c] = c;
        int Root(int c) { while (group[c] != c) c = group[c] = group[group[c]]; return c; }
        for (int a = 0; a < centers.Length; a++)
            for (int b = a + 1; b < centers.Length; b++)
            {
                double dr = centers[a][0] - centers[b][0], dg = centers[a][1] - centers[b][1], db = centers[a][2] - centers[b][2];
                if (dr * dr + dg * dg + db * db <= groupThreshold) group[Root(a)] = Root(b);
            }

        // A backdrop shows on most of the frame; a subject cut by the frame shows on one side or
        // two. A colour group is background if it fills a fair share of at least three sides, or
        // most of the frame. Ring pixels of other colours are left to the colour models.
        var sideCount = new int[centers.Length, 4];
        var total = new int[centers.Length];
        var ringGroup = new int[ring.Count];
        for (int r = 0; r < ring.Count; r++)
        {
            int i = ring[r], x = i % w, y = i / w;
            int g = NearestDistance2(color, i, centers) <= threshold ? Root(Nearest(color, i, centers)) : -1;
            ringGroup[r] = g;
            if (g < 0) continue;
            total[g]++;
            if (y == 0) sideCount[g, 0]++;
            if (y == h - 1) sideCount[g, 1]++;
            if (x == 0) sideCount[g, 2]++;
            if (x == w - 1) sideCount[g, 3]++;
        }
        var isBackdrop = new bool[centers.Length];
        bool any = false;
        for (int g = 0; g < centers.Length; g++)
        {
            if (Root(g) != g) continue;
            int sides = 0;
            for (int side = 0; side < 4; side++)
                if (sideCount[g, side] >= 0.15 * (side < 2 ? w : h)) sides++;
            isBackdrop[g] = sides >= 3 || total[g] >= 0.6 * ring.Count;
            any |= isBackdrop[g];
        }

        var queue = new Queue<int>();
        var seen = new bool[w * h];
        var backdropCenters = new List<double[]>();
        for (int c = 0; c < centers.Length; c++) if (!any || isBackdrop[Root(c)]) backdropCenters.Add(centers[c]);
        var backdrop = backdropCenters.ToArray();
        for (int r = 0; r < ring.Count; r++)
        {
            int i = ring[r];
            seen[i] = true;
            if (any && (ringGroup[r] < 0 || !isBackdrop[ringGroup[r]])) continue;
            trimap[i] = 0;
            queue.Enqueue(i);
        }

        // Then every region joined to that backdrop that keeps to its colours starts out as
        // probable background - like a magic wand clicked all around the frame.
        while (queue.Count > 0)
        {
            int i = queue.Dequeue();
            int x = i % w, y = i / w;
            for (int k = 0; k < 4; k++)
            {
                int nx = x + (k == 0 ? 1 : k == 1 ? -1 : 0), ny = y + (k == 2 ? 1 : k == 3 ? -1 : 0);
                if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                int j = ny * w + nx;
                if (seen[j]) continue;
                seen[j] = true;
                if (NearestDistance2(color, j, backdrop) > threshold) continue;
                trimap[j] = 1;
                queue.Enqueue(j);
            }
        }
    }

    private static double NearestDistance2(float[] color, int i, double[][] centers, int count = -1)
    {
        double best = double.MaxValue;
        if (count < 0) count = centers.Length;
        for (int k = 0; k < count; k++)
        {
            var c = centers[k];
            double dr = color[i * 3] - c[0], dg = color[i * 3 + 1] - c[1], db = color[i * 3 + 2] - c[2];
            double d = dr * dr + dg * dg + db * db;
            if (d < best) best = d;
        }
        return best;
    }

    /// <summary>Plain k-means on the given pixels, seeded deterministically (k-means++ with a
    /// fixed seed) so the same image always gives the same result.</summary>
    internal static double[][] KMeans(float[] color, IReadOnlyList<int> pixels, int k, int rounds)
    {
        if (pixels.Count == 0 || k <= 0) return [];
        // Large sets are subsampled: the centres barely move, the time does.
        var sample = pixels;
        if (pixels.Count > 20000)
        {
            var picked = new List<int>(20000);
            double step = pixels.Count / 20000.0;
            for (double p = 0; p < pixels.Count && picked.Count < 20000; p += step) picked.Add(pixels[(int)p]);
            sample = picked;
        }
        k = Math.Min(k, sample.Count);
        var rng = new Random(1);
        var centers = new double[k][];
        int first = sample[rng.Next(sample.Count)];
        centers[0] = [color[first * 3], color[first * 3 + 1], color[first * 3 + 2]];
        var d2 = new double[sample.Count];
        for (int c = 1; c < k; c++)
        {
            double total = 0;
            for (int s = 0; s < sample.Count; s++)
            {
                d2[s] = NearestDistance2(color, sample[s], centers, c);
                total += d2[s];
            }
            int chosen = sample[rng.Next(sample.Count)];
            if (total > 0)
            {
                double target = rng.NextDouble() * total;
                for (int s = 0; s < sample.Count; s++)
                {
                    target -= d2[s];
                    if (target <= 0) { chosen = sample[s]; break; }
                }
            }
            centers[c] = [color[chosen * 3], color[chosen * 3 + 1], color[chosen * 3 + 2]];
        }

        var sums = new double[k * 3];
        var counts = new int[k];
        for (int round = 0; round < rounds; round++)
        {
            Array.Clear(sums); Array.Clear(counts);
            foreach (int i in sample)
            {
                int best = Nearest(color, i, centers);
                sums[best * 3] += color[i * 3]; sums[best * 3 + 1] += color[i * 3 + 1]; sums[best * 3 + 2] += color[i * 3 + 2];
                counts[best]++;
            }
            for (int c = 0; c < k; c++)
                if (counts[c] > 0)
                    centers[c] = [sums[c * 3] / counts[c], sums[c * 3 + 1] / counts[c], sums[c * 3 + 2] / counts[c]];
        }
        return centers;
    }

    private static int Nearest(float[] color, int i, double[][] centers)
    {
        int best = 0; double bestD = double.MaxValue;
        for (int c = 0; c < centers.Length; c++)
        {
            double dr = color[i * 3] - centers[c][0], dg = color[i * 3 + 1] - centers[c][1], db = color[i * 3 + 2] - centers[c][2];
            double d = dr * dr + dg * dg + db * db;
            if (d < bestD) { bestD = d; best = c; }
        }
        return best;
    }

    /// <summary>Edge weights to the right, down, down-right and down-left neighbours (4 per pixel),
    /// high across flat colour and low across edges, so the cut prefers to follow edges.</summary>
    private static float[] EdgeWeights(float[] color, int w, int h, double gamma)
    {
        var weights = new float[w * h * 4];
        double sum = 0; long count = 0;
        Span<(int dx, int dy)> offsets = [(1, 0), (0, 1), (1, 1), (-1, 1)];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + offsets[k].dx, ny = y + offsets[k].dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    sum += Diff2(color, y * w + x, ny * w + nx);
                    count++;
                }
        double beta = count == 0 || sum <= 0 ? 0 : 1.0 / (2.0 * sum / count);
        double diagonal = gamma / Math.Sqrt(2);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                for (int k = 0; k < 4; k++)
                {
                    int nx = x + offsets[k].dx, ny = y + offsets[k].dy;
                    if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                    double g = k < 2 ? gamma : diagonal;
                    weights[(y * w + x) * 4 + k] = (float)(g * Math.Exp(-beta * Diff2(color, y * w + x, ny * w + nx)));
                }
        return weights;
    }

    private static double Diff2(float[] color, int a, int b)
    {
        double dr = color[a * 3] - color[b * 3], dg = color[a * 3 + 1] - color[b * 3 + 1], db = color[a * 3 + 2] - color[b * 3 + 2];
        return dr * dr + dg * dg + db * db;
    }

    private static void AddNeighbourEdges(GraphCut graph, float[] weights, int w, int h)
    {
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (x + 1 < w) graph.AddEdge(i, i + 1, weights[i * 4], weights[i * 4]);
                if (y + 1 < h)
                {
                    graph.AddEdge(i, i + w, weights[i * 4 + 1], weights[i * 4 + 1]);
                    if (x + 1 < w) graph.AddEdge(i, i + w + 1, weights[i * 4 + 2], weights[i * 4 + 2]);
                    if (x > 0) graph.AddEdge(i, i + w - 1, weights[i * 4 + 3], weights[i * 4 + 3]);
                }
            }
    }

    private static (int[] Labels, int[] Areas) CleanUp(bool[] fg, int w, int h, SubjectOptions options)
    {
        int n = w * h;
        // Pinholes: background pockets that don't reach the edge and are tiny.
        var (bgLabels, bgAreas, bgTouches) = Label(fg, w, h, wantForeground: false);
        int maxHole = (int)Math.Ceiling(n * options.MaxHoleShare);
        for (int i = 0; i < n; i++)
        {
            int l = bgLabels[i];
            if (l > 0 && !bgTouches[l - 1] && bgAreas[l - 1] <= maxHole) fg[i] = true;
        }

        var (labels, areas, _) = Label(fg, w, h, wantForeground: true);
        int largest = areas.Length == 0 ? 0 : areas.Max();
        double minArea = Math.Max(1, Math.Max(n * options.MinSubjectShare, largest * options.MinRelativeSize));
        // Keep the big enough components, renumbered largest first.
        var order = Enumerable.Range(0, areas.Length).Where(c => areas[c] >= minArea)
            .OrderByDescending(c => areas[c]).ToArray();
        var remap = new int[areas.Length + 1];
        for (int r = 0; r < order.Length; r++) remap[order[r] + 1] = r + 1;
        for (int i = 0; i < n; i++) labels[i] = remap[labels[i]];
        return (labels, order.Select(c => areas[c]).ToArray());
    }

    /// <summary>8-connected components of the pixels whose flag equals <paramref name="wantForeground"/>.</summary>
    private static (int[] Labels, int[] Areas, bool[] TouchesEdge) Label(bool[] fg, int w, int h, bool wantForeground)
    {
        var labels = new int[w * h];
        var areas = new List<int>();
        var touches = new List<bool>();
        var stack = new Stack<int>();
        for (int start = 0; start < labels.Length; start++)
        {
            if (fg[start] != wantForeground || labels[start] != 0) continue;
            int label = areas.Count + 1, area = 0;
            bool touch = false;
            labels[start] = label;
            stack.Push(start);
            while (stack.Count > 0)
            {
                int i = stack.Pop();
                area++;
                int x = i % w, y = i / w;
                if (x == 0 || y == 0 || x == w - 1 || y == h - 1) touch = true;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        int j = ny * w + nx;
                        if (fg[j] != wantForeground || labels[j] != 0) continue;
                        labels[j] = label;
                        stack.Push(j);
                    }
            }
            areas.Add(area);
            touches.Add(touch);
        }
        return (labels, areas.ToArray(), touches.ToArray());
    }

    private static void ExtendToEdges(int[] labels, int w, int h)
    {
        if (w < 3 || h < 3) return;
        for (int x = 1; x < w - 1; x++)
        {
            labels[x] = labels[w + x];
            labels[(h - 1) * w + x] = labels[(h - 2) * w + x];
        }
        for (int y = 0; y < h; y++)
        {
            labels[y * w] = labels[y * w + 1];
            labels[y * w + w - 1] = labels[y * w + w - 2];
        }
    }

    /// <summary>
    /// Scales a 0..1 low-res mask up to full size: a light blur, bilinear interpolation, then a
    /// contrast stretch so the edge ends up a couple of pixels soft rather than blurry or stepped.
    /// Only the low-res box (lx0..lx1, ly0..ly1) is computed; the rest stays 0.
    /// </summary>
    internal static byte[] Upsample(float[] low, int w, int h, int fullW, int fullH,
        int lx0, int ly0, int lx1, int ly1, out (int X, int Y, int W, int H) bounds)
    {
        var result = new byte[fullW * fullH];
        lx0 = Math.Clamp(lx0, 0, w - 1); ly0 = Math.Clamp(ly0, 0, h - 1);
        lx1 = Math.Clamp(lx1, 0, w - 1); ly1 = Math.Clamp(ly1, 0, h - 1);
        if (lx1 < lx0 || ly1 < ly0) { bounds = (0, 0, 0, 0); return result; }

        // 3x3 blur, so the bilinear surface has rounded rather than stair-stepped contours.
        var smooth = new float[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float s = 0; int c = 0;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                        s += low[ny * w + nx] * (dx == 0 && dy == 0 ? 4 : dx == 0 || dy == 0 ? 2 : 1);
                        c += dx == 0 && dy == 0 ? 4 : dx == 0 || dy == 0 ? 2 : 1;
                    }
                smooth[y * w + x] = s / c;
            }

        double sx = (double)w / fullW, sy = (double)h / fullH;
        int fx0 = Math.Max(0, (int)Math.Floor(lx0 / sx)), fy0 = Math.Max(0, (int)Math.Floor(ly0 / sy));
        int fx1 = Math.Min(fullW - 1, (int)Math.Ceiling((lx1 + 1) / sx)), fy1 = Math.Min(fullH - 1, (int)Math.Ceiling((ly1 + 1) / sy));
        // How wide the soft edge is, in full-res pixels: about 1.5 px whatever the scale.
        double scale = Math.Max(1.0, 1.0 / Math.Min(sx, sy));
        double half = Math.Min(0.5, 0.75 / scale + 0.15);
        double lo = 0.5 - half, hi = 0.5 + half;

        int minX = fullW, minY = fullH, maxX = -1, maxY = -1;
        for (int y = fy0; y <= fy1; y++)
        {
            double ly = (y + 0.5) * sy - 0.5;
            int y0 = Math.Clamp((int)Math.Floor(ly), 0, h - 1), y1 = Math.Min(y0 + 1, h - 1);
            double ty = Math.Clamp(ly - y0, 0, 1);
            for (int x = fx0; x <= fx1; x++)
            {
                double lxp = (x + 0.5) * sx - 0.5;
                int x0 = Math.Clamp((int)Math.Floor(lxp), 0, w - 1), x1 = Math.Min(x0 + 1, w - 1);
                double tx = Math.Clamp(lxp - x0, 0, 1);
                double v = (smooth[y0 * w + x0] * (1 - tx) + smooth[y0 * w + x1] * tx) * (1 - ty)
                         + (smooth[y1 * w + x0] * (1 - tx) + smooth[y1 * w + x1] * tx) * ty;
                double t = Math.Clamp((v - lo) / (hi - lo), 0, 1);
                t = t * t * (3 - 2 * t);
                byte b = (byte)Math.Round(t * 255);
                if (b == 0) continue;
                result[y * fullW + x] = b;
                if (x < minX) minX = x; if (x > maxX) maxX = x;
                if (y < minY) minY = y; if (y > maxY) maxY = y;
            }
        }
        bounds = maxX < 0 ? (0, 0, 0, 0) : (minX, minY, maxX - minX + 1, maxY - minY + 1);
        return result;
    }

    /// <summary>A Gaussian mixture over RGB with full covariances, GrabCut's colour model.</summary>
    private sealed class Gmm
    {
        private readonly double[] _weight = new double[Components];
        private readonly double[][] _mean = new double[Components][];
        private readonly double[][] _inverse = new double[Components][];   // 3x3 row-major
        private readonly double[] _norm = new double[Components];          // weight / sqrt((2pi)^3 det)
        private int _count;

        public void InitWithKMeans(float[] color, bool[] labels, bool[] opaque, bool wantForeground)
        {
            var pixels = Pixels(labels, opaque, wantForeground);
            var centers = KMeans(color, pixels, Components, 10);
            var assignment = new int[labels.Length];
            foreach (int i in pixels) assignment[i] = Nearest(color, i, centers);
            Fit(color, pixels, assignment, centers.Length);
        }

        /// <summary>Re-estimates the mixture: each pixel goes to its most likely component.</summary>
        public void Refit(float[] color, bool[] labels, bool[] opaque, int[] assignment, bool wantForeground)
        {
            var pixels = Pixels(labels, opaque, wantForeground);
            if (_count == 0) { InitWithKMeans(color, labels, opaque, wantForeground); return; }
            foreach (int i in pixels)
            {
                double r = color[i * 3], g = color[i * 3 + 1], b = color[i * 3 + 2];
                int best = 0; double bestP = -1;
                for (int k = 0; k < _count; k++)
                {
                    double p = Component(k, r, g, b);
                    if (p > bestP) { bestP = p; best = k; }
                }
                assignment[i] = best;
            }
            Fit(color, pixels, assignment, _count);
        }

        private static List<int> Pixels(bool[] labels, bool[] opaque, bool wantForeground)
        {
            var list = new List<int>();
            for (int i = 0; i < labels.Length; i++)
                if (opaque[i] && labels[i] == wantForeground) list.Add(i);
            return list;
        }

        private void Fit(float[] color, List<int> pixels, int[] assignment, int k)
        {
            _count = 0;
            if (pixels.Count == 0 || k == 0) return;
            var sums = new double[k * 3];
            var prods = new double[k * 9];
            var counts = new int[k];
            foreach (int i in pixels)
            {
                int c = assignment[i];
                double r = color[i * 3], g = color[i * 3 + 1], b = color[i * 3 + 2];
                sums[c * 3] += r; sums[c * 3 + 1] += g; sums[c * 3 + 2] += b;
                prods[c * 9 + 0] += r * r; prods[c * 9 + 1] += r * g; prods[c * 9 + 2] += r * b;
                prods[c * 9 + 4] += g * g; prods[c * 9 + 5] += g * b; prods[c * 9 + 8] += b * b;
                counts[c]++;
            }
            for (int c = 0; c < k; c++)
            {
                if (counts[c] == 0) continue;
                double inv = 1.0 / counts[c];
                double mr = sums[c * 3] * inv, mg = sums[c * 3 + 1] * inv, mb = sums[c * 3 + 2] * inv;
                // Covariance, with a small ridge so flat-coloured clusters stay invertible.
                const double ridge = 0.01 * 255;
                double a00 = prods[c * 9 + 0] * inv - mr * mr + ridge;
                double a01 = prods[c * 9 + 1] * inv - mr * mg;
                double a02 = prods[c * 9 + 2] * inv - mr * mb;
                double a11 = prods[c * 9 + 4] * inv - mg * mg + ridge;
                double a12 = prods[c * 9 + 5] * inv - mg * mb;
                double a22 = prods[c * 9 + 8] * inv - mb * mb + ridge;
                double det = a00 * (a11 * a22 - a12 * a12) - a01 * (a01 * a22 - a12 * a02) + a02 * (a01 * a12 - a11 * a02);
                if (det <= 1e-12) continue;
                int slot = _count++;
                _mean[slot] = [mr, mg, mb];
                _inverse[slot] =
                [
                    (a11 * a22 - a12 * a12) / det, (a02 * a12 - a01 * a22) / det, (a01 * a12 - a02 * a11) / det,
                    (a02 * a12 - a01 * a22) / det, (a00 * a22 - a02 * a02) / det, (a01 * a02 - a00 * a12) / det,
                    (a01 * a12 - a02 * a11) / det, (a01 * a02 - a00 * a12) / det, (a00 * a11 - a01 * a01) / det,
                ];
                _weight[slot] = (double)counts[c] / pixels.Count;
                _norm[slot] = _weight[slot] / Math.Sqrt(Math.Pow(2 * Math.PI, 3) * det);
            }
        }

        private double Component(int k, double r, double g, double b)
        {
            double dr = r - _mean[k][0], dg = g - _mean[k][1], db = b - _mean[k][2];
            var m = _inverse[k];
            double q = dr * (m[0] * dr + m[1] * dg + m[2] * db)
                     + dg * (m[3] * dr + m[4] * dg + m[5] * db)
                     + db * (m[6] * dr + m[7] * dg + m[8] * db);
            return _norm[k] * Math.Exp(-0.5 * q);
        }

        public double Probability(double r, double g, double b)
        {
            if (_count == 0) return 1e-12;   // no model (e.g. an all-transparent background): stay neutral
            double p = 0;
            for (int k = 0; k < _count; k++) p += Component(k, r, g, b);
            return p;
        }
    }
}
