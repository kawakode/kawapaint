// KawaPaint - document-level operations that produce a new Document.

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
        var result = new Document(w, h) { Dpi = doc.Dpi };
        foreach (var layer in doc.Layers)
        {
            var cropped = layer.Surface.Crop(x, y, w, h);
            result.AddLayer(new Layer(cropped, layer.Name)
            {
                Opacity = layer.Opacity,
                Visible = layer.Visible,
                BlendMode = layer.BlendMode
            });
        }
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

    /// <summary>Returns a new document scaled to (w,h); every layer is resampled.</summary>
    public static Document Resize(Document doc, int w, int h)
    {
        var result = new Document(w, h) { Dpi = doc.Dpi };
        foreach (var layer in doc.Layers)
        {
            result.AddLayer(new Layer(layer.Surface.Resized(w, h), layer.Name)
            {
                Opacity = layer.Opacity,
                Visible = layer.Visible,
                BlendMode = layer.BlendMode
            });
        }
        double sx = (double)w / doc.Width, sy = (double)h / doc.Height;
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

        var result = new Document(w, h) { Dpi = doc.Dpi };
        foreach (var layer in doc.Layers)
        {
            var placed = new Surface(w, h);
            SurfaceOps.ShiftInto(placed, layer.Surface, dx, dy);
            result.AddLayer(new Layer(placed, layer.Name)
            {
                Opacity = layer.Opacity,
                Visible = layer.Visible,
                BlendMode = layer.BlendMode
            });
        }
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
        foreach (var layer in doc.Layers) SurfaceOps.FlipHorizontal(layer.Surface);
    }

    public static void FlipVertical(Document doc)
    {
        foreach (var layer in doc.Layers) SurfaceOps.FlipVertical(layer.Surface);
    }

    /// <summary>Returns a new document rotated 90 degrees; canvas dimensions swap.</summary>
    public static Document Rotate90(Document doc, bool clockwise)
    {
        var result = new Document(doc.Height, doc.Width) { Dpi = doc.Dpi };
        foreach (var layer in doc.Layers)
        {
            result.AddLayer(new Layer(SurfaceOps.Rotate90(layer.Surface, clockwise), layer.Name)
            {
                Opacity = layer.Opacity,
                Visible = layer.Visible,
                BlendMode = layer.BlendMode
            });
        }
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

    /// <summary>Returns a new single-layer document with all layers composited together.</summary>
    public static Document Flatten(Document doc)
    {
        var result = new Document(doc.Width, doc.Height) { Dpi = doc.Dpi };
        result.AddLayer(new Layer(doc.Flatten(), "Flattened"));
        foreach (var zone in doc.DynamicTextZones) result.DynamicTextZones.Add(zone.Clone());
        return result;
    }
}
