using System.Drawing;
using System.Text.Json;
using Grasshopper.Kernel;
using InstantMeshPlus.Core;
using Rhino.Geometry;

namespace InstantMeshPlus.Grasshopper;

public sealed class InstantMeshComponent : GH_TaskCapableComponent<InstantMeshComponent.SolveOutcome>
{
    public InstantMeshComponent()
        : base(
            "Instant Mesh Plus",
            "Instant Mesh+",
            "Run constraint-aware Instant Meshes in a cancellable out-of-process worker.",
            ComponentUtilities.Category,
            "Solve")
    {
    }

    public override Guid ComponentGuid => new("37D97326-6D27-4F06-A7DE-71D16948C4DE");
    protected override Bitmap Icon => null!;
    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override void RegisterInputParams(GH_InputParamManager inputs)
    {
        inputs.AddMeshParameter("Mesh", "M", "Triangle or quad mesh to remesh.", GH_ParamAccess.item);
        inputs.AddGenericParameter("Settings", "Settings", "Optional Instant Mesh Settings object.", GH_ParamAccess.item);
        inputs.AddGenericParameter("Orientation Constraints", "OC", "Orientation Constraint objects.", GH_ParamAccess.list);
        inputs.AddGenericParameter("Position Constraints", "PC", "Position Constraint objects.", GH_ParamAccess.list);
        inputs.AddGenericParameter("Curve Guides", "G", "Field Curve Guide objects.", GH_ParamAccess.list);
        inputs.AddGenericParameter("Singularity Paths", "S", "Move Singularity path objects.", GH_ParamAccess.list);
        inputs.AddBooleanParameter("Run", "Run", "Run the worker. Set false while editing heavy definitions.", GH_ParamAccess.item, true);
        inputs.AddIntegerParameter("Preview Stride", "Stride", "Display every nth field sample; 1 displays all.", GH_ParamAccess.item, 20);
        inputs.AddTextParameter("Worker", "Worker", "Optional full path to InstantMeshPlus.Worker.exe.", GH_ParamAccess.item, string.Empty);
        for (var index = 1; index <= 5; index++)
            Params.Input[index].Optional = true;
        Params.Input[8].Optional = true;
    }

    protected override void RegisterOutputParams(GH_OutputParamManager outputs)
    {
        outputs.AddMeshParameter("Mesh", "M", "Remeshed output.", GH_ParamAccess.item);
        outputs.AddGenericParameter("Result", "R", "Full field, topology, singularity, and diagnostic result.", GH_ParamAccess.item);
        outputs.AddLineParameter("Orientation Field", "Q", "Sampled orientation field preview lines.", GH_ParamAccess.list);
        outputs.AddPointParameter("Position Field", "O", "Sampled position field points.", GH_ParamAccess.list);
        outputs.AddPointParameter("Orientation Singularities", "OS", "Detected orientation singularities.", GH_ParamAccess.list);
        outputs.AddPointParameter("Position Singularities", "PS", "Detected position singularities.", GH_ParamAccess.list);
        outputs.AddTextParameter("Diagnostics", "D", "Constraint counts, warnings, and phase timings.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        if (!TryReadInputs(data, out var input))
            return;
        if (!input.Run)
        {
            Message = "Paused";
            return;
        }

        if (InPreSolve)
        {
            TaskList.Add(Task.Run(
                () => ComputeAsync(input, CancelToken),
                CancelToken));
            return;
        }

        if (!GetSolveResults(data, out var outcome))
            outcome = ComputeAsync(input, CancellationToken.None).GetAwaiter().GetResult();

        if (outcome.Error is not null)
        {
            Message = "Error";
            AddRuntimeMessage(
                outcome.Error is OperationCanceledException
                    ? GH_RuntimeMessageLevel.Warning
                    : GH_RuntimeMessageLevel.Error,
                outcome.Error.Message);
            return;
        }

        var result = outcome.Result!;
        var displayStride = Math.Max(1, input.PreviewStride);
        var displayScale = Math.Max(result.Diagnostics.Scale * 0.35f, 1e-5f);
        var orientations = result.Field
            .Where((_, index) => index % displayStride == 0)
            .Select(sample =>
            {
                var start = ComponentUtilities.ToPoint(sample.Point);
                var end = ComponentUtilities.ToPoint(sample.Point + sample.Orientation * displayScale);
                return new Line(start, end);
            })
            .ToArray();
        var positions = result.Field
            .Where((_, index) => index % displayStride == 0)
            .Select(sample => ComponentUtilities.ToPoint(sample.Position))
            .ToArray();

        data.SetData(0, ComponentUtilities.ToRhinoMesh(result));
        data.SetData(1, result);
        data.SetDataList(2, orientations);
        data.SetDataList(3, positions);
        data.SetDataList(4, result.OrientationSingularities.Select(s => ComponentUtilities.ToPoint(s.Point)));
        data.SetDataList(5, result.PositionSingularities.Select(s => ComponentUtilities.ToPoint(s.Point)));
        data.SetData(6, FormatDiagnostics(result.Diagnostics));
        foreach (var warning in result.Diagnostics.Warnings)
            AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
        Message = $"{result.FaceCount:N0} faces";
    }

    private bool TryReadInputs(IGH_DataAccess data, out SolveInput input)
    {
        input = default!;
        Mesh? mesh = null;
        object? settingsValue = null;
        var orientationValues = new List<object>();
        var positionValues = new List<object>();
        var guideValues = new List<object>();
        var singularityValues = new List<object>();
        var run = true;
        var previewStride = 20;
        var worker = string.Empty;
        if (!data.GetData(0, ref mesh) || mesh is null)
            return false;
        data.GetData(1, ref settingsValue);
        data.GetDataList(2, orientationValues);
        data.GetDataList(3, positionValues);
        data.GetDataList(4, guideValues);
        data.GetDataList(5, singularityValues);
        data.GetData(6, ref run);
        data.GetData(7, ref previewStride);
        data.GetData(8, ref worker);
        try
        {
            var request = new RemeshRequest
            {
                Mesh = ComponentUtilities.ToCoreMesh(mesh),
                Settings = ComponentUtilities.Unwrap<RemeshSettings>(settingsValue) ?? new RemeshSettings(),
                OrientationConstraints = ComponentUtilities.UnwrapMany<OrientationConstraint>(orientationValues),
                PositionConstraints = ComponentUtilities.UnwrapMany<PositionConstraint>(positionValues),
                Guides = ComponentUtilities.UnwrapMany<CurveGuide>(guideValues),
                SingularityPaths = ComponentUtilities.UnwrapMany<SingularityPath>(singularityValues)
            };
            request.Validate();
            input = new SolveInput(
                request,
                run,
                Math.Max(1, previewStride),
                string.IsNullOrWhiteSpace(worker) ? null : worker);
            return true;
        }
        catch (Exception error)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error.Message);
            return false;
        }
    }

    private static async Task<SolveOutcome> ComputeAsync(
        SolveInput input,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await new WorkerRunner().RunAsync(
                input.Request,
                new WorkerOptions { ExecutablePath = input.WorkerPath },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            return new SolveOutcome(result, null);
        }
        catch (Exception error)
        {
            return new SolveOutcome(null, error);
        }
    }

    private static string FormatDiagnostics(RemeshDiagnostics diagnostics) =>
        JsonSerializer.Serialize(new
        {
            diagnostics.Scale,
            diagnostics.AppliedOrientationConstraints,
            diagnostics.AppliedPositionConstraints,
            diagnostics.RejectedConstraints,
            diagnostics.Warnings,
            diagnostics.TimingsMilliseconds
        }, new JsonSerializerOptions { WriteIndented = true });

    private sealed record SolveInput(
        RemeshRequest Request,
        bool Run,
        int PreviewStride,
        string? WorkerPath);

    public sealed record SolveOutcome(RemeshResult? Result, Exception? Error);
}
