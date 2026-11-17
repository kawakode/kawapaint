// KawaPaint - document-level operations that produce a new Document.

using KawaPaint.Engine.ThreeD;

namespace KawaPaint.Engine;

/// <summary>Where the existing content lands within a larger or smaller canvas. See DocumentOps.ResizeCanvas.</summary>
public enum CanvasAnchor
{
    TopLeft, TopCenter, TopRight,
    MiddleLeft, MiddleCenter, MiddleRight,
    BottomLeft, BottomCenter, BottomRight
}

public static class DocumentOps
{
    /// <summary>Returns a new document cropped to the (x,y,w,h) region; every layer is cropped in place.</summary>
    public static Document Crop(Document doc, int x, int y, int w, int h)
    {
        var result = NewTransformedDocument(doc, w, h);
        TransformFrames(doc, result, layer => Carry(layer, layer.Surface.Crop(x, y, w, h),
            live => live.Shifted(-x, -y)));
        foreach (var source in doc.DynamicTextZones)
        {
            int left = Math.Max(0, source.X - x), top = Math.Max(0, source.Y - y);
            int right = Math.Min(w, source.X + source.Width - x);
            int bottom = Math.Min(h, source.Y + source.Height - y);
            if (right <= left || bottom <= top) continue;
            var zone = source.Clone();
            zone.X = left; zone.Y = top; zone.Width = right - left; zone.Height = bottom - top;
            result.DynamicTextZones.Add(zone);
        }
        return result;
    }

    /// <summary>
    /// Smallest rectangle holding every non-transparent pixel of every layer in every frame, for
    /// Auto-Crop. Hidden layers count too, so cropping never cuts into content that is merely
    /// switched off. Returns null when the whole document is transparent.
    /// </summary>
    public static (int X, int Y, int W, int H)? ContentBounds(Document doc) => ContentBounds(doc, null);

    private static unsafe (int X, int Y, int W, int H)? ContentBounds(Document doc, Func<Layer, bool>? which)
    {
        int minX = doc.Width, minY = doc.Height, maxX = -1, maxY = -1;
        foreach (DocumentFrame frame in doc.Frames)
            foreach (Layer layer in frame.Layers)
            {
                if (which is not null && !which(layer)) continue;
                Surface surface = layer.Surface;
                for (int y = 0; y < surface.Height; y++)
                {
                    ColorBgra* row = (ColorBgra*)surface.GetRowPointer(y);
                    int first = -1, last = -1;
                    for (int x = 0; x < surface.Width; x++)
                        if (row[x].A != 0) { first = x; break; }
                    if (first < 0) continue;
                    for (int x = surface.Width - 1; x >= first; x--)
                        if (row[x].A != 0) { last = x; break; }
                    if (first < minX) minX = first;
                    if (last > maxX) maxX = last;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        return maxX < 0 ? null : (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>Default Auto-Crop tolerance: enough to absorb JPEG noise on a plain backdrop.</summary>
    public const int DefaultTrimTolerance = 24;

    /// <summary>
    /// What Auto-Crop keeps: first the transparent margins go (see <see cref="ContentBounds"/>),
    /// then any border of one uniform colour, read on the composited image of every frame. The
    /// border colour is the one most corners of what is left agree on (within
    /// <paramref name="tolerance"/>, per channel); if fewer than two corners agree there is no
    /// backdrop to trim. Each side then moves in while its whole row or column stays within the
    /// tolerance of that colour. Hidden layers are not part of the composite, so the trim never
    /// cuts into their content either. Returns null when the image is fully transparent.
    /// </summary>
    public static (int X, int Y, int W, int H)? TrimBounds(Document doc, int tolerance = DefaultTrimTolerance)
    {
        if (ContentBounds(doc) is not { } content) return null;
        if (tolerance < 0) return content;

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        int keep = doc.ActiveFrameIndex;
        try
        {
            for (int f = 0; f < doc.Frames.Count; f++)
            {
                if (doc.Frames.Count > 1) doc.SetActiveFrame(f);
                using var composite = doc.Flatten();
                var (x, y, w, h) = UniformBorderTrim(composite, content, tolerance);
                minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                maxX = Math.Max(maxX, x + w - 1); maxY = Math.Max(maxY, y + h - 1);
            }
        }
        finally
        {
            if (doc.Frames.Count > 1) doc.SetActiveFrame(keep);
        }
        if (maxX < 0) return content;
        if (ContentBounds(doc, layer => !layer.Visible) is { } hidden)
        {
            minX = Math.Min(minX, hidden.X); minY = Math.Min(minY, hidden.Y);
            maxX = Math.Max(maxX, hidden.X + hidden.W - 1); maxY = Math.Max(maxY, hidden.Y + hidden.H - 1);
        }
        return (minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    /// <summary>The part of <paramref name="area"/> left once a uniform-colour border is trimmed.</summary>
    public static unsafe (int X, int Y, int W, int H) UniformBorderTrim(Surface s, (int X, int Y, int W, int H) area, int tolerance)
    {
        var (x0, y0, w, h) = area;
        int x1 = x0 + w - 1, y1 = y0 + h - 1;
        if (w < 2 || h < 2) return area;

        ColorBgra[] corners = [s[x0, y0], s[x1, y0], s[x0, y1], s[x1, y1]];
        ColorBgra backdrop = default;
        int agree = 0;
        foreach (var candidate in corners)
        {
            int votes = 0;
            foreach (var other in corners) if (Near(candidate, other, tolerance)) votes++;
            if (votes > agree) { agree = votes; backdrop = candidate; }
        }
        if (agree < 2) return area;

        bool RowIsBackdrop(int y, int from, int to)
        {
            ColorBgra* row = (ColorBgra*)s.GetRowPointer(y);
            for (int x = from; x <= to; x++) if (!Near(row[x], backdrop, tolerance)) return false;
            return true;
        }
        bool ColumnIsBackdrop(int x, int from, int to)
        {
            for (int y = from; y <= to; y++) if (!Near(*s.GetPointPointer(x, y), backdrop, tolerance)) return false;
            return true;
        }

        while (y0 <= y1 && RowIsBackdrop(y0, x0, x1)) y0++;
        if (y0 > y1) return area;   // all backdrop: nothing to keep, so trim nothing
        while (RowIsBackdrop(y1, x0, x1)) y1--;
        while (ColumnIsBackdrop(x0, y0, y1)) x0++;
        while (ColumnIsBackdrop(x1, y0, y1)) x1--;
        return (x0, y0, x1 - x0 + 1, y1 - y0 + 1);
    }

    /// <summary>Same colour within a per-channel tolerance; two fully transparent pixels always match.</summary>
    private static bool Near(ColorBgra a, ColorBgra b, int tolerance)
    {
        if (a.A == 0 && b.A == 0) return true;
        return Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance
            && Math.Abs(a.B - b.B) <= tolerance && Math.Abs(a.A - b.A) <= tolerance;
    }

    /// <summary>Returns a new document scaled to (w,h); every layer is resampled.</summary>
    public static Document Resize(Document doc, int w, int h)
    {
        var result = NewTransformedDocument(doc, w, h);
        double sx = (double)w / doc.Width, sy = (double)h / doc.Height;
        TransformFrames(doc, result, layer => Carry(layer, layer.Surface.Resized(w, h),
            live => live.Scaled(sx, sy)));
        foreach (var source in doc.DynamicTextZones)
        {
            var zone = source.Clone();
            zone.X = (int)Math.Round(source.X * sx); zone.Y = (int)Math.Round(source.Y * sy);
            zone.Width = Math.Max(1, (int)Math.Round(source.Width * sx));
            zone.Height = Math.Max(1, (int)Math.Round(source.Height * sy));
            zone.FontSize = Math.Max(1, source.FontSize * (float)Math.Min(sx, sy));
            result.DynamicTextZones.Add(zone);
        }
        return result;
    }

    /// <summary>
    /// Returns a new document at (w,h) with the existing content placed per <paramref name="anchor"/>
    /// and padded with transparency - unlike <see cref="Resize"/>, nothing is rescaled, so this is
    /// Paint.NET's "Canvas Size" rather than its "Image Resize".
    /// </summary>
    public static Document ResizeCanvas(Document doc, int w, int h, CanvasAnchor anchor)
    {
        var (dx, dy) = AnchorOffset(doc.Width, doc.Height, w, h, anchor);

        var result = NewTransformedDocument(doc, w, h);
        TransformFrames(doc, result, layer =>
        {
            var placed = new Surface(w, h);
            SurfaceOps.ShiftInto(placed, layer.Surface, dx, dy);
            return Carry(layer, placed, live => live.Shifted(dx, dy));
        });
        foreach (var source in doc.DynamicTextZones)
        {
            var zone = source.Clone();
            zone.X += dx; zone.Y += dy;
            if (zone.X + zone.Width > 0 && zone.Y + zone.Height > 0 && zone.X < w && zone.Y < h)
                result.DynamicTextZones.Add(zone);
        }
        return result;
    }

    private static (int Dx, int Dy) AnchorOffset(int oldW, int oldH, int newW, int newH, CanvasAnchor anchor)
    {
        int dx = anchor switch
        {
            CanvasAnchor.TopLeft or CanvasAnchor.MiddleLeft or CanvasAnchor.BottomLeft => 0,
            CanvasAnchor.TopCenter or CanvasAnchor.MiddleCenter or CanvasAnchor.BottomCenter => (newW - oldW) / 2,
            _ => newW - oldW
        };
        int dy = anchor switch
        {
            CanvasAnchor.TopLeft or CanvasAnchor.TopCenter or CanvasAnchor.TopRight => 0,
            CanvasAnchor.MiddleLeft or CanvasAnchor.MiddleCenter or CanvasAnchor.MiddleRight => (newH - oldH) / 2,
            _ => newH - oldH
        };
        return (dx, dy);
    }

    public static void FlipHorizontal(Document doc)
    {
        foreach (DocumentFrame frame in doc.Frames)
            foreach (Layer layer in frame.Layers)
            {
                SurfaceOps.FlipHorizontal(layer.Surface);
                if (layer.LiveModel is { } live)
                    layer.LiveModel = live.WithSettings(live.Settings.FlippedHorizontally(doc.Width));
            }
        RefreshLive(doc);
    }

    public static void FlipVertical(Document doc)
    {
        foreach (DocumentFrame frame in doc.Frames)
            foreach (Layer layer in frame.Layers)
            {
                SurfaceOps.FlipVertical(layer.Surface);
                if (layer.LiveModel is { } live)
                    layer.LiveModel = live.WithSettings(live.Settings.FlippedVertically(doc.Height));
            }
        RefreshLive(doc);
    }

    /// <summary>Returns a new document rotated 90 degrees; canvas dimensions swap.</summary>
    public static Document Rotate90(Document doc, bool clockwise)
    {
        var result = NewTransformedDocument(doc, doc.Height, doc.Width);
        TransformFrames(doc, result, layer => Carry(layer, SurfaceOps.Rotate90(layer.Surface, clockwise),
            live => live.Rotated(clockwise ? 90 : -90, doc.Width, doc.Height, doc.Height, doc.Width)));
        foreach (var source in doc.DynamicTextZones)
        {
            var zone = source.Clone();
            if (clockwise)
            {
                zone.X = doc.Height - source.Y - source.Height;
                zone.Y = source.X;
            }
            else
            {
                zone.X = source.Y;
                zone.Y = doc.Width - source.X - source.Width;
            }
            zone.Width = source.Height;
            zone.Height = source.Width;
            result.DynamicTextZones.Add(zone);
        }
        return result;
    }

    /// <summary>Turns the document half a turn in place (canvas size is unchanged).</summary>
    public static void Rotate180(Document doc)
    {
        foreach (DocumentFrame frame in doc.Frames)
            foreach (Layer layer in frame.Layers)
            {
                SurfaceOps.FlipHorizontal(layer.Surface);
                SurfaceOps.FlipVertical(layer.Surface);
                if (layer.LiveModel is { } live)
                    layer.LiveModel = live.WithSettings(live.Settings.Rotated(180, doc.Width, doc.Height, doc.Width, doc.Height));
            }
        RefreshLive(doc);
        foreach (var zone in doc.DynamicTextZones)
        {
            zone.X = doc.Width - zone.X - zone.Width;
            zone.Y = doc.Height - zone.Y - zone.Height;
        }
    }

    /// <summary>
    /// Returns a new document turned <paramref name="degrees"/> clockwise about its centre. With
    /// <paramref name="expandCanvas"/> the canvas grows to hold the whole turned image (corners
    /// become transparent); otherwise it keeps its size and the corners are cut off.
    /// </summary>
    public static Document Rotate(Document doc, double degrees, bool expandCanvas)
    {
        var (w, h) = expandCanvas ? Surface.RotatedBounds(doc.Width, doc.Height, degrees) : (doc.Width, doc.Height);
        var result = NewTransformedDocument(doc, w, h);
        TransformFrames(doc, result, layer => Carry(layer, layer.Surface.Rotated(degrees, w, h),
            live => live.Rotated(degrees, doc.Width, doc.Height, w, h)));
        // Text zones stay upright: each keeps its size and follows its centre around the turn.
        double radians = degrees * Math.PI / 180, cos = Math.Cos(radians), sin = Math.Sin(radians);
        foreach (var source in doc.DynamicTextZones)
        {
            double cx = source.X + source.Width / 2.0 - doc.Width / 2.0;
            double cy = source.Y + source.Height / 2.0 - doc.Height / 2.0;
            var zone = source.Clone();
            zone.X = (int)Math.Round(w / 2.0 + cx * cos - cy * sin - source.Width / 2.0);
            zone.Y = (int)Math.Round(h / 2.0 + cx * sin + cy * cos - source.Height / 2.0);
            if (zone.X + zone.Width > 0 && zone.Y + zone.Height > 0 && zone.X < w && zone.Y < h)
                result.DynamicTextZones.Add(zone);
        }
        return result;
    }

    /// <summary>Returns a new single-layer document with all layers composited together.</summary>
    public static Document Flatten(Document doc)
    {
        var result = NewTransformedDocument(doc, doc.Width, doc.Height);
        int active = doc.ActiveFrameIndex;
        try
        {
            for (int frameIndex = 0; frameIndex < doc.FrameCount; frameIndex++)
            {
                doc.SetActiveFrame(frameIndex);
                var flattened = new Layer(doc.Flatten(), "Flattened");
                if (frameIndex == 0)
                {
                    result.ActiveFrame.Name = doc.ActiveFrame.Name;
                    result.ActiveFrame.DurationMs = doc.ActiveFrame.DurationMs;
                    result.AddLayer(flattened);
                }
                else result.AddFrame(new DocumentFrame(new[] { flattened }, doc.ActiveFrame.Name,
                    doc.ActiveFrame.DurationMs));
            }
        }
        finally { doc.SetActiveFrame(active); }
        result.SetActiveFrame(active);
        foreach (var zone in doc.DynamicTextZones) result.DynamicTextZones.Add(zone.Clone());
        return result;
    }

    private static void TransformFrames(Document source, Document destination, Func<Layer, Layer> transform)
    {
        for (int frameIndex = 0; frameIndex < source.FrameCount; frameIndex++)
        {
            DocumentFrame sourceFrame = source.Frames[frameIndex];
            if (frameIndex == 0)
            {
                destination.ActiveFrame.Name = sourceFrame.Name;
                destination.ActiveFrame.DurationMs = sourceFrame.DurationMs;
                foreach (Layer layer in sourceFrame.Layers) destination.AddLayer(transform(layer));
            }
            else
            {
                destination.AddFrame(new DocumentFrame(sourceFrame.Layers.Select(transform),
                    sourceFrame.Name, sourceFrame.DurationMs));
            }
        }
        destination.SetActiveFrame(source.ActiveFrameIndex);
        RefreshLive(destination);
    }

    /// <summary>
    /// The transformed copy of <paramref name="source"/> around <paramref name="surface"/>: same
    /// identity and compositing properties, and a live 3D model moved the way the canvas moved, so
    /// the layer stays live and is re-rendered sharp instead of resampled.
    /// </summary>
    private static Layer Carry(Layer source, Surface surface, Func<LiveModelSettings, LiveModelSettings> live) =>
        new(surface, source.Name)
        {
            Id = source.Id,
            LiveModel = source.LiveModel is { } model ? model.WithSettings(live(model.Settings)) : null,
            Opacity = source.Opacity,
            Visible = source.Visible,
            BlendMode = source.BlendMode
        };

    private static void RefreshLive(Document doc)
    {
        if (doc.Frames.Any(frame => frame.Layers.Any(layer => layer.LiveModel is not null)))
            LiveModelRenderer.RefreshAll(doc);
    }

    private static Document NewTransformedDocument(Document source, int width, int height) => new(width, height)
    {
        Dpi = source.Dpi,
        ExifTiff = source.ExifTiff?.ToArray()
    };
}
