using System.Numerics;

namespace KawaPaint.Engine.ThreeD;

public readonly record struct ModelTriangle(int A, int B, int C);

/// <summary>Geometry decoded from a scene-oriented format before conversion to the shared renderer mesh.</summary>
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

    public ObjMesh ToRenderMesh() => ObjMesh.Create(Vertices,
        Triangles.Select(t => new ObjTriangle(t.A, t.B, t.C)));
}
