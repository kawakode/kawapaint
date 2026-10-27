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
        var result = NewTransformedDocument(doc, w, h);
        TransformFrames(doc, result, layer =>
            new Layer(layer.Surface.Crop(x, y, w, h), layer.Name)
            {
                Opacity = layer.Opacity,
                Visible = layer.Visible,
                BlendMode = layer.BlendMode
            });
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
        var result = NewTransformedDocument(doc, w, h);
        TransformFrames(doc, result, layer =>
            new Layer(layer.Surface.Resized(w, h), layer.Name)
            {
                Opacity = layer.Opacity,
                Visible = layer.Visible,
                BlendMode = layer.BlendMode
            });
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

        var result = NewTransformedDocument(doc, w, h);
        TransformFrames(doc, result, layer =>
        {
            var placed = new Surface(w, h);
            SurfaceOps.ShiftInto(placed, layer.Surface, dx, dy);
            return new Layer(placed, layer.Name)
            {
                Opacity = layer.Opacity,
                Visible = layer.Visible,
                BlendMode = layer.BlendMode
            };
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
            foreach (Layer layer in frame.Layers) SurfaceOps.FlipHorizontal(layer.Surface);
    }

    public static void FlipVertical(Document doc)
    {
        foreach (DocumentFrame frame in doc.Frames)
            foreach (Layer layer in frame.Layers) SurfaceOps.FlipVertical(layer.Surface);
    }

    /// <summary>Returns a new document rotated 90 degrees; canvas dimensions swap.</summary>
    public static Document Rotate90(Document doc, bool clockwise)
    {
        var result = NewTransformedDocument(doc, doc.Height, doc.Width);
        TransformFrames(doc, result, layer =>
            new Layer(SurfaceOps.Rotate90(layer.Surface, clockwise), layer.Name)
            {
                Opacity = layer.Opacity,
                Visible = layer.Visible,
                BlendMode = layer.BlendMode
            });
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
            }
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
        TransformFrames(doc, result, layer =>
            new Layer(layer.Surface.Rotated(degrees, w, h), layer.Name)
            {
                Opacity = layer.Opacity,
                Visible = layer.Visible,
                BlendMode = layer.BlendMode
            });
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
    }

    private static Document NewTransformedDocument(Document source, int width, int height) => new(width, height)
    {
        Dpi = source.Dpi,
        ExifTiff = source.ExifTiff?.ToArray()
    };
}
