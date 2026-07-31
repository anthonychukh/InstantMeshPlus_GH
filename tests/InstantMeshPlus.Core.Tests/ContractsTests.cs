using System.Numerics;
using InstantMeshPlus.Core;

namespace InstantMeshPlus.Core.Tests;

public sealed class ContractsTests
{
    [Fact]
    public void RejectsInvalidSymmetry()
    {
        var settings = new RemeshSettings { OrientationSymmetry = 5 };

        var error = Assert.Throws<ArgumentOutOfRangeException>(settings.Validate);

        Assert.Contains("2, 4, or 6", error.Message);
    }

    [Fact]
    public void RejectsDegenerateTriangles()
    {
        var mesh = new TriangleMesh(
            [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
            [0, 1, 1]);

        var error = Assert.Throws<ArgumentException>(mesh.Validate);

        Assert.Contains("repeated", error.Message);
    }

    [Fact]
    public void RejectsShortGuide()
    {
        var request = new RemeshRequest
        {
            Mesh = Triangle(),
            Guides = [new CurveGuide([Vector3.Zero])]
        };

        var error = Assert.Throws<ArgumentException>(request.Validate);

        Assert.Contains("two sample points", error.Message);
    }

    private static TriangleMesh Triangle() => new(
        [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
        [0, 1, 2]);
}
