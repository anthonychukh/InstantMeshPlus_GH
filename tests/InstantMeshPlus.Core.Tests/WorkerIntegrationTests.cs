using System.Numerics;
using InstantMeshPlus.Core;

namespace InstantMeshPlus.Core.Tests;

public sealed class WorkerIntegrationTests
{
    [Theory(Timeout = 120_000)]
    [InlineData(4, false, 0.5f, 0)]
    [InlineData(4, true, 0.5f, 0)]
    [InlineData(4, false, 1.0f, 2)]
    [InlineData(4, true, 1.0f, 2)]
    [InlineData(6, false, 0.5f, 0)]
    [InlineData(6, true, 0.5f, 0)]
    [InlineData(6, false, 1.0f, 2)]
    [InlineData(6, true, 1.0f, 2)]
    public async Task EdgeLengthTargetsFinalMeshEdges(
        int symmetry, bool pureOutput, float target, int smoothing)
    {
        // A flat, uniformly guided patch isolates sizing from curvature and singularities.
        var vertices = new List<Vector3>();
        var triangles = new List<uint>();
        const int cells = 32;
        const float size = 8;
        for (var y = 0; y <= cells; y++)
            for (var x = 0; x <= cells; x++)
                vertices.Add(new Vector3(size * x / cells, size * y / cells, 0));
        for (var y = 0; y < cells; y++)
            for (var x = 0; x < cells; x++)
            {
                var a = (uint)(y * (cells + 1) + x);
                var b = a + 1;
                var c = a + cells + 1;
                triangles.AddRange([a, b, c + 1, a, c + 1, c]);
            }

        var result = await new WorkerRunner().RunAsync(
            new RemeshRequest
            {
                Mesh = new TriangleMesh(vertices, triangles),
                Settings = new RemeshSettings
                {
                    TargetMode = TargetMode.EdgeLength,
                    TargetValue = target,
                    OrientationSymmetry = symmetry,
                    PositionSymmetry = symmetry,
                    PureOutput = pureOutput,
                    SmoothIterations = smoothing,
                    Deterministic = true,
                    ThreadCount = 1
                },
                OrientationConstraints = vertices.Select(p =>
                    new OrientationConstraint(p, Vector3.UnitX)).ToArray()
            },
            new WorkerOptions { ExecutablePath = FindWorker(), Timeout = TimeSpan.FromMinutes(1) },
            cancellationToken: TestContext.Current.CancellationToken);

        // Measure unique interior edges of the returned mesh, not the reported field scale.
        var edges = new HashSet<(uint, uint)>();
        var lengths = new List<float>();
        for (var face = 0; face < result.FaceIndices.Count; face += result.FaceArity)
            for (var corner = 0; corner < result.FaceArity; corner++)
            {
                var a = result.FaceIndices[face + corner];
                var b = result.FaceIndices[face + (corner + 1) % result.FaceArity];
                if (a == b || !edges.Add((Math.Min(a, b), Math.Max(a, b))))
                    continue;
                var p = result.Vertices[(int)a];
                var q = result.Vertices[(int)b];
                if (Interior(p) && Interior(q))
                    lengths.Add(Vector3.Distance(p, q));
            }
        Assert.True(lengths.Count >= 10, "Expected enough interior edges to measure sizing.");
        lengths.Sort();
        var median = lengths[lengths.Count / 2];
        TestContext.Current.TestOutputHelper!.WriteLine(
            $"Target={target}, median={median}, mean={lengths.Average()}, field scale={result.Diagnostics.Scale}");
        Assert.True(median >= target * 0.85f && median <= target * 1.15f,
            $"PoSy={symmetry}, Pure={pureOutput}, target={target}, median={median}, " +
            $"mean={lengths.Average()}, edges={lengths.Count}, field scale={result.Diagnostics.Scale}");

        bool Interior(Vector3 p) =>
            p.X > target && p.X < size - target && p.Y > target && p.Y < size - target;
    }

    [Fact(Timeout = 120_000)]
    public async Task SolvesConstrainedClosedMeshAndReturnsFields()
    {
        var worker = FindWorker();
        var progress = new List<WorkerProgress>();
        var request = new RemeshRequest
        {
            Mesh = Cube(),
            Settings = new RemeshSettings
            {
                TargetMode = TargetMode.EdgeLength,
                TargetValue = 0.45f,
                OrientationSymmetry = 4,
                PositionSymmetry = 4,
                AlignBoundaries = true,
                SmoothIterations = 0,
                PureOutput = false,
                Deterministic = true,
                ThreadCount = 1,
                ProjectionTolerance = 0.25f
            },
            OrientationConstraints =
            [
                new OrientationConstraint(new Vector3(0, 0, -1), Vector3.UnitX)
            ],
            PositionConstraints =
            [
                new PositionConstraint(new Vector3(1, 0, 0), new Vector3(1, 0, 0))
            ],
            Guides =
            [
                new CurveGuide(
                    [new Vector3(-1, -0.5f, -1), new Vector3(0, -0.5f, -1), new Vector3(1, -0.5f, -1)],
                    GuideMode.Orientation,
                    0.8f)
            ]
        };

        var result = await new WorkerRunner().RunAsync(
            request,
            new WorkerOptions { ExecutablePath = worker, Timeout = TimeSpan.FromMinutes(1) },
            new Progress<WorkerProgress>(progress.Add),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.Vertices);
        Assert.NotEmpty(result.FaceIndices);
        Assert.Contains(result.FaceArity, new[] { 3, 4 });
        Assert.NotEmpty(result.Field);
        Assert.True(result.Diagnostics.Scale > 0);
        Assert.True(result.Diagnostics.AppliedOrientationConstraints >= 1);
        Assert.True(result.Diagnostics.AppliedPositionConstraints >= 1);
    }

    private static string FindWorker()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "CMakePresets.json")))
            directory = directory.Parent;
        if (directory is null)
            throw new FileNotFoundException("Could not locate the repository root.");

#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        var path = Path.Combine(
            directory.FullName,
            "artifacts",
            "native",
            configuration,
            "worker",
            configuration,
            WorkerLocator.ExecutableName);
        return File.Exists(path)
            ? path
            : throw new FileNotFoundException(
                $"Build the {configuration} native worker before running {configuration} integration tests.");
    }

    private static TriangleMesh Cube()
    {
        IReadOnlyList<Vector3> vertices =
        [
            new(-1, -1, -1), new(1, -1, -1), new(1, 1, -1), new(-1, 1, -1),
            new(-1, -1, 1), new(1, -1, 1), new(1, 1, 1), new(-1, 1, 1)
        ];
        IReadOnlyList<uint> triangles =
        [
            0, 2, 1, 0, 3, 2,
            4, 5, 6, 4, 6, 7,
            0, 1, 5, 0, 5, 4,
            1, 2, 6, 1, 6, 5,
            2, 3, 7, 2, 7, 6,
            3, 0, 4, 3, 4, 7
        ];
        return new TriangleMesh(vertices, triangles);
    }
}
