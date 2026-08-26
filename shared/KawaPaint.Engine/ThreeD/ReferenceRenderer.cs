using System.Numerics;

namespace KawaPaint.Engine.ThreeD;

public sealed record ReferenceRenderOptions
{
    public float YawDegrees { get; init; } = -35;
    public float PitchDegrees { get; init; } = -25;
    public float RollDegrees { get; init; }
    public float PaddingFraction { get; init; } = 0.08f;
    public float Ambient { get; init; } = 0.28f;
    public ColorBgra Color { get; init; } = ColorBgra.FromBgr(195, 205, 220);
    public int Supersampling { get; init; } = 2;
}

/// <summary>Deterministic orthographic CPU renderer for rasterized 3D reference layers.</summary>
public static class ReferenceRenderer
{
    private const long MaxSupersampledPixels = 32_000_000;

    public static Surface Render(ObjMesh mesh, int width, int height, ReferenceRenderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(mesh);
        if (width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (height <= 0) throw new ArgumentOutOfRangeException(nameof(height));
        options ??= new ReferenceRenderOptions();

        int samples = Math.Clamp(options.Supersampling, 1, 4);
        while (samples > 1 && (long)width * height * samples * samples > MaxSupersampledPixels) samples--;
        int renderWidth = checked(width * samples);
        int renderHeight = checked(height * samples);
        using Surface highResolution = RenderCore(mesh, renderWidth, renderHeight, options);
        if (samples == 1) return highResolution.Clone();
        return Downsample(highResolution, width, height, samples);
    }

    private static Surface RenderCore(ObjMesh mesh, int width, int height, ReferenceRenderOptions options)
    {
        Matrix4x4 rotation = Matrix4x4.CreateRotationY(Degrees(options.YawDegrees)) *
                             Matrix4x4.CreateRotationX(Degrees(options.PitchDegrees)) *
                             Matrix4x4.CreateRotationZ(Degrees(options.RollDegrees));

        var transformed = new Vector3[mesh.Vertices.Count];
        Vector3 sourceMin = new(float.PositiveInfinity), sourceMax = new(float.NegativeInfinity);
        foreach (Vector3 vertex in mesh.Vertices)
        {
            sourceMin = Vector3.Min(sourceMin, vertex);
            sourceMax = Vector3.Max(sourceMax, vertex);
        }
        Vector3 center = (sourceMin + sourceMax) * 0.5f;

        Vector2 min = new(float.PositiveInfinity), max = new(float.NegativeInfinity);
        for (int i = 0; i < transformed.Length; i++)
        {
            Vector3 point = Vector3.Transform(mesh.Vertices[i] - center, rotation);
            transformed[i] = point;
            min = Vector2.Min(min, new Vector2(point.X, point.Y));
            max = Vector2.Max(max, new Vector2(point.X, point.Y));
        }

        float projectedWidth = max.X - min.X, projectedHeight = max.Y - min.Y;
        if (projectedWidth <= 1e-12f && projectedHeight <= 1e-12f)
            throw new InvalidDataException("OBJ has no renderable spatial extent.");
        float padding = Math.Clamp(options.PaddingFraction, 0, 0.45f);
        float availableWidth = width * (1 - 2 * padding);
        float availableHeight = height * (1 - 2 * padding);
        float scaleX = projectedWidth > 1e-12f ? availableWidth / projectedWidth : float.PositiveInfinity;
        float scaleY = projectedHeight > 1e-12f ? availableHeight / projectedHeight : float.PositiveInfinity;
        float scale = MathF.Min(scaleX, scaleY);
        float projectedCenterX = (min.X + max.X) * 0.5f;
        float projectedCenterY = (min.Y + max.Y) * 0.5f;

        for (int i = 0; i < transformed.Length; i++)
        {
            Vector3 p = transformed[i];
            transformed[i] = new Vector3(
                width * 0.5f + (p.X - projectedCenterX) * scale,
                height * 0.5f - (p.Y - projectedCenterY) * scale,
                p.Z);
        }

        Vector3[] generatedNormals = GenerateNormals(mesh);
        Vector3 light = Vector3.Normalize(new Vector3(-0.35f, 0.55f, 1));
        float ambient = Math.Clamp(options.Ambient, 0, 1);
        var depth = new float[checked(width * height)];
        Array.Fill(depth, float.NegativeInfinity);
        var surface = new Surface(width, height);

        foreach (ObjTriangle triangle in mesh.Triangles)
        {
            Vector3 a = transformed[triangle.A], b = transformed[triangle.B], c = transformed[triangle.C];
            float area = Edge(a, b, c.X, c.Y);
            if (MathF.Abs(area) < 1e-8f) continue;

            Vector3 na = RotateNormal(NormalFor(mesh, generatedNormals, triangle.NA, triangle.A), rotation);
            Vector3 nb = RotateNormal(NormalFor(mesh, generatedNormals, triangle.NB, triangle.B), rotation);
            Vector3 nc = RotateNormal(NormalFor(mesh, generatedNormals, triangle.NC, triangle.C), rotation);
            Rasterize(surface, depth, a, b, c, na, nb, nc, area, light, ambient, options.Color);
        }
        return surface;
    }

    private static Vector3[] GenerateNormals(ObjMesh mesh)
    {
        var result = new Vector3[mesh.Vertices.Count];
        foreach (ObjTriangle t in mesh.Triangles)
        {
            Vector3 normal = Vector3.Cross(mesh.Vertices[t.B] - mesh.Vertices[t.A],
                mesh.Vertices[t.C] - mesh.Vertices[t.A]);
            if (normal.LengthSquared() <= 1e-20f) continue;
            result[t.A] += normal; result[t.B] += normal; result[t.C] += normal;
        }
        for (int i = 0; i < result.Length; i++)
            result[i] = result[i].LengthSquared() > 1e-20f ? Vector3.Normalize(result[i]) : Vector3.UnitZ;
        return result;
    }

    private static Vector3 NormalFor(ObjMesh mesh, Vector3[] generated, int normalIndex, int vertexIndex) =>
        normalIndex >= 0 ? mesh.Normals[normalIndex] : generated[vertexIndex];

    private static Vector3 RotateNormal(Vector3 normal, Matrix4x4 rotation)
    {
        Vector3 rotated = Vector3.TransformNormal(normal, rotation);
        return rotated.LengthSquared() > 1e-20f ? Vector3.Normalize(rotated) : Vector3.UnitZ;
    }

    private static unsafe void Rasterize(Surface surface, float[] depth, Vector3 a, Vector3 b, Vector3 c,
        Vector3 na, Vector3 nb, Vector3 nc, float area, Vector3 light, float ambient, ColorBgra color)
    {
        int left = Math.Clamp((int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X))), 0, surface.Width - 1);
        int top = Math.Clamp((int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y))), 0, surface.Height - 1);
        int right = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))), 0, surface.Width - 1);
        int bottom = Math.Clamp((int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))), 0, surface.Height - 1);
        float inverseArea = 1 / area;

        for (int y = top; y <= bottom; y++)
        {
            ColorBgra* row = (ColorBgra*)surface.GetRowPointer(y);
            for (int x = left; x <= right; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float wa = Edge(b, c, px, py) * inverseArea;
                float wb = Edge(c, a, px, py) * inverseArea;
                float wc = 1 - wa - wb;
                if (wa < -1e-6f || wb < -1e-6f || wc < -1e-6f) continue;

                float z = wa * a.Z + wb * b.Z + wc * c.Z;
                int index = y * surface.Width + x;
                if (z <= depth[index]) continue;
                depth[index] = z;

                Vector3 normal = wa * na + wb * nb + wc * nc;
                if (normal.LengthSquared() > 1e-20f) normal = Vector3.Normalize(normal);
                if (normal.Z < 0) normal = -normal; // OBJ winding varies; references are double-sided.
                float intensity = ambient + (1 - ambient) * MathF.Max(0, Vector3.Dot(normal, light));
                row[x] = Shade(color, intensity);
            }
        }
    }

    private static float Edge(Vector3 a, Vector3 b, float x, float y) =>
        (x - a.X) * (b.Y - a.Y) - (y - a.Y) * (b.X - a.X);

    private static ColorBgra Shade(ColorBgra color, float intensity)
    {
        intensity = Math.Clamp(intensity, 0, 1);
        return ColorBgra.FromBgra((byte)MathF.Round(color.B * intensity),
            (byte)MathF.Round(color.G * intensity), (byte)MathF.Round(color.R * intensity), color.A);
    }

    private static unsafe Surface Downsample(Surface source, int width, int height, int samples)
    {
        var result = new Surface(width, height);
        int count = samples * samples;
        for (int y = 0; y < height; y++)
        {
            ColorBgra* dst = (ColorBgra*)result.GetRowPointer(y);
            for (int x = 0; x < width; x++)
            {
                int sumB = 0, sumG = 0, sumR = 0, sumA = 0;
                for (int sy = 0; sy < samples; sy++)
                {
                    ColorBgra* src = (ColorBgra*)source.GetRowPointer(y * samples + sy) + x * samples;
                    for (int sx = 0; sx < samples; sx++)
                    {
                        ColorBgra p = src[sx];
                        sumB += p.B * p.A; sumG += p.G * p.A; sumR += p.R * p.A; sumA += p.A;
                    }
                }
                int alpha = (sumA + count / 2) / count;
                dst[x] = sumA == 0 ? ColorBgra.Transparent : ColorBgra.FromBgra(
                    (byte)((sumB + sumA / 2) / sumA), (byte)((sumG + sumA / 2) / sumA),
                    (byte)((sumR + sumA / 2) / sumA), (byte)alpha);
            }
        }
        return result;
    }

    private static float Degrees(float value) => value * (MathF.PI / 180);
}
