using System.Numerics;

namespace InstantMeshPlus.Core;

public enum TargetMode
{
    FaceCount,
    VertexCount,
    EdgeLength
}

public enum GuideMode
{
    Orientation,
    EdgePath
}

public enum FieldKind
{
    Orientation,
    Position
}

public sealed record RemeshSettings
{
    public TargetMode TargetMode { get; init; } = TargetMode.FaceCount;
    public float TargetValue { get; init; } = 1_000;
    public int OrientationSymmetry { get; init; } = 4;
    public int PositionSymmetry { get; init; } = 4;
    public bool Extrinsic { get; init; } = true;
    public bool AlignBoundaries { get; init; } = true;
    public float CreaseAngleDegrees { get; init; } = 90;
    public int SmoothIterations { get; init; } = 2;
    public bool PureOutput { get; init; } = true;
    public bool Deterministic { get; init; }
    public int ThreadCount { get; init; } = -1;
    public float ProjectionTolerance { get; init; }

    public void Validate()
    {
        if (!float.IsFinite(TargetValue) || TargetValue <= 0)
            throw new ArgumentOutOfRangeException(nameof(TargetValue), "Target value must be finite and greater than zero.");
        if (OrientationSymmetry is not (2 or 4 or 6))
            throw new ArgumentOutOfRangeException(nameof(OrientationSymmetry), "Orientation symmetry must be 2, 4, or 6.");
        if (PositionSymmetry is not (4 or 6))
            throw new ArgumentOutOfRangeException(nameof(PositionSymmetry), "Position symmetry must be 4 (quads) or 6 (triangles).");
        if (!float.IsFinite(CreaseAngleDegrees) || CreaseAngleDegrees is < -1 or > 180)
            throw new ArgumentOutOfRangeException(nameof(CreaseAngleDegrees), "Crease angle must be -1 (disabled) or between 0 and 180 degrees.");
        if (SmoothIterations is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(SmoothIterations), "Smoothing iterations must be between 0 and 100.");
        if (ThreadCount is 0 or < -1)
            throw new ArgumentOutOfRangeException(nameof(ThreadCount), "Thread count must be -1 (automatic) or greater than zero.");
        if (!float.IsFinite(ProjectionTolerance) || ProjectionTolerance < 0)
            throw new ArgumentOutOfRangeException(nameof(ProjectionTolerance), "Projection tolerance must be finite and non-negative.");
    }
}

public sealed record TriangleMesh(
    IReadOnlyList<Vector3> Vertices,
    IReadOnlyList<uint> TriangleIndices)
{
    public int TriangleCount => TriangleIndices.Count / 3;

    public void Validate()
    {
        if (Vertices.Count < 3)
            throw new ArgumentException("A mesh needs at least three vertices.", nameof(Vertices));
        if (TriangleIndices.Count == 0 || TriangleIndices.Count % 3 != 0)
            throw new ArgumentException("Triangle indices must contain complete triangles.", nameof(TriangleIndices));
        if (Vertices.Any(v => !IsFinite(v)))
            throw new ArgumentException("Mesh vertices must be finite.", nameof(Vertices));
        if (TriangleIndices.Any(i => i >= Vertices.Count))
            throw new ArgumentException("A triangle index is outside the vertex array.", nameof(TriangleIndices));
        for (var i = 0; i < TriangleIndices.Count; i += 3)
        {
            var a = TriangleIndices[i];
            var b = TriangleIndices[i + 1];
            var c = TriangleIndices[i + 2];
            if (a == b || b == c || c == a)
                throw new ArgumentException("A triangle contains repeated vertex indices.", nameof(TriangleIndices));
        }
    }

    internal static bool IsFinite(Vector3 value) =>
        float.IsFinite(value.X) && float.IsFinite(value.Y) && float.IsFinite(value.Z);
}

public sealed record OrientationConstraint(Vector3 Point, Vector3 Direction, float Weight = 1);

public sealed record PositionConstraint(Vector3 Point, Vector3 Anchor, float Weight = 1);

public sealed record CurveGuide(
    IReadOnlyList<Vector3> Points,
    GuideMode Mode = GuideMode.Orientation,
    float Weight = 1);

public sealed record SingularityPath(
    IReadOnlyList<Vector3> Points,
    FieldKind Field = FieldKind.Orientation);

public sealed record RemeshRequest
{
    public required TriangleMesh Mesh { get; init; }
    public RemeshSettings Settings { get; init; } = new();
    public IReadOnlyList<OrientationConstraint> OrientationConstraints { get; init; } = [];
    public IReadOnlyList<PositionConstraint> PositionConstraints { get; init; } = [];
    public IReadOnlyList<CurveGuide> Guides { get; init; } = [];
    public IReadOnlyList<SingularityPath> SingularityPaths { get; init; } = [];

    public void Validate()
    {
        Mesh.Validate();
        Settings.Validate();
        foreach (var constraint in OrientationConstraints)
        {
            ValidatePoint(constraint.Point, "Orientation constraint point");
            ValidatePoint(constraint.Direction, "Orientation constraint direction");
            ValidateWeight(constraint.Weight, "Orientation constraint weight");
        }
        foreach (var constraint in PositionConstraints)
        {
            ValidatePoint(constraint.Point, "Position constraint point");
            ValidatePoint(constraint.Anchor, "Position constraint anchor");
            ValidateWeight(constraint.Weight, "Position constraint weight");
        }
        foreach (var guide in Guides)
        {
            if (guide.Points.Count < 2)
                throw new ArgumentException("Curve guides require at least two sample points.", nameof(Guides));
            if (guide.Points.Any(p => !TriangleMesh.IsFinite(p)))
                throw new ArgumentException("Curve guide points must be finite.", nameof(Guides));
            ValidateWeight(guide.Weight, "Curve guide weight");
        }
        foreach (var path in SingularityPaths)
        {
            if (path.Points.Count < 2)
                throw new ArgumentException("Singularity paths require at least two sample points.", nameof(SingularityPaths));
            if (path.Points.Any(p => !TriangleMesh.IsFinite(p)))
                throw new ArgumentException("Singularity path points must be finite.", nameof(SingularityPaths));
        }
    }

    private static void ValidatePoint(Vector3 point, string label)
    {
        if (!TriangleMesh.IsFinite(point))
            throw new ArgumentException($"{label} must be finite.");
    }

    private static void ValidateWeight(float weight, string label)
    {
        if (!float.IsFinite(weight) || weight is < 0 or > 1)
            throw new ArgumentOutOfRangeException(label, "Constraint weights must be between 0 and 1.");
    }
}

public sealed record FieldSample(
    Vector3 Point,
    Vector3 Normal,
    Vector3 Orientation,
    Vector3 Position);

public sealed record OrientationSingularity(uint Face, int Index, Vector3 Point);

public sealed record PositionSingularity(uint Face, int IndexU, int IndexV, Vector3 Point);

public sealed record RemeshDiagnostics(
    float Scale,
    int AppliedOrientationConstraints,
    int AppliedPositionConstraints,
    int RejectedConstraints,
    IReadOnlyList<string> Warnings,
    IReadOnlyDictionary<string, double> TimingsMilliseconds);

public sealed record RemeshResult(
    IReadOnlyList<Vector3> Vertices,
    IReadOnlyList<uint> FaceIndices,
    int FaceArity,
    IReadOnlyList<FieldSample> Field,
    IReadOnlyList<OrientationSingularity> OrientationSingularities,
    IReadOnlyList<PositionSingularity> PositionSingularities,
    RemeshDiagnostics Diagnostics)
{
    public int FaceCount => FaceArity == 0 ? 0 : FaceIndices.Count / FaceArity;
}

public sealed record WorkerProgress(string Phase, float Progress);
