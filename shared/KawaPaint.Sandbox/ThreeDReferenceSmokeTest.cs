using KawaPaint.Engine;
using KawaPaint.Engine.ThreeD;

namespace KawaPaint.Sandbox;

internal static class ThreeDReferenceSmokeTest
{
    public static void RunAll()
    {
        ParserTriangulatesAndResolvesIndices();
        ParserRejectsBadReferences();
        RendererIsDeterministicAntialiasedAndDepthBuffered();
        Console.WriteLine("3D REFERENCE SMOKE OK - OBJ parsing, normals, z-buffer, lighting, AA");
    }

    private static void ParserTriangulatesAndResolvesIndices()
    {
        const string obj = """
            # a quad using relative indices and explicit normals
            v -1 -1 0
            v  1 -1 0
            v  1  1 0
            v -1  1 0
            vn 0 0 2
            f -4//1 -3//1 -2//1 -1//1
            """;
        ObjMesh mesh = ObjMesh.Parse(new StringReader(obj));
        Check(mesh.Vertices.Count == 4, "OBJ vertex count changed");
        Check(mesh.Normals.Count == 1, "OBJ normal count changed");
        Check(mesh.Triangles.Count == 2, "quad was not fan-triangulated");
        Check(mesh.Triangles[0] == new ObjTriangle(0, 1, 2, 0, 0, 0),
            "negative OBJ indices resolved incorrectly");
        Check(Math.Abs(mesh.Normals[0].Length() - 1) < 0.0001f, "OBJ normal was not normalized");
    }

    private static void ParserRejectsBadReferences()
    {
        const string obj = "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 4\n";
        try
        {
            ObjMesh.Parse(new StringReader(obj));
            throw new InvalidOperationException("out-of-range OBJ index was accepted");
        }
        catch (InvalidDataException ex)
        {
            Check(ex.Message.Contains("line 4", StringComparison.OrdinalIgnoreCase),
                "OBJ parse error lost its line number");
        }
    }

    private static void RendererIsDeterministicAntialiasedAndDepthBuffered()
    {
        // Both triangles occupy the same projection. The bright +Z face is closer but deliberately
        // listed first; without a working z-buffer the later, sideways-normal rear face paints over it.
        const string obj = """
            v -1 -1  1
            v  1 -1  1
            v  0  1  1
            v -1 -1 -1
            v  1 -1 -1
            v  0  1 -1
            vn 0 0 1
            vn 1 0 0
            f 1//1 2//1 3//1
            f 4//2 5//2 6//2
            """;
        ObjMesh mesh = ObjMesh.Parse(new StringReader(obj));
        var options = new ReferenceRenderOptions
        {
            YawDegrees = 0,
            PitchDegrees = 0,
            RollDegrees = 0,
            Ambient = 0.2f,
            Color = ColorBgra.White,
            Supersampling = 2
        };
        using Surface first = ReferenceRenderer.Render(mesh, 80, 64, options);
        using Surface second = ReferenceRenderer.Render(mesh, 80, 64, options);

        int opaque = 0, partial = 0;
        for (int y = 0; y < first.Height; y++)
        for (int x = 0; x < first.Width; x++)
        {
            ColorBgra a = first[x, y], b = second[x, y];
            Check(a == b, $"3D render is not deterministic at ({x},{y})");
            if (a.A == 255) opaque++;
            else if (a.A > 0) partial++;
        }

        Check(opaque > 500, "renderer produced too little filled geometry");
        Check(partial > 20, "supersampling produced no meaningful antialiased silhouette");
        ColorBgra center = first[first.Width / 2, first.Height / 2];
        Check(center.A == 255 && center.R > 180,
            $"rear triangle won the depth test or lighting failed (center={center})");
        Check(first[0, 0].A == 0, "renderer did not preserve a transparent background");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
