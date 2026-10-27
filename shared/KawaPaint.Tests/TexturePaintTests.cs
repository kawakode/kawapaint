using System.Numerics;
using KawaPaint.Engine;
using KawaPaint.Engine.ThreeD;
using Xunit;

namespace KawaPaint.Tests;

public class TexturePaintTests
{
    // A square facing the camera at yaw = pitch = 0, mapped to the whole texture with its top-left
    // corner at texture (0,0). OBJ's V runs upwards, hence vt 0 1 on the top-left vertex.
    private const string Plane = """
        v -1 -1 0
        v  1 -1 0
        v  1  1 0
        v -1  1 0
        vt 0 0
        vt 1 0
        vt 1 1
        vt 0 1
        f 1/1 2/2 3/3 4/4
        """;

    private static readonly ReferenceRenderOptions FrontView = new()
    {
        YawDegrees = 0, PitchDegrees = 0, PaddingFraction = 0, Supersampling = 1, Ambient = 1
    };

    private static ObjMesh PlaneMesh() => ObjMesh.Parse(new StringReader(Plane));

    private static Surface HalfAndHalf(int size = 16)
    {
        var texture = new Surface(size, size);
        texture.ClearRect(0, 0, size / 2, size, ColorBgra.FromBgra(0, 0, 255, 255));        // red left
        texture.ClearRect(size / 2, 0, size / 2, size, ColorBgra.FromBgra(255, 0, 0, 255)); // blue right
        return texture;
    }

    [Fact]
    public void ObjTextureCoordinatesAreReadInImageSpace()
    {
        ObjMesh mesh = PlaneMesh();
        Assert.True(mesh.HasTexCoords);
        Assert.Equal(4, mesh.TexCoords.Count);
        Assert.Equal(new Vector2(0, 0), mesh.TexCoords[3]);      // top-left vertex, V flipped
        Assert.All(mesh.Triangles, t => Assert.True(t.HasTexCoords));
        Assert.False(ObjMesh.Parse(new StringReader("v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n")).HasTexCoords);
    }

    [Fact]
    public void ObjRejectsOutOfRangeTextureCoordinates() =>
        Assert.Throws<InvalidDataException>(() =>
            ObjMesh.Parse(new StringReader("v 0 0 0\nv 1 0 0\nv 0 1 0\nvt 0 0\nf 1/1 2/2 3/1\n")));

    [Fact]
    public void RenderShowsTheTextureAndMapsPixelsBackToIt()
    {
        using Surface texture = HalfAndHalf();
        using Surface render = ReferenceRenderer.Render(PlaneMesh(), 40, 40, FrontView with { Texture = texture },
            out UvMap map);
        ColorBgra left = render[10, 20], right = render[30, 20];
        Assert.True(left.R > 200 && left.B < 30, "left half should be red: " + left);
        Assert.True(right.B > 200 && right.R < 30, "right half should be blue: " + right);

        Assert.True(map.TryGet(10.5, 5.5, out Vector2 uv));
        Assert.InRange(uv.X, 0.2f, 0.3f);
        Assert.InRange(uv.Y, 0.1f, 0.2f);
        Assert.False(map.TryGet(-1, 5, out _));
    }

    [Fact]
    public void TransparentTexelsShowTheBareModel()
    {
        using var texture = new Surface(8, 8);
        using Surface render = ReferenceRenderer.Render(PlaneMesh(), 20, 20, FrontView with { Texture = texture });
        Assert.Equal(new ReferenceRenderOptions().Color.Bgra, render[10, 10].Bgra);
    }

    [Fact]
    public void ZoomMagnifiesTheModel()
    {
        using Surface fitted = ReferenceRenderer.Render(PlaneMesh(), 40, 40, FrontView with { PaddingFraction = 0.25f });
        using Surface zoomed = ReferenceRenderer.Render(PlaneMesh(), 40, 40, FrontView with { PaddingFraction = 0.25f, Zoom = 2 });
        Assert.Equal(0, fitted[2, 2].A);
        Assert.Equal(255, zoomed[2, 2].A);
    }

    [Fact]
    public void DabsWrapAcrossTextureEdges()
    {
        using var texture = new Surface(20, 20);
        var changed = TexturePainter.Dab(texture, new Vector2(0, 0.5f), 3, ColorBgra.FromBgra(0, 255, 0, 255));
        Assert.Equal(255, texture[0, 10].A);
        Assert.Equal(255, texture[19, 10].A);
        Assert.Equal(0, texture[10, 10].A);
        Assert.Equal(2, changed.Count);
    }

    [Fact]
    public void UvLayoutOutlinesMappedFaces()
    {
        using Surface layout = TexturePainter.RenderUvLayout(PlaneMesh(), 32, 32, ColorBgra.FromBgra(0, 0, 0, 255));
        // The quad splits along the diagonal x + y = 32 in texture pixels.
        Assert.True(layout[15, 16].A + layout[16, 16].A + layout[16, 15].A > 0, "the quad's diagonal should be drawn");
        Assert.Equal(0, layout[8, 8].A);
        Assert.Equal(0, layout[24, 24].A);
    }

    [Fact]
    public void GltfTextureCoordinatesSurviveLoading()
    {
        ReferenceModel model = GltfModelLoader.Load(new MemoryStream(TexturedTriangleGltf()));
        Assert.NotNull(model.TexCoords);
        Assert.Equal(new Vector2(1, 0.5f), model.TexCoords![1]);
        ObjMesh mesh = model.ToRenderMesh();
        Assert.True(mesh.HasTexCoords);
    }

    private static byte[] TexturedTriangleGltf()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach (float value in new float[] { 0, 0, 0, 1, 0, 0, 0, 1, 0 }) writer.Write(value);
        foreach (float value in new float[] { 0, 0, 1, 0.5f, 0, 1 }) writer.Write(value);
        byte[] binary = stream.ToArray();
        string json = $$$"""
            {"asset":{"version":"2.0"},
             "buffers":[{"byteLength":{{{binary.Length}}},"uri":"data:application/octet-stream;base64,{{{Convert.ToBase64String(binary)}}}"}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":36},{"buffer":0,"byteOffset":36,"byteLength":24}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":3,"type":"VEC3"},
                          {"bufferView":1,"componentType":5126,"count":3,"type":"VEC2"}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0,"TEXCOORD_0":1}}]}]}
            """;
        return System.Text.Encoding.UTF8.GetBytes(json);
    }
}
