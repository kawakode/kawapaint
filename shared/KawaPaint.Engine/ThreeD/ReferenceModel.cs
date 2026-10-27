using System.Numerics;

namespace KawaPaint.Engine.ThreeD;

public readonly record struct ModelTriangle(int A, int B, int C);

/// <summary>Geometry decoded from a scene-oriented format before conversion to the shared renderer mesh.</summary>
public sealed class ReferenceModel
{
    public ReferenceModel(IReadOnlyList<Vector3> vertices, IReadOnlyList<ModelTriangle> triangles,
        IReadOnlyList<Vector2>? texCoords = null)
    {
        if (vertices.Count == 0) throw new ArgumentException("A model needs at least one vertex.", nameof(vertices));
        if (triangles.Count == 0) throw new ArgumentException("A model needs at least one face.", nameof(triangles));
        if (texCoords is not null && texCoords.Count != vertices.Count)
            throw new ArgumentException("Texture coordinates must be given per vertex.", nameof(texCoords));
        Vertices = vertices;
        Triangles = triangles;
        TexCoords = texCoords;
    }

    public IReadOnlyList<Vector3> Vertices { get; }
    public IReadOnlyList<ModelTriangle> Triangles { get; }

    /// <summary>Per-vertex texture coordinates in image space (V down), or null when the model has
    /// none. A vertex whose primitive had no UVs holds NaN, and faces touching it stay unmapped.</summary>
    public IReadOnlyList<Vector2>? TexCoords { get; }

    public ObjMesh ToRenderMesh()
    {
        if (TexCoords is not { } uv)
            return ObjMesh.Create(Vertices, Triangles.Select(t => new ObjTriangle(t.A, t.B, t.C)));
        return ObjMesh.Create(Vertices, Triangles.Select(t =>
            float.IsNaN(uv[t.A].X) || float.IsNaN(uv[t.B].X) || float.IsNaN(uv[t.C].X)
                ? new ObjTriangle(t.A, t.B, t.C)
                : new ObjTriangle(t.A, t.B, t.C, TA: t.A, TB: t.B, TC: t.C)), uv);
    }
}
