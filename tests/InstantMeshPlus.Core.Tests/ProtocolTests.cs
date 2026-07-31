using System.Numerics;
using System.Text.Json;
using InstantMeshPlus.Core;

namespace InstantMeshPlus.Core.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void WritesVersionedManifestAndBinaryCounts()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"InstantMeshPlus-Test-{Guid.NewGuid():N}");
        try
        {
            var request = new RemeshRequest
            {
                Mesh = new TriangleMesh(
                    [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                    [0, 1, 2]),
                Settings = new RemeshSettings
                {
                    TargetMode = TargetMode.EdgeLength,
                    TargetValue = 0.25f,
                    PositionSymmetry = 6,
                    Deterministic = true,
                    ThreadCount = 1
                },
                OrientationConstraints =
                [
                    new OrientationConstraint(Vector3.Zero, Vector3.UnitX, 0.75f)
                ],
                PositionConstraints =
                [
                    new PositionConstraint(Vector3.UnitX, Vector3.UnitX, 0.5f)
                ],
                Guides =
                [
                    new CurveGuide([Vector3.Zero, Vector3.UnitX], GuideMode.EdgePath)
                ],
                SingularityPaths =
                [
                    new SingularityPath([Vector3.Zero, Vector3.UnitY], FieldKind.Position)
                ]
            };

            var files = WorkerProtocol.WriteRequest(request, directory);

            using var manifest = JsonDocument.Parse(File.ReadAllText(files.ManifestPath));
            Assert.Equal(WorkerProtocol.SchemaVersion, manifest.RootElement.GetProperty("schema").GetInt32());
            Assert.Equal("edgeLength", manifest.RootElement.GetProperty("settings").GetProperty("targetMode").GetString());
            Assert.Equal(3, manifest.RootElement.GetProperty("settings").GetProperty("posy").GetInt32());
            using var stream = File.OpenRead(files.RequestPayloadPath);
            using var reader = new BinaryReader(stream);
            Assert.Equal("IMPREQ1\0"u8.ToArray(), reader.ReadBytes(8));
            Assert.Equal((uint)WorkerProtocol.SchemaVersion, reader.ReadUInt32());
            Assert.Equal(3u, reader.ReadUInt32());
            Assert.Equal(1u, reader.ReadUInt32());
            Assert.Equal(1u, reader.ReadUInt32());
            Assert.Equal(1u, reader.ReadUInt32());
            Assert.Equal(1u, reader.ReadUInt32());
            Assert.Equal(1u, reader.ReadUInt32());
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }
}
