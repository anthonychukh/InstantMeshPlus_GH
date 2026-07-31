using System.Numerics;
using InstantMeshPlus.Core;

namespace InstantMeshPlus.Core.Tests;

public sealed class WorkerIntegrationTests
{
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
