// KawaPaint - Remove Background: the subject GrabCut finds (SubjectSegmentation), then a clean-up
// at full size that GrabCut cannot do on its reduced copy:
//
//   - pockets of the background colour left inside the subject (the inside of letters, the gap
//     between two lines, specks of paper showing through a stamp) are cleared when they stand
//     out sharply from what surrounds them. GrabCut keeps them because they are cut off from the
//     borders and its smoothness term favours one solid shape;
//   - the edges get their opacity from the colours themselves: an edge pixel is read as a mix
//     of the background colour and the nearby subject colour, which gives the share of each
//     (its alpha), and the background's share is taken out of its colour (decontamination), so
//     no pale fringe is left around a subject cut out of a white page.
//
// That clean-up only runs when the background colours are known for sure: picked by the user
// with the eyedropper, or found on the borders and flat (a plain colour, or a fake-transparency
// checkerboard). A photo's backdrop shares too many colours with the subject to clear pockets
// on colour alone, so there the GrabCut result is used as it is.
//
// "Colors only" skips GrabCut: every pixel of the background colours goes (joined to the borders,
// or everywhere), with the same soft edges - a global magic eraser.

namespace KawaPaint.Engine;

/// <summary>The background colours of an image, as found on its borders or picked by the user.</summary>
public sealed record Backdrop(ColorBgra[] Colors, double Tolerance, bool Flat, bool UserChosen)
{
    /// <summary>Tolerance for picked colours when none is given: JPEG noise on a white page stays
    /// within it, the anti-aliased edge of a subject does not.</summary>
    public const double DefaultUserTolerance = 30;

    public static Backdrop None { get; } = new([], 0, false, false);

    /// <summary>True when its colours are reliable enough to clear the background on colour alone.</summary>
    public bool Usable => Colors.Length > 0 && (Flat || UserChosen);
}

public enum BackgroundMode
{
    /// <summary>Find the subject (GrabCut), then clean the background colours out of it.</summary>
    Subject,
    /// <summary>Remove the background colours, without looking for a subject.</summary>
    ColorsOnly
}

public sealed record BackgroundOptions
{
    public BackgroundMode Mode { get; init; } = BackgroundMode.Subject;

    /// <summary>Background colours picked by the user. Empty: found on the borders.</summary>
    public IReadOnlyList<ColorBgra> Colors { get; init; } = [];

    /// <summary>0..100: how different from the background colours a pixel may be and still be
    /// removed. Null: worked out from the image (<see cref="BackgroundRemoval.AutoTolerance"/>).</summary>
    public double? Tolerance { get; init; }

    /// <summary>Also clear background-coloured areas enclosed by the subject.</summary>
    public bool RemoveEnclosed { get; init; } = true;

    /// <summary>Take the background colour out of the semi-transparent edge pixels.</summary>
    public bool CleanEdges { get; init; } = true;

    /// <summary>Pulls the edge in by this many pixels (0..10), to drop a remaining halo.</summary>
    public int ShrinkEdge { get; init; }

    /// <summary>Softens the edge over this many pixels (0..10).</summary>
    public int Feather { get; init; }

    public static BackgroundOptions Default { get; } = new();

    /// <summary>The 0..100 tolerance scale as an RGB distance (100 = half the RGB cube's diagonal).</summary>
    public static double ToDistance(double tolerance) => Math.Clamp(tolerance, 0, 100) * 2.21;

    public static double FromDistance(double distance) => Math.Clamp(distance / 2.21, 0, 100);
}

/// <summary>A computed background removal: per-pixel opacity, plus the cleaned colours of the edge pixels.</summary>
public sealed class BackgroundCut
{
    internal BackgroundCut(int width, int height, byte[] coverage, int[] fixedIndex, ColorBgra[] fixedColor,
        Backdrop backdrop, SubjectMask? subject)
    {
        Width = width; Height = height; Coverage = coverage;
        _fixedIndex = fixedIndex; _fixedColor = fixedColor;
        Backdrop = backdrop; Subject = subject;
    }

    private readonly int[] _fixedIndex;
    private readonly ColorBgra[] _fixedColor;

    public int Width { get; }
    public int Height { get; }

    /// <summary>0 = removed, 255 = kept, in between on soft edges.</summary>
    public byte[] Coverage { get; }

    /// <summary>The background the removal went by.</summary>
    public Backdrop Backdrop { get; }

    /// <summary>The GrabCut result it started from (null in <see cref="BackgroundMode.ColorsOnly"/>).</summary>
    public SubjectMask? Subject { get; }

    /// <summary>True when nothing is kept.</summary>
    public bool IsEmpty => Array.FindIndex(Coverage, c => c != 0) < 0;

    /// <summary>Number of edge pixels whose colour was cleaned.</summary>
    public int CleanedPixels => _fixedIndex.Length;

    /// <summary>The coverage of one subject (0-based, largest first) of the search: this cut,
    /// limited to the neighbourhood of that subject.</summary>
    public byte[] SubjectCoverage(int index)
    {
        if (Subject is null) throw new InvalidOperationException("No subject search was made.");
        var (own, _, _, _, _) = Subject.SubjectCoverage(index);
        var near = new bool[own.Length];
        for (int i = 0; i < own.Length; i++) near[i] = own[i] > 0;
        near = BackgroundRemoval.Dilate(near, Width, Height, 3);
        var result = new byte[own.Length];
        for (int i = 0; i < own.Length; i++) if (near[i]) result[i] = Coverage[i];
        return result;
    }

    /// <summary>Cleans the edge colours, then multiplies the opacity by the coverage.</summary>
    public void ApplyTo(Surface surface) => ApplyTo(surface, Coverage);

    /// <summary>As <see cref="ApplyTo(Surface)"/>, with another coverage (one subject's, say).</summary>
    public unsafe void ApplyTo(Surface surface, byte[] coverage)
    {
        if (surface.Width != Width || surface.Height != Height || coverage.Length != Width * Height)
            throw new ArgumentException("The cut must match the surface's size.");
        for (int k = 0; k < _fixedIndex.Length; k++)
        {
            int i = _fixedIndex[k];
            if (coverage[i] == 0) continue;
            ColorBgra* p = (ColorBgra*)surface.GetRowPointer(i / Width) + i % Width;
            var c = _fixedColor[k];
            p->R = c.R; p->G = c.G; p->B = c.B;
        }
        SubjectSegmentation.ApplyAsAlpha(surface, coverage);
    }
}

public static class BackgroundRemoval
{
    /// <summary>The tolerance (0..100) used when none is set: a little above the backdrop's own noise.</summary>
    public static double AutoTolerance(Backdrop backdrop)
        => backdrop.UserChosen
            ? BackgroundOptions.FromDistance(Backdrop.DefaultUserTolerance)
            : Math.Round(BackgroundOptions.FromDistance(Math.Clamp(backdrop.Tolerance * 2, 30, 55)));

    /// <summary>Finds and removes the background of <paramref name="source"/> (not modified).
    /// <paramref name="hint"/>, when given, is a selection: only what it covers can be kept.</summary>
    public static BackgroundCut Compute(Surface source, BackgroundOptions? options = null, ReadOnlySpan<byte> hint = default,
        SubjectOptions? subjectOptions = null, CancellationToken cancel = default)
    {
        options ??= BackgroundOptions.Default;
        subjectOptions ??= new SubjectOptions();
        var seeding = new SubjectOptions
        {
            WorkingSize = subjectOptions.WorkingSize,
            Iterations = subjectOptions.Iterations,
            Smoothness = subjectOptions.Smoothness,
            MinSubjectShare = subjectOptions.MinSubjectShare,
            MinRelativeSize = subjectOptions.MinRelativeSize,
            MaxHoleShare = subjectOptions.MaxHoleShare,
            BackdropColors = options.Colors,
            BackdropTolerance = options.Colors.Count > 0 && options.Tolerance is { } t ? BackgroundOptions.ToDistance(t) : null
        };

        if (options.Mode == BackgroundMode.ColorsOnly)
        {
            var backdrop = options.Colors.Count > 0
                ? new Backdrop(options.Colors.ToArray(), 0, true, true)
                : SubjectSegmentation.FindBackdrop(source, seeding);
            var all = OpaqueCoverage(source, hint);
            return Refine(source, all, null, backdrop, options, hint, colorsOnly: true, cancel);
        }

        var subject = SubjectSegmentation.Find(source, hint, seeding, cancel);
        return Refine(source, subject.Coverage, subject, subject.Backdrop, options, hint, colorsOnly: false, cancel);
    }

    /// <summary>Re-runs only the full-size clean-up on an earlier GrabCut result, for settings that
    /// don't change the search (tolerance, enclosed areas, edges): far quicker than <see cref="Compute"/>.</summary>
    public static BackgroundCut Refine(Surface source, SubjectMask subject, BackgroundOptions options, ReadOnlySpan<byte> hint = default,
        CancellationToken cancel = default)
        => Refine(source, subject.Coverage, subject, subject.Backdrop, options, hint, colorsOnly: false, cancel);

    /// <summary>The clean-up alone, on any coverage (a selection, another tool's mask): clears
    /// the <paramref name="backdrop"/> colours left in it and softens its edges from the colours.</summary>
    public static BackgroundCut Clean(Surface source, byte[] coverage, Backdrop backdrop, BackgroundOptions? options = null)
    {
        if (coverage.Length != source.Width * source.Height)
            throw new ArgumentException("The coverage must match the image's size.", nameof(coverage));
        return Refine(source, coverage, null, backdrop, options ?? BackgroundOptions.Default, default, colorsOnly: false, CancellationToken.None);
    }

    private static byte[] OpaqueCoverage(Surface source, ReadOnlySpan<byte> hint)
    {
        int w = source.Width, h = source.Height;
        var coverage = new byte[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                coverage[i] = source[x, y].A == 0 ? (byte)0 : hint.IsEmpty ? (byte)255 : hint[i];
            }
        return coverage;
    }

    private static unsafe BackgroundCut Refine(Surface source, byte[] grabCut, SubjectMask? subject, Backdrop backdrop,
        BackgroundOptions options, ReadOnlySpan<byte> hint, bool colorsOnly, CancellationToken cancel)
    {
        int w = source.Width, h = source.Height, n = w * h;
        bool hinted = !hint.IsEmpty;
        if (!backdrop.Usable && !colorsOnly || backdrop.Colors.Length == 0)
        {
            // Nothing reliable to go by: the search result as it is, softened or shrunk if asked.
            var plain = (byte[])grabCut.Clone();
            if (!colorsOnly) Finish(plain, options, w, h);
            return new BackgroundCut(w, h, colorsOnly ? new byte[n] : plain, [], [], backdrop, subject);
        }

        var palette = backdrop.Colors;
        double tolerance = BackgroundOptions.ToDistance(options.Tolerance ?? AutoTolerance(backdrop));
        double t = Math.Max(1, tolerance);
        // "Solid" subject colour: clearly not background; edges are mixes between it and the backdrop.
        double solidDistance = Math.Max(3 * t, 80);

        // --- Per pixel: distance to the nearest background colour ------------------------------
        var dist = new float[n];
        var nearest = new byte[n];
        for (int y = 0; y < h; y++)
        {
            ColorBgra* row = (ColorBgra*)source.GetRowPointer(y);
            for (int x = 0; x < w; x++)
            {
                var c = row[x];
                double best = double.MaxValue; int bestK = 0;
                for (int k = 0; k < palette.Length; k++)
                {
                    double dr = c.R - palette[k].R, dg = c.G - palette[k].G, db = c.B - palette[k].B;
                    double d = dr * dr + dg * dg + db * db;
                    if (d < best) { best = d; bestK = k; }
                }
                dist[y * w + x] = (float)Math.Sqrt(best);
                nearest[y * w + x] = (byte)bestK;
            }
        }
        cancel.ThrowIfCancellationRequested();

        var kept = new bool[n];        // inside the search result
        var key = new bool[n];         // inside it, and of a background colour
        var solid = new bool[n];       // inside it, and clearly not
        for (int i = 0; i < n; i++)
        {
            kept[i] = grabCut[i] > 0;
            key[i] = kept[i] && dist[i] < t;
            solid[i] = kept[i] && dist[i] >= solidDistance;
        }
        var nearSolid = Dilate(solid, w, h, 2);

        // --- Background-coloured areas left in the result: which ones go ----------------------
        // Each 8-connected area is judged on what surrounds it. Next to the removed background it
        // is a leftover of it; enclosed, it goes only if the option is on. Either way, in subject
        // mode, it must be sharply set off from its surroundings (most of its rim right next to a
        // clearly different colour): a white shirt fades into its own grey shading and stays,
        // the inside of a red letter does not.
        var removed = new bool[n];
        var area = new int[n];
        int[] queue = new int[n];
        var labelOf = new int[n];
        int label = 0;
        for (int start = 0; start < n; start++)
        {
            if (!key[start] || labelOf[start] != 0) continue;
            label++;
            int head = 0, tail = 0;
            queue[tail++] = start;
            labelOf[start] = label;
            bool touchesOut = false;
            int rim = 0, sharp = 0;
            while (head < tail)
            {
                int i = queue[head++];
                int x = i % w, y = i / w;
                for (int dy = -1; dy <= 1; dy++)
                    for (int dx = -1; dx <= 1; dx++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx, ny = y + dy;
                        if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) { touchesOut = true; continue; }
                        int j = ny * w + nx;
                        if (key[j])
                        {
                            if (labelOf[j] == 0) { labelOf[j] = label; queue[tail++] = j; }
                            continue;
                        }
                        if (!kept[j]) { touchesOut = true; continue; }
                        rim++;
                        if (nearSolid[j]) sharp++;
                    }
            }
            bool go;
            if (colorsOnly) go = touchesOut || options.RemoveEnclosed;
            else
            {
                bool contrasted = rim == 0 || sharp >= 0.5 * rim;
                go = contrasted && (touchesOut || options.RemoveEnclosed);
            }
            if (!go) continue;
            for (int q = 0; q < tail; q++) removed[queue[q]] = true;
            cancel.ThrowIfCancellationRequested();
        }

        // --- Soft edges and specks, from the colours ---------------------------------------
        var alpha = new float[n];
        var gone = new bool[n];
        for (int i = 0; i < n; i++)
        {
            gone[i] = !kept[i] || removed[i];
            alpha[i] = gone[i] ? 0f : 1f;
        }
        var nearGone = Dilate(gone, w, h, 2);
        // The cut was made on a reduced copy, so its edge can sit a pixel or two inside the
        // subject; the band just outside it is weighed too.
        var core = new bool[n];
        for (int i = 0; i < n; i++) core[i] = !gone[i] && grabCut[i] >= 128;
        var nearCore = colorsOnly ? core : Dilate(core, w, h, 2);
        // The subject colour a pixel blends: the mean of the clearly-subject pixels around it.
        var subjectSums = WindowSums(source, (i) => nearCore[i] && !removed[i] && dist[i] >= solidDistance, w, h, 3);

        var fixedIndex = new List<int>();
        var fixedColor = new List<ColorBgra>();
        var cleaned = new ColorBgra[n];
        var unmixed = new bool[n];

        // How much of the subject colour is in pixel i, read as a blend of it with the nearest
        // background colour. NaN when there is no subject colour nearby, or (strict) when the
        // pixel is off the line between the two - a colour of its own, not a blend.
        double Unmix(int i, ColorBgra c, bool strict)
        {
            int count = subjectSums.Count(i);
            var b = palette[nearest[i]];
            double vr = c.R - b.R, vg = c.G - b.G, vb = c.B - b.B;
            if (count == 0) return strict ? double.NaN : Math.Clamp((dist[i] - t) / (solidDistance - t), 0, 1);
            var (fr, fg, fb) = subjectSums.Mean(i);
            double ur = fr - b.R, ug = fg - b.G, ub = fb - b.B;
            double len2 = ur * ur + ug * ug + ub * ub;
            double share = (vr * ur + vg * ug + vb * ub) / len2;
            if (strict)
            {
                double pr = vr - share * ur, pg = vg - share * ug, pb = vb - share * ub;
                if (Math.Sqrt(pr * pr + pg * pg + pb * pb) > Math.Max(t, 0.15 * Math.Sqrt(len2))) return double.NaN;
            }
            // The tolerance comes off the background end, so its noise reads as background.
            double floor = t / Math.Sqrt(len2);
            return Math.Clamp((share - floor) / Math.Max(1e-6, 1 - floor), 0, 1);
        }

        void Set(int i, ColorBgra c, double a)
        {
            alpha[i] = (float)a;
            if (!options.CleanEdges || a <= 0.02 || a >= 0.999) return;
            // C = a F + (1 - a) B, so F = B + (C - B) / a.
            var b = palette[nearest[i]];
            unmixed[i] = true;
            cleaned[i] = ColorBgra.FromBgr(
                (byte)Math.Clamp(Math.Round(b.B + (c.B - b.B) / a), 0, 255),
                (byte)Math.Clamp(Math.Round(b.G + (c.G - b.G) / a), 0, 255),
                (byte)Math.Clamp(Math.Round(b.R + (c.R - b.R) / a), 0, 255));
        }

        // Edges: the pixels next to the removed background.
        var edge = new bool[n];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (!nearGone[i] || !nearCore[i] || removed[i]) continue;
                var c = source[x, y];
                if (c.A == 0 || hinted && hint[i] == 0 || !kept[i] && dist[i] < t) continue;
                // A clearly different colour is weighed only if it is a blend of the subject's and
                // the background's; otherwise it is the subject, or something else, and stays.
                double a = Unmix(i, c, strict: dist[i] >= solidDistance);
                if (double.IsNaN(a)) continue;
                edge[i] = true;
                Set(i, c, a);
            }
        cancel.ThrowIfCancellationRequested();

        // Specks: with enclosed areas cleared, small spots inside the subject that blend its
        // colour with the background's (paper showing through ink) are partly background too.
        // Only small ones: a large pale area on that line is more likely a highlight.
        if (options.RemoveEnclosed)
        {
            var speckAlpha = new float[n];
            var candidate = new bool[n];
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = y * w + x;
                    if (!kept[i] || removed[i] || edge[i] || dist[i] < t) continue;
                    var c = source[x, y];
                    double a = Unmix(i, c, strict: true);
                    if (double.IsNaN(a) || a >= 0.85) continue;
                    candidate[i] = true;
                    speckAlpha[i] = (float)a;
                }
            int maxSpeck = Math.Max(64, (int)(n * 0.0002));
            var seen = new bool[n];
            for (int start = 0; start < n; start++)
            {
                if (!candidate[start] || seen[start]) continue;
                int head = 0, tail = 0;
                queue[tail++] = start;
                seen[start] = true;
                while (head < tail)
                {
                    int i = queue[head++];
                    int x = i % w, y = i / w;
                    for (int dy = -1; dy <= 1; dy++)
                        for (int dx = -1; dx <= 1; dx++)
                        {
                            int nx = x + dx, ny = y + dy;
                            if ((uint)nx >= (uint)w || (uint)ny >= (uint)h) continue;
                            int j = ny * w + nx;
                            if (!candidate[j] || seen[j]) continue;
                            seen[j] = true;
                            queue[tail++] = j;
                        }
                }
                if (tail > maxSpeck) continue;
                for (int q = 0; q < tail; q++)
                {
                    int i = queue[q];
                    Set(i, source[i % w, i / w], speckAlpha[i]);
                }
            }
        }
        cancel.ThrowIfCancellationRequested();
        for (int i = 0; i < n; i++)
            if (unmixed[i]) { fixedIndex.Add(i); fixedColor.Add(cleaned[i]); }

        var coverage = new byte[n];
        for (int i = 0; i < n; i++) coverage[i] = (byte)Math.Round(alpha[i] * 255);
        Finish(coverage, options, w, h);
        if (hinted)
            for (int i = 0; i < n; i++) if (hint[i] < coverage[i]) coverage[i] = hint[i];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                if (source[x, y].A == 0) coverage[y * w + x] = 0;

        return new BackgroundCut(w, h, coverage, fixedIndex.ToArray(), fixedColor.ToArray(), backdrop, subject);
    }

    /// <summary>Shrink, then feather.</summary>
    private static void Finish(byte[] coverage, BackgroundOptions options, int w, int h)
    {
        int shrink = Math.Clamp(options.ShrinkEdge, 0, 10);
        if (shrink > 0)
        {
            var eroded = MinFilter(coverage, w, h, shrink);
            Array.Copy(eroded, coverage, coverage.Length);
        }
        int feather = Math.Clamp(options.Feather, 0, 10);
        if (feather > 0)
        {
            // Blur only inward (min with the original), so no background shows up as a halo.
            var blurred = BoxBlur(BoxBlur(coverage, w, h, (feather + 1) / 2), w, h, (feather + 1) / 2);
            for (int i = 0; i < coverage.Length; i++) if (blurred[i] < coverage[i]) coverage[i] = blurred[i];
        }
    }

    /// <summary>Sums of colour and count over a (2r+1)² window of the pixels <paramref name="include"/> picks,
    /// from summed-area tables, so each window costs four lookups whatever its size.</summary>
    private sealed class Windowed(long[] r, long[] g, long[] b, int[] count, int w, int h, int radius)
    {
        private (int a, int b, int c, int d) Corners(int i)
        {
            int x = i % w, y = i / w, sw = w + 1;
            int x0 = Math.Max(0, x - radius), y0 = Math.Max(0, y - radius);
            int x1 = Math.Min(w, x + radius + 1), y1 = Math.Min(h, y + radius + 1);
            return (y1 * sw + x1, y0 * sw + x1, y1 * sw + x0, y0 * sw + x0);
        }

        public int Count(int i)
        {
            var (a, bb, c, d) = Corners(i);
            return count[a] - count[bb] - count[c] + count[d];
        }

        public (double R, double G, double B) Mean(int i)
        {
            var (a, bb, c, d) = Corners(i);
            double k = count[a] - count[bb] - count[c] + count[d];
            return ((r[a] - r[bb] - r[c] + r[d]) / k, (g[a] - g[bb] - g[c] + g[d]) / k, (b[a] - b[bb] - b[c] + b[d]) / k);
        }
    }

    private static Windowed WindowSums(Surface source, Func<int, bool> include, int w, int h, int radius)
    {
        int sw = w + 1, size = sw * (h + 1);
        var r = new long[size]; var g = new long[size]; var b = new long[size]; var count = new int[size];
        for (int y = 0; y < h; y++)
        {
            long rr = 0, gg = 0, bb = 0; int cc = 0;
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x;
                if (include(i))
                {
                    var c = source[x, y];
                    rr += c.R; gg += c.G; bb += c.B; cc++;
                }
                int o = (y + 1) * sw + x + 1, above = y * sw + x + 1;
                r[o] = r[above] + rr; g[o] = g[above] + gg; b[o] = b[above] + bb; count[o] = count[above] + cc;
            }
        }
        return new Windowed(r, g, b, count, w, h, radius);
    }

    /// <summary>Square dilation of a flag mask by <paramref name="r"/> pixels, via running counts.</summary>
    internal static bool[] Dilate(bool[] mask, int w, int h, int r)
    {
        var horizontal = new bool[mask.Length];
        for (int y = 0; y < h; y++)
        {
            int o = y * w, run = 0;
            for (int x = 0; x < Math.Min(r, w); x++) if (mask[o + x]) run++;
            for (int x = 0; x < w; x++)
            {
                if (x + r < w && mask[o + x + r]) run++;
                if (x - r - 1 >= 0 && mask[o + x - r - 1]) run--;
                horizontal[o + x] = run > 0;
            }
        }
        var result = new bool[mask.Length];
        for (int x = 0; x < w; x++)
        {
            int run = 0;
            for (int y = 0; y < Math.Min(r, h); y++) if (horizontal[y * w + x]) run++;
            for (int y = 0; y < h; y++)
            {
                if (y + r < h && horizontal[(y + r) * w + x]) run++;
                if (y - r - 1 >= 0 && horizontal[(y - r - 1) * w + x]) run--;
                result[y * w + x] = run > 0;
            }
        }
        return result;
    }

    private static byte[] MinFilter(byte[] values, int w, int h, int r)
    {
        var tmp = new byte[values.Length];
        var result = new byte[values.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte m = 255;
                for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) m = Math.Min(m, values[y * w + k]);
                tmp[y * w + x] = m;
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                byte m = 255;
                for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) m = Math.Min(m, tmp[k * w + x]);
                result[y * w + x] = m;
            }
        return result;
    }

    private static byte[] BoxBlur(byte[] values, int w, int h, int r)
    {
        if (r <= 0) return (byte[])values.Clone();
        var tmp = new byte[values.Length];
        var result = new byte[values.Length];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = 0, c = 0;
                for (int k = Math.Max(0, x - r); k <= Math.Min(w - 1, x + r); k++) { s += values[y * w + k]; c++; }
                tmp[y * w + x] = (byte)(s / c);
            }
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int s = 0, c = 0;
                for (int k = Math.Max(0, y - r); k <= Math.Min(h - 1, y + r); k++) { s += tmp[k * w + x]; c++; }
                result[y * w + x] = (byte)(s / c);
            }
        return result;
    }
}
