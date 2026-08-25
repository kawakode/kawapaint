using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using System.Text.Json;

namespace KawaPaint.Engine.ThreeD;

/// <summary>Geometry-only glTF 2.0 reader for 3D reference rasterization. It accepts JSON glTF and
/// binary GLB, embedded data URIs, and optional caller-resolved external buffers. Materials,
/// textures, skins, morphs and animation do not survive a raster reference import and are ignored.</summary>
public static class GltfModelLoader
{
    private const uint GlbMagic = 0x46546C67;
    private const uint JsonChunk = 0x4E4F534A;
    private const uint BinChunk = 0x004E4942;
    private const int MaxFileBytes = 512 * 1024 * 1024;
    private const int MaxVertices = 5_000_000;
    private const int MaxTriangles = 10_000_000;

    public static ReferenceModel Load(Stream stream, Func<string, byte[]?>? externalBufferResolver = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] source = ReadAll(stream);
        byte[] json;
        byte[]? glbBinary = null;

        if (source.Length >= 12 && BinaryPrimitives.ReadUInt32LittleEndian(source) == GlbMagic)
            (json, glbBinary) = ReadGlb(source);
        else
            json = source;

        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 256
        });
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("asset", out JsonElement asset) ||
            !asset.TryGetProperty("version", out JsonElement version) ||
            !version.GetString()!.StartsWith("2", StringComparison.Ordinal))
            throw new InvalidDataException("Only glTF 2.x files are supported.");

        List<byte[]> buffers = ReadBuffers(root, glbBinary, externalBufferResolver);
        var outputVertices = new List<Vector3>();
        var outputTriangles = new List<ModelTriangle>();
        var reader = new GeometryReader(root, buffers, outputVertices, outputTriangles);
        reader.ReadScene();
        if (outputVertices.Count == 0 || outputTriangles.Count == 0)
            throw new InvalidDataException("glTF contains no supported triangle geometry.");
        return new ReferenceModel(outputVertices, outputTriangles);
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var memory = new MemoryStream();
        var buffer = new byte[81920];
        int total = 0, read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            total = checked(total + read);
            if (total > MaxFileBytes) throw new InvalidDataException("glTF file is too large.");
            memory.Write(buffer, 0, read);
        }
        return memory.ToArray();
    }

    private static (byte[] Json, byte[]? Binary) ReadGlb(byte[] source)
    {
        uint version = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(4));
        uint declaredLength = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(8));
        if (version != 2 || declaredLength != source.Length)
            throw new InvalidDataException("Invalid or unsupported GLB header.");

        byte[]? json = null, binary = null;
        int offset = 12;
        while (offset < source.Length)
        {
            if (source.Length - offset < 8) throw new InvalidDataException("Truncated GLB chunk header.");
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset + 4));
            offset += 8;
            if (length > int.MaxValue || offset > source.Length - (int)length)
                throw new InvalidDataException("Truncated GLB chunk.");
            byte[] chunk = source.AsSpan(offset, (int)length).ToArray();
            offset += (int)length;
            if (type == JsonChunk && json is null) json = chunk;
            else if (type == BinChunk && binary is null) binary = chunk;
        }
        return (json ?? throw new InvalidDataException("GLB has no JSON chunk."), binary);
    }

    private static List<byte[]> ReadBuffers(JsonElement root, byte[]? glbBinary,
        Func<string, byte[]?>? externalResolver)
    {
        var result = new List<byte[]>();
        if (!root.TryGetProperty("buffers", out JsonElement buffers)) return result;
        int index = 0;
        foreach (JsonElement buffer in buffers.EnumerateArray())
        {
            byte[]? data = null;
            if (buffer.TryGetProperty("uri", out JsonElement uriElement))
            {
                string uri = uriElement.GetString() ?? "";
                data = uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
                    ? ReadDataUri(uri)
                    : externalResolver?.Invoke(Uri.UnescapeDataString(uri));
                if (data is null)
                    throw new InvalidDataException($"glTF buffer '{uri}' is external and could not be opened.");
            }
            else if (index == 0)
                data = glbBinary;

            if (data is null) throw new InvalidDataException($"glTF buffer {index} has no data.");
            int expected = RequiredInt(buffer, "byteLength");
            if (expected < 0 || data.Length < expected)
                throw new InvalidDataException($"glTF buffer {index} is shorter than its declared byteLength.");
            result.Add(data);
            index++;
        }
        return result;
    }

    private static byte[] ReadDataUri(string uri)
    {
        int comma = uri.IndexOf(',');
        if (comma < 0) throw new InvalidDataException("Malformed glTF data URI.");
        string header = uri[..comma];
        string payload = uri[(comma + 1)..];
        try
        {
            return header.EndsWith(";base64", StringComparison.OrdinalIgnoreCase)
                ? Convert.FromBase64String(payload)
                : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(payload));
        }
        catch (Exception ex) when (ex is FormatException or UriFormatException)
        {
            throw new InvalidDataException("Malformed glTF data URI.", ex);
        }
    }

    private sealed class GeometryReader
    {
        private readonly JsonElement _root;
        private readonly List<byte[]> _buffers;
        private readonly List<Vector3> _vertices;
        private readonly List<ModelTriangle> _triangles;

        public GeometryReader(JsonElement root, List<byte[]> buffers, List<Vector3> vertices,
            List<ModelTriangle> triangles)
        {
            _root = root; _buffers = buffers; _vertices = vertices; _triangles = triangles;
        }

        public void ReadScene()
        {
            if (!_root.TryGetProperty("meshes", out _)) return;
            if (!_root.TryGetProperty("nodes", out JsonElement nodes) || nodes.GetArrayLength() == 0)
            {
                JsonElement meshes = _root.GetProperty("meshes");
                for (int i = 0; i < meshes.GetArrayLength(); i++) ReadMesh(i, Matrix4x4.Identity);
                return;
            }

            var roots = new List<int>();
            if (_root.TryGetProperty("scenes", out JsonElement scenes) && scenes.GetArrayLength() > 0)
            {
                int sceneIndex = _root.TryGetProperty("scene", out JsonElement active) ? active.GetInt32() : 0;
                JsonElement scene = At(scenes, sceneIndex, "scene");
                if (scene.TryGetProperty("nodes", out JsonElement sceneNodes))
                    foreach (JsonElement node in sceneNodes.EnumerateArray()) roots.Add(node.GetInt32());
            }
            else
            {
                var children = new HashSet<int>();
                foreach (JsonElement node in nodes.EnumerateArray())
                    if (node.TryGetProperty("children", out JsonElement list))
                        foreach (JsonElement child in list.EnumerateArray()) children.Add(child.GetInt32());
                for (int i = 0; i < nodes.GetArrayLength(); i++) if (!children.Contains(i)) roots.Add(i);
            }

            var path = new HashSet<int>();
            foreach (int node in roots) ReadNode(node, Matrix4x4.Identity, path, 0);
        }

        private void ReadNode(int index, Matrix4x4 parent, HashSet<int> path, int depth)
        {
            if (depth > 256 || !path.Add(index)) throw new InvalidDataException("glTF node graph contains a cycle.");
            JsonElement node = At(_root.GetProperty("nodes"), index, "node");
            Matrix4x4 world = LocalTransform(node) * parent;
            if (node.TryGetProperty("mesh", out JsonElement mesh)) ReadMesh(mesh.GetInt32(), world);
            if (node.TryGetProperty("children", out JsonElement children))
                foreach (JsonElement child in children.EnumerateArray())
                    ReadNode(child.GetInt32(), world, path, depth + 1);
            path.Remove(index);
        }

        private void ReadMesh(int index, Matrix4x4 transform)
        {
            JsonElement mesh = At(_root.GetProperty("meshes"), index, "mesh");
            if (!mesh.TryGetProperty("primitives", out JsonElement primitives)) return;
            foreach (JsonElement primitive in primitives.EnumerateArray()) ReadPrimitive(primitive, transform);
        }

        private void ReadPrimitive(JsonElement primitive, Matrix4x4 transform)
        {
            int mode = primitive.TryGetProperty("mode", out JsonElement modeElement) ? modeElement.GetInt32() : 4;
            if (mode is not (4 or 5 or 6)) return; // points and lines do not make a filled reference
            if (!primitive.TryGetProperty("attributes", out JsonElement attributes) ||
                !attributes.TryGetProperty("POSITION", out JsonElement positionAccessor))
                throw new InvalidDataException("glTF triangle primitive has no POSITION attribute.");

            Vector3[] positions = ReadPositions(positionAccessor.GetInt32());
            int baseVertex = _vertices.Count;
            if ((long)baseVertex + positions.Length > MaxVertices)
                throw new InvalidDataException("glTF has too many vertices.");
            foreach (Vector3 p in positions)
            {
                Vector3 transformed = Vector3.Transform(p, transform);
                if (!float.IsFinite(transformed.X) || !float.IsFinite(transformed.Y) || !float.IsFinite(transformed.Z))
                    throw new InvalidDataException("glTF node transform produced a non-finite vertex.");
                _vertices.Add(transformed);
            }

            uint[] indices = primitive.TryGetProperty("indices", out JsonElement indexAccessor)
                ? ReadIndices(indexAccessor.GetInt32())
                : Enumerable.Range(0, positions.Length).Select(i => (uint)i).ToArray();
            foreach (uint value in indices)
                if (value >= positions.Length) throw new InvalidDataException("glTF index is outside its POSITION accessor.");

            if (mode == 4)
            {
                if (indices.Length % 3 != 0) throw new InvalidDataException("glTF TRIANGLES index count is not divisible by three.");
                for (int i = 0; i < indices.Length; i += 3) AddTriangle(baseVertex, indices[i], indices[i + 1], indices[i + 2]);
            }
            else if (mode == 5)
            {
                for (int i = 2; i < indices.Length; i++)
                    if ((i & 1) == 0) AddTriangle(baseVertex, indices[i - 2], indices[i - 1], indices[i]);
                    else AddTriangle(baseVertex, indices[i - 1], indices[i - 2], indices[i]);
            }
            else
            {
                for (int i = 2; i < indices.Length; i++) AddTriangle(baseVertex, indices[0], indices[i - 1], indices[i]);
            }
        }

        private void AddTriangle(int offset, uint a, uint b, uint c)
        {
            if (a == b || b == c || a == c) return;
            if (_triangles.Count >= MaxTriangles) throw new InvalidDataException("glTF has too many triangles.");
            _triangles.Add(new ModelTriangle(offset + (int)a, offset + (int)b, offset + (int)c));
        }

        private Vector3[] ReadPositions(int accessorIndex)
        {
            Accessor a = GetAccessor(accessorIndex);
            if (a.ComponentType != 5126 || a.Type != "VEC3")
                throw new InvalidDataException("glTF POSITION must use FLOAT VEC3 data.");
            var result = new Vector3[a.Count];
            for (int i = 0; i < result.Length; i++)
            {
                ReadOnlySpan<byte> item = a.Item(i, 12);
                float x = Float(item), y = Float(item[4..]), z = Float(item[8..]);
                if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z))
                    throw new InvalidDataException("glTF POSITION contains a non-finite value.");
                result[i] = new Vector3(x, y, z);
            }
            return result;
        }

        private uint[] ReadIndices(int accessorIndex)
        {
            Accessor a = GetAccessor(accessorIndex);
            if (a.Type != "SCALAR" || a.ComponentType is not (5121 or 5123 or 5125))
                throw new InvalidDataException("glTF indices must be unsigned SCALAR data.");
            int size = a.ComponentType == 5121 ? 1 : a.ComponentType == 5123 ? 2 : 4;
            var result = new uint[a.Count];
            for (int i = 0; i < result.Length; i++)
            {
                ReadOnlySpan<byte> item = a.Item(i, size);
                result[i] = size switch
                {
                    1 => item[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(item),
                    _ => BinaryPrimitives.ReadUInt32LittleEndian(item)
                };
            }
            return result;
        }

        private Accessor GetAccessor(int index)
        {
            JsonElement accessor = At(_root.GetProperty("accessors"), index, "accessor");
            if (accessor.TryGetProperty("sparse", out _))
                throw new InvalidDataException("Sparse glTF accessors are not supported for reference import.");
            int viewIndex = RequiredInt(accessor, "bufferView");
            JsonElement view = At(_root.GetProperty("bufferViews"), viewIndex, "bufferView");
            int bufferIndex = RequiredInt(view, "buffer");
            if ((uint)bufferIndex >= (uint)_buffers.Count) throw new InvalidDataException("glTF buffer index is out of range.");
            int componentType = RequiredInt(accessor, "componentType");
            int count = RequiredInt(accessor, "count");
            if (count < 0 || count > MaxVertices * 3) throw new InvalidDataException("glTF accessor count is out of range.");
            string type = accessor.GetProperty("type").GetString() ?? "";
            int viewOffset = OptionalInt(view, "byteOffset");
            int accessorOffset = OptionalInt(accessor, "byteOffset");
            int viewLength = RequiredInt(view, "byteLength");
            int elementSize = ComponentSize(componentType) * Components(type);
            int stride = view.TryGetProperty("byteStride", out JsonElement strideElement)
                ? strideElement.GetInt32() : elementSize;
            if (stride < elementSize || viewOffset < 0 || accessorOffset < 0 || viewLength < 0)
                throw new InvalidDataException("glTF accessor layout is invalid.");
            long used = count == 0 ? accessorOffset : (long)accessorOffset + (long)stride * (count - 1) + elementSize;
            if (used > viewLength || (long)viewOffset + viewLength > _buffers[bufferIndex].Length)
                throw new InvalidDataException("glTF accessor exceeds its bufferView.");
            return new Accessor(_buffers[bufferIndex], checked(viewOffset + accessorOffset), stride,
                count, componentType, type);
        }

        private static Matrix4x4 LocalTransform(JsonElement node)
        {
            if (node.TryGetProperty("matrix", out JsonElement matrix))
            {
                float[] m = matrix.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                if (m.Length != 16) throw new InvalidDataException("glTF node matrix must have 16 values.");
                return new Matrix4x4(m[0], m[1], m[2], m[3], m[4], m[5], m[6], m[7],
                    m[8], m[9], m[10], m[11], m[12], m[13], m[14], m[15]);
            }
            Vector3 scale = ReadVector3(node, "scale", Vector3.One);
            Vector3 translation = ReadVector3(node, "translation", Vector3.Zero);
            Quaternion rotation = Quaternion.Identity;
            if (node.TryGetProperty("rotation", out JsonElement r))
            {
                float[] q = r.EnumerateArray().Select(v => v.GetSingle()).ToArray();
                if (q.Length != 4) throw new InvalidDataException("glTF node rotation must have four values.");
                rotation = Quaternion.Normalize(new Quaternion(q[0], q[1], q[2], q[3]));
            }
            return Matrix4x4.CreateScale(scale) * Matrix4x4.CreateFromQuaternion(rotation) *
                   Matrix4x4.CreateTranslation(translation);
        }

        private static Vector3 ReadVector3(JsonElement node, string name, Vector3 fallback)
        {
            if (!node.TryGetProperty(name, out JsonElement element)) return fallback;
            float[] v = element.EnumerateArray().Select(n => n.GetSingle()).ToArray();
            if (v.Length != 3) throw new InvalidDataException($"glTF node {name} must have three values.");
            return new Vector3(v[0], v[1], v[2]);
        }

        private readonly record struct Accessor(byte[] Data, int Offset, int Stride, int Count,
            int ComponentType, string Type)
        {
            public ReadOnlySpan<byte> Item(int index, int length) => Data.AsSpan(checked(Offset + index * Stride), length);
        }
    }

    private static float Float(ReadOnlySpan<byte> data)
        => BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data));

    private static int RequiredInt(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value)
            ? value.GetInt32() : throw new InvalidDataException($"glTF object is missing required '{name}'.");

    private static int OptionalInt(JsonElement element, string name)
        => element.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : 0;

    private static JsonElement At(JsonElement array, int index, string name)
    {
        if ((uint)index >= (uint)array.GetArrayLength()) throw new InvalidDataException($"glTF {name} index is out of range.");
        return array[index];
    }

    private static int ComponentSize(int componentType) => componentType switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => throw new InvalidDataException($"Unsupported glTF componentType {componentType}.")
    };

    private static int Components(string type) => type switch
    {
        "SCALAR" => 1, "VEC2" => 2, "VEC3" => 3, "VEC4" or "MAT2" => 4,
        "MAT3" => 9, "MAT4" => 16,
        _ => throw new InvalidDataException($"Unsupported glTF accessor type '{type}'.")
    };
}
