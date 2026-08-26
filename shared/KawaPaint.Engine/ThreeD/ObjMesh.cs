using System.Globalization;
using System.Numerics;

namespace KawaPaint.Engine.ThreeD;

public readonly record struct ObjTriangle(int A, int B, int C, int NA = -1, int NB = -1, int NC = -1);

/// <summary>A deliberately small, dependency-free OBJ mesh: positions, normals and triangulated faces.</summary>
public sealed class ObjMesh
{
    private const int MaxVertices = 5_000_000;
    private const int MaxTriangles = 10_000_000;

    public IReadOnlyList<Vector3> Vertices { get; }
    public IReadOnlyList<Vector3> Normals { get; }
    public IReadOnlyList<ObjTriangle> Triangles { get; }

    private ObjMesh(List<Vector3> vertices, List<Vector3> normals, List<ObjTriangle> triangles)
    {
        Vertices = vertices;
        Normals = normals;
        Triangles = triangles;
    }

    public static ObjMesh Load(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        using var reader = new StreamReader(stream, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return Parse(reader);
    }

    public static ObjMesh Parse(TextReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        var vertices = new List<Vector3>();
        var normals = new List<Vector3>();
        var triangles = new List<ObjTriangle>();
        string? line;
        int lineNumber = 0;

        while ((line = reader.ReadLine()) is not null)
        {
            lineNumber++;
            ReadOnlySpan<char> span = line.AsSpan().Trim();
            if (span.IsEmpty || span[0] == '#') continue;
            int comment = span.IndexOf('#');
            if (comment >= 0) span = span[..comment].TrimEnd();

            string[] parts = span.ToString().Split((char[]?)null,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length == 0) continue;

            if (parts[0] == "v")
            {
                if (parts.Length < 4) throw Error(lineNumber, "vertex needs x, y and z");
                if (vertices.Count >= MaxVertices) throw Error(lineNumber, $"vertex limit ({MaxVertices:N0}) exceeded");
                vertices.Add(new Vector3(Number(parts[1], lineNumber), Number(parts[2], lineNumber),
                    Number(parts[3], lineNumber)));
            }
            else if (parts[0] == "vn")
            {
                if (parts.Length < 4) throw Error(lineNumber, "normal needs x, y and z");
                Vector3 normal = new(Number(parts[1], lineNumber), Number(parts[2], lineNumber),
                    Number(parts[3], lineNumber));
                normals.Add(normal.LengthSquared() > 1e-20f ? Vector3.Normalize(normal) : Vector3.UnitZ);
            }
            else if (parts[0] == "f")
            {
                if (parts.Length < 4) throw Error(lineNumber, "face needs at least three vertices");
                var corners = new (int Vertex, int Normal)[parts.Length - 1];
                for (int i = 1; i < parts.Length; i++)
                    corners[i - 1] = ParseCorner(parts[i], vertices.Count, normals.Count, lineNumber);

                for (int i = 1; i + 1 < corners.Length; i++)
                {
                    if (triangles.Count >= MaxTriangles)
                        throw Error(lineNumber, $"triangle limit ({MaxTriangles:N0}) exceeded");
                    triangles.Add(new ObjTriangle(corners[0].Vertex, corners[i].Vertex, corners[i + 1].Vertex,
                        corners[0].Normal, corners[i].Normal, corners[i + 1].Normal));
                }
            }
        }

        if (vertices.Count == 0) throw new InvalidDataException("OBJ contains no vertices.");
        if (triangles.Count == 0) throw new InvalidDataException("OBJ contains no faces.");
        return new ObjMesh(vertices, normals, triangles);
    }

    private static (int Vertex, int Normal) ParseCorner(string token, int vertexCount, int normalCount,
        int lineNumber)
    {
        string[] fields = token.Split('/');
        if (fields.Length > 3 || fields[0].Length == 0)
            throw Error(lineNumber, $"invalid face corner '{token}'");

        int vertex = ResolveIndex(fields[0], vertexCount, "vertex", lineNumber);
        int normal = fields.Length == 3 && fields[2].Length > 0
            ? ResolveIndex(fields[2], normalCount, "normal", lineNumber)
            : -1;
        return (vertex, normal);
    }

    private static int ResolveIndex(string text, int count, string kind, int lineNumber)
    {
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int raw) || raw == 0)
            throw Error(lineNumber, $"invalid {kind} index '{text}'");
        int index = raw > 0 ? raw - 1 : count + raw;
        if ((uint)index >= (uint)count) throw Error(lineNumber, $"{kind} index '{text}' is out of range");
        return index;
    }

    private static float Number(string text, int lineNumber)
    {
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float value) ||
            !float.IsFinite(value))
            throw Error(lineNumber, $"invalid finite number '{text}'");
        return value;
    }

    private static InvalidDataException Error(int line, string message) =>
        new($"OBJ line {line}: {message}.");
}
