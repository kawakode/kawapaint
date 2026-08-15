// KawaPaint — document-level operations that produce a new Document.

namespace KawaPaint.Engine;

public static class DocumentOps
{
    /// <summary>Returns a new document cropped to the (x,y,w,h) region; every layer is cropped in place.</summary>
    public static Document Crop(Document doc, int x, int y, int w, int h)
    {
        var result = new Document(w, h);
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
        return result;
    }

    /// <summary>Returns a new single-layer document with all layers composited together.</summary>
    public static Document Flatten(Document doc)
    {
        var result = new Document(doc.Width, doc.Height);
        result.AddLayer(new Layer(doc.Flatten(), "Flattened"));
        return result;
    }
}
