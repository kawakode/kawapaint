using KawaPaint.Engine;
using Xunit;

namespace KawaPaint.Tests;

public class GraphCutTests
{
    [Fact]
    public void ChainCutsAtItsWeakestLink()
    {
        // source -9-> 0 -2-> 1 -9-> sink: the 0-1 arc is the cut.
        var graph = new GraphCut(2);
        graph.AddTerminalWeights(0, 9, 0);
        graph.AddTerminalWeights(1, 0, 9);
        graph.AddEdge(0, 1, 2, 0);
        Assert.Equal(2, graph.Solve(), 6);
        Assert.True(graph.IsSourceSide(0));
        Assert.False(graph.IsSourceSide(1));
    }

    [Fact]
    public void MatchesAReferenceMaxFlowOnRandomGraphs()
    {
        var rng = new Random(7);
        for (int trial = 0; trial < 200; trial++)
        {
            int n = rng.Next(2, 14);
            var cap = new double[n + 2, n + 2];   // n = source, n+1 = sink
            var graph = new GraphCut(n);
            for (int i = 0; i < n; i++)
            {
                double s = rng.Next(3) == 0 ? 0 : rng.Next(1, 20), t = rng.Next(3) == 0 ? 0 : rng.Next(1, 20);
                graph.AddTerminalWeights(i, s, t);
                cap[n, i] += s; cap[i, n + 1] += t;
            }
            int edges = rng.Next(0, n * 3);
            for (int e = 0; e < edges; e++)
            {
                int a = rng.Next(n), b = rng.Next(n);
                if (a == b) continue;
                double c = rng.Next(0, 15), r = rng.Next(0, 15);
                graph.AddEdge(a, b, c, r);
                cap[a, b] += c; cap[b, a] += r;
            }
            double expected = EdmondsKarp(cap, n, n + 1);
            Assert.Equal(expected, graph.Solve(), 6);

            // The reported sides must form a cut of exactly that capacity.
            double cut = 0;
            bool Side(int v) => v == n || v < n && graph.IsSourceSide(v);
            for (int a = 0; a < n + 2; a++)
                for (int b = 0; b < n + 2; b++)
                    if (Side(a) && !Side(b) && b != n && a != n + 1) cut += cap[a, b];
            Assert.Equal(expected, cut, 6);
        }
    }

    private static double EdmondsKarp(double[,] capacity, int s, int t)
    {
        int n = capacity.GetLength(0);
        var r = (double[,])capacity.Clone();
        double flow = 0;
        while (true)
        {
            var parent = Enumerable.Repeat(-1, n).ToArray();
            parent[s] = s;
            var queue = new Queue<int>();
            queue.Enqueue(s);
            while (queue.Count > 0 && parent[t] < 0)
            {
                int u = queue.Dequeue();
                for (int v = 0; v < n; v++)
                    if (parent[v] < 0 && r[u, v] > 0) { parent[v] = u; queue.Enqueue(v); }
            }
            if (parent[t] < 0) return flow;
            double b = double.MaxValue;
            for (int v = t; v != s; v = parent[v]) b = Math.Min(b, r[parent[v], v]);
            for (int v = t; v != s; v = parent[v]) { r[parent[v], v] -= b; r[v, parent[v]] += b; }
            flow += b;
        }
    }
}

public class SubjectSegmentationTests
{
    /// <summary>A noisy, softly graded backdrop - closer to a photo than a flat fill.</summary>
    private static Surface Backdrop(int w, int h, int seed = 3)
    {
        var rng = new Random(seed);
        var s = new Surface(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int v = 200 + x * 30 / w + rng.Next(-8, 9);
                s[x, y] = ColorBgra.FromBgr((byte)(v - 10), (byte)v, (byte)v);
            }
        return s;
    }

    private static void Disk(Surface s, int cx, int cy, int r, ColorBgra color, int seed = 5)
    {
        var rng = new Random(seed);
        for (int y = Math.Max(0, cy - r); y < Math.Min(s.Height, cy + r + 1); y++)
            for (int x = Math.Max(0, cx - r); x < Math.Min(s.Width, cx + r + 1); x++)
                if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r)
                {
                    int n = rng.Next(-10, 11);
                    s[x, y] = ColorBgra.FromBgr((byte)Math.Clamp(color.B + n, 0, 255),
                        (byte)Math.Clamp(color.G + n, 0, 255), (byte)Math.Clamp(color.R + n, 0, 255));
                }
    }

    private static bool InDisk(int x, int y, int cx, int cy, int r) => (x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r;

    /// <summary>Intersection over union of the mask (>= 128) with a predicate.</summary>
    private static double IoU(byte[] mask, int w, int h, Func<int, int, bool> truth)
    {
        int inter = 0, union = 0;
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                bool m = mask[y * w + x] >= 128, t = truth(x, y);
                if (m && t) inter++;
                if (m || t) union++;
            }
        return union == 0 ? 1 : (double)inter / union;
    }

    [Fact]
    public void FindsACentredSubjectWithoutAHint()
    {
        using var image = Backdrop(240, 180);
        Disk(image, 120, 90, 50, ColorBgra.FromBgr(40, 60, 190));
        var mask = SubjectSegmentation.Find(image);
        Assert.Equal(1, mask.SubjectCount);
        Assert.True(IoU(mask.Coverage, 240, 180, (x, y) => InDisk(x, y, 120, 90, 50)) > 0.95);
    }

    [Fact]
    public void WorksOnAReducedCopyOfALargeImage()
    {
        using var image = Backdrop(1200, 800);
        Disk(image, 600, 400, 250, ColorBgra.FromBgr(30, 160, 60));
        var mask = SubjectSegmentation.Find(image, options: new SubjectOptions { WorkingSize = 200 });
        Assert.Equal(1, mask.SubjectCount);
        Assert.True(IoU(mask.Coverage, 1200, 800, (x, y) => InDisk(x, y, 600, 400, 250)) > 0.97);
        // The edge is soft (antialiased), not a hard step.
        Assert.Contains(mask.Coverage, c => c is > 20 and < 235);
    }

    [Fact]
    public void SeparatesSeveralSubjects()
    {
        using var image = Backdrop(300, 160);
        Disk(image, 70, 80, 40, ColorBgra.FromBgr(40, 60, 190));
        Disk(image, 220, 80, 50, ColorBgra.FromBgr(160, 60, 30), seed: 9);
        var mask = SubjectSegmentation.Find(image);
        Assert.Equal(2, mask.SubjectCount);

        // Largest first.
        var (big, bx, by, bw, bh) = mask.SubjectCoverage(0);
        Assert.True(IoU(big, 300, 160, (x, y) => InDisk(x, y, 220, 80, 50)) > 0.93);
        Assert.InRange(bx + bw / 2, 210, 230);
        var (small, _, _, _, _) = mask.SubjectCoverage(1);
        Assert.True(IoU(small, 300, 160, (x, y) => InDisk(x, y, 70, 80, 40)) > 0.93);
        Assert.Equal(0, small[80 * 300 + 220]);
    }

    [Fact]
    public void AHintLimitsTheSearch()
    {
        using var image = Backdrop(300, 160);
        Disk(image, 70, 80, 40, ColorBgra.FromBgr(40, 60, 190));
        Disk(image, 220, 80, 50, ColorBgra.FromBgr(160, 60, 30), seed: 9);
        var hint = new Selection(300, 160);
        hint.ReplaceWithRectangle(10, 20, 130, 140);
        var mask = SubjectSegmentation.Find(image, hint.Mask);
        Assert.Equal(1, mask.SubjectCount);
        Assert.True(IoU(mask.Coverage, 300, 160, (x, y) => InDisk(x, y, 70, 80, 40)) > 0.93);
        Assert.Equal(0, mask.Coverage[80 * 300 + 220]);
    }

    [Fact]
    public void ASubjectRunningOffTheEdgeKeepsItsEdgePixels()
    {
        // Head-and-shoulders framing: the subject is cut by the bottom of the image.
        using var image = Backdrop(200, 200);
        Disk(image, 100, 200, 70, ColorBgra.FromBgr(50, 50, 170));
        var mask = SubjectSegmentation.Find(image);
        Assert.Equal(1, mask.SubjectCount);
        Assert.Equal(255, mask.Coverage[199 * 200 + 100]);
        Assert.True(IoU(mask.Coverage, 200, 200, (x, y) => InDisk(x, y, 100, 200, 70)) > 0.93);
    }

    [Fact]
    public void TransparentPixelsAreNeverPartOfTheSubject()
    {
        using var image = new Surface(120, 120);
        Disk(image, 60, 60, 35, ColorBgra.FromBgr(40, 200, 220));
        var mask = SubjectSegmentation.Find(image);
        Assert.False(mask.IsEmpty);
        Assert.Equal(0, mask.Coverage[5 * 120 + 5]);
        Assert.Equal(255, mask.Coverage[60 * 120 + 60]);
    }

    [Fact]
    public void AFlatImageHasNoSubject()
    {
        using var image = new Surface(80, 60);
        image.Clear(ColorBgra.FromBgr(120, 120, 120));
        Assert.True(SubjectSegmentation.Find(image).IsEmpty);
    }

    [Fact]
    public void RemoveBackgroundScriptStepClearsTheBackdrop()
    {
        Assert.True(KawaPaint.App.Core.Scripting.ScriptRecorder.IsScriptable("image.removeBackground"));
        var doc = new Document(160, 120);
        var surface = doc.AddLayer().Surface;
        using (var image = Backdrop(160, 120))
        {
            Disk(image, 80, 60, 35, ColorBgra.FromBgr(40, 60, 190));
            surface.CopyFrom(image);
        }
        var script = new KawaPaint.Engine.Scripting.ScriptFile();
        script.Steps.Add(new KawaPaint.Engine.Scripting.ScriptStep("image.removeBackground"));
        var results = KawaPaint.Engine.Scripting.ScriptExecutor.Run(ref doc, script);
        Assert.All(results, r => Assert.Equal(KawaPaint.Engine.Scripting.ScriptStepOutcome.Applied, r.Outcome));
        Assert.Equal(0, doc.Layers[0].Surface[3, 3].A);
        Assert.Equal(255, doc.Layers[0].Surface[80, 60].A);
        doc.Dispose();
    }

    [Fact]
    public void ApplyAsAlphaClearsTheBackground()
    {
        using var image = Backdrop(160, 120);
        Disk(image, 80, 60, 35, ColorBgra.FromBgr(40, 60, 190));
        var mask = SubjectSegmentation.Find(image);
        SubjectSegmentation.ApplyAsAlpha(image, mask.Coverage);
        Assert.Equal(0, image[3, 3].A);
        Assert.Equal(255, image[80, 60].A);
    }
}

public class AutoCropTrimTests
{
    [Fact]
    public void TrimsANoisyUniformBackdrop()
    {
        using var doc = new Document(80, 60);
        var surface = doc.AddLayer().Surface;
        var rng = new Random(2);
        for (int y = 0; y < 60; y++)
            for (int x = 0; x < 80; x++)
            {
                byte v = (byte)(245 + rng.Next(-8, 9));
                surface[x, y] = ColorBgra.FromBgr(v, v, v);
            }
        surface.ClearRect(20, 10, 30, 25, ColorBgra.FromBgr(30, 30, 200));
        Assert.Equal((0, 0, 80, 60), DocumentOps.ContentBounds(doc));
        Assert.Equal((20, 10, 30, 25), DocumentOps.TrimBounds(doc));
    }

    [Fact]
    public void LeavesAnImageWithoutBackdropAlone()
    {
        using var doc = new Document(40, 30);
        var surface = doc.AddLayer().Surface;
        for (int y = 0; y < 30; y++)
            for (int x = 0; x < 40; x++)
                surface[x, y] = ColorBgra.FromBgr((byte)(x * 6), (byte)(y * 8), 100);
        Assert.Equal((0, 0, 40, 30), DocumentOps.TrimBounds(doc));
    }

    [Fact]
    public void AnAllBackdropImageIsNotTrimmedAway()
    {
        using var doc = new Document(20, 20);
        doc.AddLayer().Surface.Clear(ColorBgra.White);
        Assert.Equal((0, 0, 20, 20), DocumentOps.TrimBounds(doc));
    }

    [Fact]
    public void HiddenLayerContentIsKept()
    {
        using var doc = new Document(50, 40);
        var visible = doc.AddLayer().Surface;
        visible.Clear(ColorBgra.White);
        visible.ClearRect(10, 10, 5, 5, ColorBgra.Black);
        Layer hidden = doc.AddLayer();
        hidden.Visible = false;
        hidden.Surface[40, 30] = ColorBgra.Black;
        Assert.Equal((10, 10, 31, 21), DocumentOps.TrimBounds(doc));
    }

    [Fact]
    public void ToleranceBelowZeroOnlyTrimsTransparency()
    {
        using var doc = new Document(20, 20);
        var s = doc.AddLayer().Surface;
        s.ClearRect(2, 2, 16, 16, ColorBgra.White);
        s[10, 10] = ColorBgra.Black;
        Assert.Equal((2, 2, 16, 16), DocumentOps.TrimBounds(doc, -1));
        Assert.Equal((10, 10, 1, 1), DocumentOps.TrimBounds(doc));
    }
}
