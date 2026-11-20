using KawaPaint.Engine;
using Xunit;

namespace KawaPaint.Tests;

public class BackgroundRemovalTests
{
    private static readonly ColorBgra Red = ColorBgra.FromBgr(35, 30, 228);

    /// <summary>A fake-transparency checkerboard (white and light grey squares) with a little
    /// noise, as on the "transparent" PNGs of stock sites.</summary>
    private static Surface Checkerboard(int w, int h, int seed = 1)
    {
        var rng = new Random(seed);
        var s = new Surface(w, h);
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int v = ((x / 12 + y / 12) % 2 == 0 ? 255 : 237) - rng.Next(0, 4);
                s[x, y] = ColorBgra.FromBgr((byte)v, (byte)v, (byte)v);
            }
        return s;
    }

    private static void Fill(Surface s, int x0, int y0, int x1, int y1, ColorBgra c)
    {
        for (int y = y0; y < y1; y++)
            for (int x = x0; x < x1; x++)
                s[x, y] = c;
    }

    /// <summary>A red stamp: a frame of two bars, a letter with a hole (an "O"), a solid block
    /// with paper-white specks, all on the checkerboard. The background shows through the hole,
    /// between the bars and in the specks, none of it joined to the image's borders.</summary>
    private static Surface Stamp()
    {
        var s = Checkerboard(320, 240);
        Fill(s, 40, 50, 280, 62, Red);     // top bar
        Fill(s, 40, 178, 280, 190, Red);   // bottom bar
        Fill(s, 40, 50, 52, 190, Red);     // left bar
        Fill(s, 268, 50, 280, 190, Red);   // right bar
        Fill(s, 80, 80, 140, 160, Red);    // "O": a block ...
        var hole = Checkerboard(320, 240, seed: 9);
        for (int y = 100; y < 140; y++)    // ... with the backdrop showing through its middle
            for (int x = 100; x < 120; x++)
                s[x, y] = hole[x, y];
        hole.Dispose();
        Fill(s, 170, 80, 240, 160, Red);   // a solid block ...
        foreach (var (x, y) in new[] { (185, 95), (220, 110), (200, 140) })
            Fill(s, x, y, x + 3, y + 3, ColorBgra.White);   // ... with specks of paper
        return s;
    }

    /// <summary>What GrabCut returned on the real stamp: the whole stamp as one shape, the
    /// pockets of background inside it included.</summary>
    private static byte[] StampOutline()
    {
        var coverage = new byte[320 * 240];
        for (int y = 44; y < 196; y++)
            for (int x = 34; x < 286; x++)
                coverage[y * 320 + x] = 255;
        return coverage;
    }

    private static Backdrop CheckerBackdrop =>
        new([ColorBgra.FromBgr(253, 253, 253), ColorBgra.FromBgr(235, 235, 235)], 4, Flat: true, UserChosen: false);

    [Fact]
    public void ClearsTheBackgroundLeftInsideTheSubject()
    {
        using var image = Stamp();
        var cut = BackgroundRemoval.Clean(image, StampOutline(), CheckerBackdrop);
        byte At(int x, int y) => cut.Coverage[y * 320 + x];

        Assert.Equal(0, At(5, 5));          // the outer background
        Assert.Equal(0, At(110, 120));      // inside the "O"
        Assert.Equal(0, At(60, 120));       // inside the frame, between its bars and the letters
        Assert.Equal(0, At(160, 70));       // between the top bar and the letters
        Assert.Equal(0, At(186, 96));       // a speck
        Assert.Equal(255, At(45, 120));     // the frame
        Assert.Equal(255, At(90, 120));     // the "O"
        Assert.Equal(255, At(200, 120));    // the block

        cut.ApplyTo(image);
        Assert.Equal(Red, image[90, 120]);
        Assert.Equal(0, image[110, 120].A);
    }

    [Fact]
    public void FindsAndClearsAStampOnACheckerboard()
    {
        using var image = Stamp();
        var cut = BackgroundRemoval.Compute(image);
        Assert.True(cut.Backdrop.Flat);
        Assert.Equal(0, cut.Coverage[5 * 320 + 5]);
        Assert.Equal(0, cut.Coverage[120 * 320 + 110]);
        Assert.Equal(0, cut.Coverage[96 * 320 + 186]);
        Assert.Equal(255, cut.Coverage[120 * 320 + 90]);
    }

    [Fact]
    public void KeepsEnclosedAreasWhenAskedTo()
    {
        using var image = Stamp();
        var cut = BackgroundRemoval.Clean(image, StampOutline(), CheckerBackdrop, new BackgroundOptions { RemoveEnclosed = false });
        Assert.Equal(0, cut.Coverage[5 * 320 + 5]);
        Assert.Equal(255, cut.Coverage[120 * 320 + 110]);   // the inside of the "O"
        Assert.Equal(255, cut.Coverage[96 * 320 + 186]);    // a speck
        // Next to the removed outside, the leftovers still go: they are the frame's edge.
        Assert.Equal(0, cut.Coverage[47 * 320 + 160]);
    }

    [Fact]
    public void SoftEdgesGetTheirAlphaFromTheBlendAndLoseTheBackgroundColour()
    {
        // A red block on white whose left edge column is half red, half white.
        var s = new Surface(200, 160);
        s.Clear(ColorBgra.White);
        Fill(s, 60, 40, 140, 120, Red);
        var half = ColorBgra.FromBgr((byte)((Red.B + 255) / 2), (byte)((Red.G + 255) / 2), (byte)((Red.R + 255) / 2));
        for (int y = 40; y < 120; y++) s[59, y] = half;

        var cut = BackgroundRemoval.Compute(s);
        byte a = cut.Coverage[80 * 200 + 59];
        Assert.InRange(a, 100, 150);        // about half
        cut.ApplyTo(s);
        var edge = s[59, 80];
        Assert.InRange(edge.R, Red.R - 20, Red.R + 20);
        Assert.InRange(edge.G, 0, 70);      // not the pale pink of the blend
        Assert.InRange(edge.B, 0, 75);
        s.Dispose();
    }

    [Fact]
    public void KeepsAWhiteAreaThatFadesIntoItsOwnShading()
    {
        // A navy disk holding a white patch that shades off to grey: part of the subject (a white
        // shirt, a highlight), not background, since nothing sharp sets it apart.
        var s = new Surface(240, 200);
        s.Clear(ColorBgra.White);
        for (int y = 0; y < 200; y++)
            for (int x = 0; x < 240; x++)
            {
                double d = Math.Sqrt((x - 120) * (x - 120) + (y - 100) * (y - 100));
                if (d > 70) continue;
                if (d < 25) { s[x, y] = ColorBgra.White; continue; }
                if (d < 45)
                {
                    int v = (int)(255 - (d - 25) / 20 * 175);   // white fading to grey
                    s[x, y] = ColorBgra.FromBgr((byte)v, (byte)v, (byte)v);
                    continue;
                }
                s[x, y] = ColorBgra.FromBgr(90, 30, 20);
            }
        var cut = BackgroundRemoval.Compute(s);
        Assert.Equal(0, cut.Coverage[5 * 240 + 5]);
        Assert.Equal(255, cut.Coverage[100 * 240 + 120]);
        s.Dispose();
    }

    [Fact]
    public void ABusyBackdropIsNotClearedOnColourAlone()
    {
        var rng = new Random(4);
        var s = new Surface(240, 180);
        for (int y = 0; y < 180; y++)
            for (int x = 0; x < 240; x++)
                s[x, y] = ColorBgra.FromBgr((byte)rng.Next(60, 256), (byte)rng.Next(60, 256), (byte)rng.Next(60, 256));
        Fill(s, 80, 50, 160, 130, ColorBgra.FromBgr(20, 20, 20));
        var search = SubjectSegmentation.Find(s);
        var cut = BackgroundRemoval.Refine(s, search, BackgroundOptions.Default);
        Assert.False(search.Backdrop.Usable);
        Assert.Equal(search.Coverage, cut.Coverage);
        s.Dispose();
    }

    [Fact]
    public void PickedColoursAreTheBackground()
    {
        // A green screen whose borders are half covered by a grey band: the eyedropper says green.
        var s = new Surface(240, 180);
        s.Clear(ColorBgra.FromBgr(40, 200, 40));
        Fill(s, 0, 0, 240, 60, ColorBgra.FromBgr(128, 128, 128));
        Fill(s, 90, 90, 150, 150, ColorBgra.FromBgr(30, 30, 220));
        var options = new BackgroundOptions { Mode = BackgroundMode.ColorsOnly, Colors = [ColorBgra.FromBgr(40, 200, 40)] };
        var cut = BackgroundRemoval.Compute(s, options);
        Assert.True(cut.Backdrop.UserChosen);
        Assert.Equal(0, cut.Coverage[120 * 240 + 20]);      // green
        Assert.Equal(255, cut.Coverage[30 * 240 + 20]);     // grey band kept
        Assert.Equal(255, cut.Coverage[120 * 240 + 120]);   // subject kept
        s.Dispose();
    }

    [Fact]
    public void ColorsOnlyCanSpareEnclosedAreas()
    {
        var s = new Surface(200, 160);
        s.Clear(ColorBgra.White);
        Fill(s, 50, 40, 150, 120, Red);
        Fill(s, 90, 70, 110, 90, ColorBgra.White);
        var everywhere = BackgroundRemoval.Compute(s, new BackgroundOptions { Mode = BackgroundMode.ColorsOnly });
        var fromBorders = BackgroundRemoval.Compute(s, new BackgroundOptions { Mode = BackgroundMode.ColorsOnly, RemoveEnclosed = false });
        Assert.Equal(0, everywhere.Coverage[80 * 200 + 100]);
        Assert.Equal(255, fromBorders.Coverage[80 * 200 + 100]);
        Assert.Equal(0, fromBorders.Coverage[5 * 200 + 5]);
        s.Dispose();
    }

    [Fact]
    public void HigherToleranceRemovesMore()
    {
        var s = new Surface(200, 160);
        s.Clear(ColorBgra.White);
        Fill(s, 50, 40, 150, 120, Red);
        Fill(s, 90, 70, 110, 90, ColorBgra.FromBgr(215, 215, 215));   // a light grey patch
        var low = BackgroundRemoval.Compute(s, new BackgroundOptions { Tolerance = 15 });
        var high = BackgroundRemoval.Compute(s, new BackgroundOptions { Tolerance = 40 });
        Assert.Equal(255, low.Coverage[80 * 200 + 100]);
        Assert.Equal(0, high.Coverage[80 * 200 + 100]);
        s.Dispose();
    }

    [Fact]
    public void ShrinkAndFeatherOnlyEverTakeAway()
    {
        using var image = Stamp();
        var plain = BackgroundRemoval.Compute(image);
        var shrunk = BackgroundRemoval.Compute(image, new BackgroundOptions { ShrinkEdge = 2, Feather = 3 });
        for (int i = 0; i < plain.Coverage.Length; i++) Assert.True(shrunk.Coverage[i] <= plain.Coverage[i]);
        Assert.Equal(0, shrunk.Coverage[120 * 320 + 81]);     // within 2 px of the "O"'s edge
        Assert.Equal(255, shrunk.Coverage[120 * 320 + 90]);
    }
}
