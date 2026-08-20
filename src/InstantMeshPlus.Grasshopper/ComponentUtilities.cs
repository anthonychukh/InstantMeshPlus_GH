using System.Numerics;
using Grasshopper.Kernel.Types;
using InstantMeshPlus.Core;
using Rhino.Geometry;

namespace InstantMeshPlus.Grasshopper;

internal static class ComponentUtilities
{
    public const string Category = "Instant Mesh+";

    public static T? Unwrap<T>(object? value) where T : class
    {
        if (value is T direct)
            return direct;
        return value is GH_ObjectWrapper wrapper ? wrapper.Value as T : null;
    }

    public static IReadOnlyList<T> UnwrapMany<T>(IEnumerable<object> values) where T : class =>
        values
            .Select(Unwrap<T>)
            .Where(value => value is not null)
            .Cast<T>()
            .ToArray();

    public static TriangleMesh ToCoreMesh(Mesh source)
    {
        var mesh = source.DuplicateMesh();
        mesh.Faces.ConvertQuadsToTriangles();
        mesh.Compact();
        var vertices = mesh.Vertices
            .Select(vertex => new Vector3(vertex.X, vertex.Y, vertex.Z))
            .ToArray();
        var triangles = new uint[mesh.Faces.Count * 3];
        for (var faceIndex = 0; faceIndex < mesh.Faces.Count; faceIndex++)
        {
            var face = mesh.Faces[faceIndex];
            triangles[faceIndex * 3] = checked((uint)face.A);
            triangles[faceIndex * 3 + 1] = checked((uint)face.B);
            triangles[faceIndex * 3 + 2] = checked((uint)face.C);
        }
        return new TriangleMesh(vertices, triangles);
    }

    public static Mesh ToRhinoMesh(RemeshResult result)
    {
        var mesh = new Mesh();
        foreach (var vertex in result.Vertices)
            mesh.Vertices.Add(vertex.X, vertex.Y, vertex.Z);
        for (var offset = 0; offset < result.FaceIndices.Count; offset += result.FaceArity)
        {
            if (result.FaceArity == 4)
            {
                mesh.Faces.AddFace(
                    checked((int)result.FaceIndices[offset]),
                    checked((int)result.FaceIndices[offset + 1]),
                    checked((int)result.FaceIndices[offset + 2]),
                    checked((int)result.FaceIndices[offset + 3]));
            }
            else
            {
                mesh.Faces.AddFace(
                    checked((int)result.FaceIndices[offset]),
                    checked((int)result.FaceIndices[offset + 1]),
                    checked((int)result.FaceIndices[offset + 2]));
            }
        }
        mesh.Normals.ComputeNormals();
        mesh.Compact();
        return mesh;
    }

    public static IReadOnlyList<Vector3> SampleCurveByCount(Curve curve, int sampleCount)
    {
        if (curve.TryGetPolyline(out var polyline) && polyline.Count >= 2)
            return polyline.Select(ToVector).ToArray();

        sampleCount = Math.Clamp(sampleCount, 2, 10_000);
        var parameters = curve.DivideByCount(sampleCount - 1, includeEnds: true);
        if (parameters is null || parameters.Length < 2)
            throw new ArgumentException("The curve could not be sampled.");
        return parameters.Select(parameter => ToVector(curve.PointAt(parameter))).ToArray();
    }

    public static IReadOnlyList<Vector3> SampleCurveByDeviation(Curve curve, double deviationTolerance)
    {
        if (!curve.IsValid)
            throw new ArgumentException("Supply a valid guide curve.", nameof(curve));
        if (!double.IsFinite(deviationTolerance) || deviationTolerance <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(deviationTolerance),
                "Deviation tolerance must be a finite number greater than zero.");
        if (curve.TryGetPolyline(out var existingPolyline) && existingPolyline.Count >= 2)
            return existingPolyline.Select(ToVector).ToArray();

        using var polylineCurve = curve.ToPolyline(
            mainSegmentCount: 0,
            subSegmentCount: 0,
            maxAngleRadians: 0,
            maxChordLengthRatio: 0,
            maxAspectRatio: 0,
            tolerance: deviationTolerance,
            minEdgeLength: 0,
            maxEdgeLength: 0,
            keepStartPoint: true);
        if (polylineCurve is null ||
            !polylineCurve.TryGetPolyline(out var sampledPolyline) ||
            sampledPolyline.Count < 2)
            throw new ArgumentException("The curve could not be sampled within the deviation tolerance.");
        return sampledPolyline.Select(ToVector).ToArray();
    }

    public static Vector3 ToVector(Point3d point) =>
        new((float)point.X, (float)point.Y, (float)point.Z);

    public static Vector3 ToVector(Vector3d vector) =>
        new((float)vector.X, (float)vector.Y, (float)vector.Z);

    public static Point3d ToPoint(Vector3 vector) => new(vector.X, vector.Y, vector.Z);
}
