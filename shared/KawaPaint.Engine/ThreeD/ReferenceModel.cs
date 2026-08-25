using System.Globalization;
using System.Numerics;

namespace KawaPaint.Engine.ThreeD;

public readonly record struct ModelTriangle(int A, int B, int C);

public sealed class ReferenceModel
{
    public ReferenceModel(IReadOnlyList<Vector3> vertices, IReadOnlyList<ModelTriangle> triangles)
    {
        if (vertices.Count == 0) throw new ArgumentException("A model needs at least one vertex.", nameof(vertices));
        if (triangles.Count == 0) throw new ArgumentException("A model needs at least one face.", nameof(triangles));
        Vertices = vertices;
        Triangles = triangles;
    }

    public IReadOnlyList<Vector3> Vertices { get; }
    public IReadOnlyList<ModelTriangle> Triangles { get; }
}

/// <summary>Minimal, defensive Wavefront OBJ reader for raster reference layers. Geometry is all
/// that survives the import, so texture coordinates, materials and smoothing groups are skipped;
/// polygon faces are fan-triangulated and positive or relative vertex indices are accepted.</summary>
public static class ObjModelLoader
{
    private const int MaxVertices = 5_000_000;
    private const int MaxTriangles = 10_000_000;

    public static ReferenceModel Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, leaveOpen: true);
        return Load(reader);
    }

    public static ReferenceModel Load(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var vertices = new List<Vector3>();
        var triangles = new List<ModelTriangle>();
        string? line;
        int lineNumber = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            ReadOnlySpan<char> span = line.AsSpan().Trim();
            if (span.Length == 0 || span[0] == '#') continue;

            if (span.StartsWith("v ", StringComparison.Ordinal))
            {
                string[] fields = span[2..].ToString().Split((char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (fields.Length < 3 ||
                    !float.TryParse(fields[0], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) ||
                    !float.TryParse(fields[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) ||
                    !float.TryParse(fields[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float z) ||
                    !float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                    throw new InvalidDataException($"OBJ line {lineNumber} has an invalid vertex.");
                if (vertices.Count >= MaxVertices) throw new InvalidDataException("OBJ has too many vertices.");
                vertices.Add(new Vector3(x, y, z));
                continue;
            }

            if (!span.StartsWith("f ", StringComparison.Ordinal)) continue;
            string[] corners = span[2..].ToString().Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (corners.Length < 3)
                throw new InvalidDataException($"OBJ line {lineNumber} has fewer than three face vertices.");

            int first = ParseVertexIndex(corners[0], vertices.Count, lineNumber);
            int previous = ParseVertexIndex(corners[1], vertices.Count, lineNumber);
            for (int i = 2; i < corners.Length; i++)
            {
                int current = ParseVertexIndex(corners[i], vertices.Count, lineNumber);
                if (triangles.Count >= MaxTriangles) throw new InvalidDataException("OBJ has too many triangles.");
                if (first != previous && previous != current && first != current)
                    triangles.Add(new ModelTriangle(first, previous, current));
                previous = current;
            }
        }

        if (vertices.Count == 0) throw new InvalidDataException("OBJ contains no vertices.");
        if (triangles.Count == 0) throw new InvalidDataException("OBJ contains no polygon faces.");
        return new ReferenceModel(vertices, triangles);
    }

    private static int ParseVertexIndex(string corner, int vertexCount, int lineNumber)
    {
        int slash = corner.IndexOf('/');
        ReadOnlySpan<char> indexText = slash < 0 ? corner.AsSpan() : corner.AsSpan(0, slash);
        if (!int.TryParse(indexText, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw) || raw == 0)
            throw new InvalidDataException($"OBJ line {lineNumber} has an invalid face index.");
        int index = raw > 0 ? raw - 1 : vertexCount + raw;
        if ((uint)index >= (uint)vertexCount)
            throw new InvalidDataException($"OBJ line {lineNumber} references vertex {raw}, which is out of range.");
        return index;
    }
}

public sealed class ReferenceRenderOptions
{
    public double YawDegrees { get; set; } = 35;
    public double PitchDegrees { get; set; } = -25;
    public double RollDegrees { get; set; }
    public double MarginFraction { get; set; } = 0.08;
    public ColorBgra Color { get; set; } = ColorBgra.FromBgr(205, 205, 215);
    public bool ShowEdges { get; set; } = true;
}

/// <summary>Small CPU orthographic renderer used only to turn a 3D reference into ordinary pixels.
/// It deliberately owns no GPU or UI state: after import, the returned Surface is a normal layer.</summary>
public static class ReferenceModelRenderer
{
    private readonly record struct Projected(float X, float Y, float Z);

    public static Surface Render(ReferenceModel model, int width, int height, ReferenceRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException();
        options ??= new ReferenceRenderOptions();

        Matrix4x4 rotation = Matrix4x4.CreateRotationY(Rad(options.YawDegrees)) *
                             Matrix4x4.CreateRotationX(Rad(options.PitchDegrees)) *
                             Matrix4x4.CreateRotationZ(Rad(options.RollDegrees));
        var rotated = new Vector3[model.Vertices.Count];
        float minX = float.PositiveInfinity, minY = float.PositiveInfinity;
        float maxX = float.NegativeInfinity, maxY = float.NegativeInfinity;
        for (int i = 0; i < rotated.Length; i++)
        {
            Vector3 v = Vector3.Transform(model.Vertices[i], rotation);
            rotated[i] = v;
            minX = Math.Min(minX, v.X); maxX = Math.Max(maxX, v.X);
            minY = Math.Min(minY, v.Y); maxY = Math.Max(maxY, v.Y);
        }

        double margin = Math.Clamp(options.MarginFraction, 0, 0.45);
        double usableWidth = Math.Max(1, width * (1 - 2 * margin));
        double usableHeight = Math.Max(1, height * (1 - 2 * margin));
        double spanX = Math.Max(1e-9, maxX - minX), spanY = Math.Max(1e-9, maxY - minY);
        double scale = Math.Min(usableWidth / spanX, usableHeight / spanY);
        double centerX = (minX + maxX) * 0.5, centerY = (minY + maxY) * 0.5;

        var projected = new Projected[rotated.Length];
        for (int i = 0; i < rotated.Length; i++)
            projected[i] = new Projected(
                (float)((rotated[i].X - centerX) * scale + width * 0.5),
                (float)(height * 0.5 - (rotated[i].Y - centerY) * scale),
                rotated[i].Z);

        var surface = new Surface(width, height);
        var depth = new float[checked(width * height)];
        Array.Fill(depth, float.NegativeInfinity);
        Vector3 light = Vector3.Normalize(new Vector3(-0.35f, 0.55f, 1));

        foreach (ModelTriangle triangle in model.Triangles)
        {
            Projected a = projected[triangle.A], b = projected[triangle.B], c = projected[triangle.C];
            Vector3 normal = Vector3.Cross(rotated[triangle.B] - rotated[triangle.A],
                                           rotated[triangle.C] - rotated[triangle.A]);
            if (normal.LengthSquared() < 1e-16f) continue;
            normal = Vector3.Normalize(normal);
            double brightness = 0.22 + 0.78 * Math.Abs(Vector3.Dot(normal, light));
            ColorBgra color = Shade(options.Color, brightness);
            FillTriangle(surface, depth, a, b, c, color);
        }

        if (options.ShowEdges)
        {
            ColorBgra edge = Shade(options.Color, 0.16);
            foreach (ModelTriangle t in model.Triangles)
            {
                DrawEdge(surface, depth, projected[t.A], projected[t.B], edge);
                DrawEdge(surface, depth, projected[t.B], projected[t.C], edge);
                DrawEdge(surface, depth, projected[t.C], projected[t.A], edge);
            }
        }

        return surface;
    }

    private static float Rad(double degrees) => (float)(degrees * Math.PI / 180.0);

    private static ColorBgra Shade(ColorBgra color, double amount) => ColorBgra.FromBgra(
        (byte)Math.Clamp(Math.Round(color.B * amount), 0, 255),
        (byte)Math.Clamp(Math.Round(color.G * amount), 0, 255),
        (byte)Math.Clamp(Math.Round(color.R * amount), 0, 255), color.A);

    private static float Edge(Projected a, Projected b, float x, float y)
        => (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);

    private static unsafe void FillTriangle(Surface surface, float[] depth, Projected a, Projected b,
        Projected c, ColorBgra color)
    {
        float area = Edge(a, b, c.X, c.Y);
        if (Math.Abs(area) < 1e-8f) return;
        int minX = Math.Clamp((int)Math.Floor(Math.Min(a.X, Math.Min(b.X, c.X))), 0, surface.Width - 1);
        int maxX = Math.Clamp((int)Math.Ceiling(Math.Max(a.X, Math.Max(b.X, c.X))), 0, surface.Width - 1);
        int minY = Math.Clamp((int)Math.Floor(Math.Min(a.Y, Math.Min(b.Y, c.Y))), 0, surface.Height - 1);
        int maxY = Math.Clamp((int)Math.Ceiling(Math.Max(a.Y, Math.Max(b.Y, c.Y))), 0, surface.Height - 1);

        for (int y = minY; y <= maxY; y++)
        {
            ColorBgra* row = (ColorBgra*)surface.GetRowPointer(y);
            for (int x = minX; x <= maxX; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float wa = Edge(b, c, px, py) / area;
                float wb = Edge(c, a, px, py) / area;
                float wc = 1 - wa - wb;
                if (wa < -1e-5f || wb < -1e-5f || wc < -1e-5f) continue;
                float z = wa * a.Z + wb * b.Z + wc * c.Z;
                int index = y * surface.Width + x;
                if (z < depth[index]) continue;
                depth[index] = z;
                row[x] = color;
            }
        }
    }

    private static unsafe void DrawEdge(Surface surface, float[] depth, Projected a, Projected b, ColorBgra color)
    {
        float dx = b.X - a.X, dy = b.Y - a.Y;
        int steps = Math.Max(1, (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy))));
        for (int i = 0; i <= steps; i++)
        {
            float t = (float)i / steps;
            int x = (int)Math.Round(a.X + dx * t), y = (int)Math.Round(a.Y + dy * t);
            if ((uint)x >= (uint)surface.Width || (uint)y >= (uint)surface.Height) continue;
            float z = a.Z + (b.Z - a.Z) * t;
            int index = y * surface.Width + x;
            if (z + 1e-4f < depth[index]) continue;
            *((ColorBgra*)surface.GetRowPointer(y) + x) = color;
        }
    }
}
