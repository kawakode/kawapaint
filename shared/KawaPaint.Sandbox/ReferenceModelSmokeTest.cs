using KawaPaint.Engine.ThreeD;

namespace KawaPaint.Sandbox;

internal static class ReferenceModelSmokeTest
{
    private const string Pyramid = """
        # square pyramid: the base uses relative indices and all faces use v/vt/vn-shaped corners
        v -1 -1 0
        v  1 -1 0
        v  1  1 0
        v -1  1 0
        v  0  0 2
        f -5/-1/-1 -4/-1/-1 -3/-1/-1 -2/-1/-1
        f 1 2 5
        f 2 3 5
        f 3 4 5
        f 4 1 5
        """;

    public static void RunAll()
    {
        ReferenceModel model = ObjModelLoader.Load(new StringReader(Pyramid));
        Assert(model.Vertices.Count == 5 && model.Triangles.Count == 6,
            $"OBJ triangulation mismatch ({model.Vertices.Count} vertices, {model.Triangles.Count} triangles)");

        using var first = ReferenceModelRenderer.Render(model, 128, 96);
        using var second = ReferenceModelRenderer.Render(model, 128, 96,
            new ReferenceRenderOptions { YawDegrees = -55, PitchDegrees = 15, ShowEdges = false });
        int painted = 0, different = 0;
        for (int y = 0; y < first.Height; y++)
        for (int x = 0; x < first.Width; x++)
        {
            if (first[x, y].A != 0) painted++;
            if (first[x, y] != second[x, y]) different++;
        }
        Assert(painted > 1500, $"renderer produced too little visible geometry ({painted} pixels)");
        Assert(different > 1000, $"camera pose did not materially change the render ({different} pixels)");

        AssertThrows(() => ObjModelLoader.Load(new StringReader("v 0 0 0\nf 1 2 3\n")),
            "out-of-range OBJ index was accepted");

        byte[] binary = BuildGeometryBuffer();
        byte[] json = BuildGltf(binary, embedded: true);
        using var jsonStream = new MemoryStream(json);
        ReferenceModel gltf = GltfModelLoader.Load(jsonStream);
        Assert(gltf.Vertices.Count == 4 && gltf.Triangles.Count == 4,
            "embedded glTF geometry count mismatch");
        Assert(gltf.Vertices.All(v => v.X >= 3), "glTF node translation was not applied");

        using var glbStream = new MemoryStream(BuildGlb(BuildGltf(binary, embedded: false), binary));
        ReferenceModel glb = GltfModelLoader.Load(glbStream);
        Assert(glb.Vertices.Count == 4 && glb.Triangles.Count == 4, "GLB geometry count mismatch");

        Console.WriteLine("3D REFERENCE SMOKE OK - OBJ/glTF/GLB, transforms, camera pose, z-buffered raster");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("3D reference smoke test: " + message);
    }

    private static void AssertThrows(Action action, string message)
    {
        try { action(); }
        catch (InvalidDataException) { return; }
        throw new InvalidOperationException("3D reference smoke test: " + message);
    }

    private static byte[] BuildGeometryBuffer()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        foreach ((float x, float y, float z) in new[]
        {
            (0f, 0f, 0f), (2f, 0f, 0f), (0f, 2f, 0f), (0.4f, 0.6f, 2f)
        })
        {
            writer.Write(x); writer.Write(y); writer.Write(z);
        }
        foreach (ushort index in new ushort[] { 0, 1, 2, 0, 1, 3, 1, 2, 3, 2, 0, 3 }) writer.Write(index);
        return stream.ToArray();
    }

    private static byte[] BuildGltf(byte[] binary, bool embedded)
    {
        string uri = embedded ? $",\"uri\":\"data:application/octet-stream;base64,{Convert.ToBase64String(binary)}\"" : "";
        string json = $$"""
            {"asset":{"version":"2.0"},"buffers":[{"byteLength":{{binary.Length}}{{uri}}}],
             "bufferViews":[{"buffer":0,"byteOffset":0,"byteLength":48},{"buffer":0,"byteOffset":48,"byteLength":24}],
             "accessors":[{"bufferView":0,"componentType":5126,"count":4,"type":"VEC3","min":[0,0,0],"max":[2,2,2]},
                          {"bufferView":1,"componentType":5123,"count":12,"type":"SCALAR"}],
             "meshes":[{"primitives":[{"attributes":{"POSITION":0},"indices":1}]}],
             "nodes":[{"mesh":0,"translation":[3,0,0]}],"scenes":[{"nodes":[0]}],"scene":0}
            """;
        return System.Text.Encoding.UTF8.GetBytes(json);
    }

    private static byte[] BuildGlb(byte[] json, byte[] binary)
    {
        int jsonLength = (json.Length + 3) & ~3;
        int binaryLength = (binary.Length + 3) & ~3;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(0x46546C67u); writer.Write(2u);
        writer.Write((uint)(12 + 8 + jsonLength + 8 + binaryLength));
        writer.Write((uint)jsonLength); writer.Write(0x4E4F534Au); writer.Write(json);
        for (int i = json.Length; i < jsonLength; i++) writer.Write((byte)' ');
        writer.Write((uint)binaryLength); writer.Write(0x004E4942u); writer.Write(binary);
        for (int i = binary.Length; i < binaryLength; i++) writer.Write((byte)0);
        return stream.ToArray();
    }
}
