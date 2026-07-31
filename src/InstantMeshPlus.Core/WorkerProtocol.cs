using System.Numerics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InstantMeshPlus.Core;

public static class WorkerProtocol
{
    public const int SchemaVersion = 1;
    private static readonly byte[] RequestMagic = [.. "IMPREQ1\0"u8];
    private static readonly byte[] ResultMagic = [.. "IMPRES1\0"u8];
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public static WorkerFiles WriteRequest(RemeshRequest request, string directory)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        Directory.CreateDirectory(directory);

        var manifestPath = Path.GetFullPath(Path.Combine(directory, "request.json"));
        var requestPayloadPath = Path.Combine(directory, "request.impbin");
        var resultManifestPath = Path.Combine(directory, "result.json");
        var resultPayloadPath = Path.Combine(directory, "result.impbin");

        using (var stream = File.Create(requestPayloadPath))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(RequestMagic);
            writer.Write((uint)SchemaVersion);
            writer.Write(CheckedCount(request.Mesh.Vertices.Count, "vertices"));
            writer.Write(CheckedCount(request.Mesh.TriangleCount, "triangles"));
            writer.Write(CheckedCount(request.OrientationConstraints.Count, "orientation constraints"));
            writer.Write(CheckedCount(request.PositionConstraints.Count, "position constraints"));
            writer.Write(CheckedCount(request.Guides.Count, "guides"));
            writer.Write(CheckedCount(request.SingularityPaths.Count, "singularity paths"));

            foreach (var vertex in request.Mesh.Vertices)
                WriteVector(writer, vertex);
            foreach (var index in request.Mesh.TriangleIndices)
                writer.Write(index);
            foreach (var constraint in request.OrientationConstraints)
            {
                WriteVector(writer, constraint.Point);
                WriteVector(writer, constraint.Direction);
                writer.Write(constraint.Weight);
            }
            foreach (var constraint in request.PositionConstraints)
            {
                WriteVector(writer, constraint.Point);
                WriteVector(writer, constraint.Anchor);
                writer.Write(constraint.Weight);
            }
            foreach (var guide in request.Guides)
            {
                writer.Write((uint)guide.Mode);
                writer.Write(guide.Weight);
                writer.Write(CheckedCount(guide.Points.Count, "guide points"));
                foreach (var point in guide.Points)
                    WriteVector(writer, point);
            }
            foreach (var path in request.SingularityPaths)
            {
                writer.Write((uint)path.Field);
                writer.Write(CheckedCount(path.Points.Count, "singularity path points"));
                foreach (var point in path.Points)
                    WriteVector(writer, point);
            }
        }

        var settings = request.Settings;
        var manifest = new
        {
            schema = SchemaVersion,
            payload = Path.GetFileName(requestPayloadPath),
            resultManifest = Path.GetFileName(resultManifestPath),
            resultPayload = Path.GetFileName(resultPayloadPath),
            settings = new
            {
                targetMode = TargetModeName(settings.TargetMode),
                settings.TargetValue,
                rosy = settings.OrientationSymmetry,
                posy = settings.PositionSymmetry == 6 ? 3 : 4,
                settings.Extrinsic,
                settings.AlignBoundaries,
                settings.CreaseAngleDegrees,
                settings.SmoothIterations,
                settings.PureOutput,
                settings.Deterministic,
                settings.ThreadCount,
                settings.ProjectionTolerance
            }
        };
        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, JsonOptions));
        return new WorkerFiles(manifestPath, requestPayloadPath, resultManifestPath, resultPayloadPath);
    }

    public static RemeshResult ReadResult(WorkerFiles files)
    {
        ArgumentNullException.ThrowIfNull(files);
        if (!File.Exists(files.ResultManifestPath))
            throw new WorkerException("The worker did not create a result manifest.");

        using var document = JsonDocument.Parse(File.ReadAllText(files.ResultManifestPath));
        var root = document.RootElement;
        var status = root.GetProperty("status").GetString();
        if (!string.Equals(status, "ok", StringComparison.Ordinal))
        {
            var message = root.TryGetProperty("message", out var error)
                ? error.GetString()
                : "The native worker reported an unknown error.";
            throw new WorkerException(message ?? "The native worker reported an unknown error.");
        }

        var payloadName = root.GetProperty("payload").GetString()
            ?? throw new WorkerException("The result manifest does not name a payload.");
        var payloadPath = Path.GetFullPath(Path.Combine(
            Path.GetDirectoryName(files.ResultManifestPath)!,
            payloadName));

        using var stream = File.OpenRead(payloadPath);
        using var reader = new BinaryReader(stream);
        if (!reader.ReadBytes(RequestMagic.Length).SequenceEqual(ResultMagic))
            throw new WorkerException("The result payload has an invalid magic header.");
        if (reader.ReadUInt32() != SchemaVersion)
            throw new WorkerException("The result payload schema is not supported.");

        var vertexCount = ReadCount(reader, "vertices");
        var faceArity = checked((int)reader.ReadUInt32());
        var faceCount = ReadCount(reader, "faces");
        var fieldCount = ReadCount(reader, "field samples");
        var orientationSingularityCount = ReadCount(reader, "orientation singularities");
        var positionSingularityCount = ReadCount(reader, "position singularities");
        if (faceArity is < 3 or > 4)
            throw new WorkerException($"The result face arity {faceArity} is invalid.");

        var vertices = ReadMany(vertexCount, () => ReadVector(reader));
        var indices = ReadMany(checked(faceCount * faceArity), reader.ReadUInt32);
        var field = ReadMany(fieldCount, () => new FieldSample(
            ReadVector(reader),
            ReadVector(reader),
            ReadVector(reader),
            ReadVector(reader)));
        var orientationSingularities = ReadMany(
            orientationSingularityCount,
            () => new OrientationSingularity(
                reader.ReadUInt32(),
                reader.ReadInt32(),
                ReadVector(reader)));
        var positionSingularities = ReadMany(
            positionSingularityCount,
            () => new PositionSingularity(
                reader.ReadUInt32(),
                reader.ReadInt32(),
                reader.ReadInt32(),
                ReadVector(reader)));

        var timings = root.GetProperty("timingsMilliseconds")
            .EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.GetDouble());
        var warnings = root.GetProperty("warnings")
            .EnumerateArray()
            .Select(v => v.GetString() ?? string.Empty)
            .ToArray();
        var diagnostics = new RemeshDiagnostics(
            root.GetProperty("scale").GetSingle(),
            root.GetProperty("appliedOrientationConstraints").GetInt32(),
            root.GetProperty("appliedPositionConstraints").GetInt32(),
            root.GetProperty("rejectedConstraints").GetInt32(),
            warnings,
            timings);
        return new RemeshResult(
            vertices,
            indices,
            faceArity,
            field,
            orientationSingularities,
            positionSingularities,
            diagnostics);
    }

    private static string TargetModeName(TargetMode mode) => mode switch
    {
        TargetMode.FaceCount => "faces",
        TargetMode.VertexCount => "vertices",
        TargetMode.EdgeLength => "edgeLength",
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    private static uint CheckedCount(int count, string label)
    {
        if (count < 0)
            throw new ArgumentOutOfRangeException(label);
        return checked((uint)count);
    }

    private static int ReadCount(BinaryReader reader, string label)
    {
        var value = reader.ReadUInt32();
        if (value > 200_000_000)
            throw new WorkerException($"The result {label} count exceeds the protocol limit.");
        return checked((int)value);
    }

    private static IReadOnlyList<T> ReadMany<T>(int count, Func<T> read)
    {
        var values = new T[count];
        for (var index = 0; index < values.Length; index++)
            values[index] = read();
        return values;
    }

    private static void WriteVector(BinaryWriter writer, Vector3 value)
    {
        writer.Write(value.X);
        writer.Write(value.Y);
        writer.Write(value.Z);
    }

    private static Vector3 ReadVector(BinaryReader reader) =>
        new(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
}

public sealed record WorkerFiles(
    string ManifestPath,
    string RequestPayloadPath,
    string ResultManifestPath,
    string ResultPayloadPath);

public sealed class WorkerException : Exception
{
    public WorkerException(string message) : base(message)
    {
    }

    public WorkerException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
