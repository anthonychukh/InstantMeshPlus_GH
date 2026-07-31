using System.Drawing;
using System.Text.Json;
using System.Windows.Forms;
using Grasshopper.Kernel;
using GrasshopperAsyncComponent;
using InstantMeshPlus.Core;
using Rhino.Geometry;

namespace InstantMeshPlus.Grasshopper;

public sealed class InstantMeshComponent : GH_AsyncComponent<InstantMeshComponent>
{
    private string? _progressPhase;

    public InstantMeshComponent()
        : base(
            "Instant Mesh Plus",
            "Instant Mesh+",
            "Run constraint-aware Instant Meshes in a responsive, cancellable worker.",
            ComponentUtilities.Category,
            "Solve")
    {
        BaseWorker = new InstantMeshWorker(this);
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

    protected override void AppendAdditionalComponentMenuItems(ToolStripDropDown menu)
    {
        base.AppendAdditionalComponentMenuItems(menu);
        Menu_AppendSeparator(menu);
        Menu_AppendItem(
            menu,
            "Cancel remeshing",
            (_, _) => RequestCancellation(),
            WorkerCount > 0);
    }

    protected override string FormatProgress(double progress)
    {
        var phase = Volatile.Read(ref _progressPhase);
        return string.IsNullOrWhiteSpace(phase)
            ? progress.ToString("0%")
            : $"{progress:0%} - {phase}";
    }

    protected override void OnWorkersCompleted()
    {
        // SetData publishes a more useful terminal state.
    }

    private void UpdateProgressPhase(string phase) =>
        Volatile.Write(ref _progressPhase, phase);

    private void ResetProgress() =>
        Volatile.Write(ref _progressPhase, null);

    private bool TryCaptureInputs(IGH_DataAccess data, out SolveInput input)
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
            var settings =
                ComponentUtilities.Unwrap<RemeshSettings>(settingsValue) ??
                new RemeshSettings();
            settings.Validate();
            input = new SolveInput(
                mesh.DuplicateMesh(),
                settings,
                ComponentUtilities.UnwrapMany<OrientationConstraint>(orientationValues),
                ComponentUtilities.UnwrapMany<PositionConstraint>(positionValues),
                ComponentUtilities.UnwrapMany<CurveGuide>(guideValues),
                ComponentUtilities.UnwrapMany<SingularityPath>(singularityValues),
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

    private static RemeshRequest BuildRequest(SolveInput input)
    {
        var request = new RemeshRequest
        {
            Mesh = ComponentUtilities.ToCoreMesh(input.Mesh),
            Settings = input.Settings,
            OrientationConstraints = input.OrientationConstraints,
            PositionConstraints = input.PositionConstraints,
            Guides = input.Guides,
            SingularityPaths = input.SingularityPaths
        };
        request.Validate();
        return request;
    }

    private static SolvePresentation BuildPresentation(
        RemeshResult result,
        int previewStride)
    {
        var displayStride = Math.Max(1, previewStride);
        var displayScale = Math.Max(result.Diagnostics.Scale * 0.35f, 1e-5f);
        var orientations = result.Field
            .Where((_, index) => index % displayStride == 0)
            .Select(sample =>
            {
                var start = ComponentUtilities.ToPoint(sample.Point);
                var end = ComponentUtilities.ToPoint(
                    sample.Point + sample.Orientation * displayScale);
                return new Line(start, end);
            })
            .ToArray();
        var positions = result.Field
            .Where((_, index) => index % displayStride == 0)
            .Select(sample => ComponentUtilities.ToPoint(sample.Position))
            .ToArray();

        return new SolvePresentation(
            ComponentUtilities.ToRhinoMesh(result),
            orientations,
            positions,
            result.OrientationSingularities
                .Select(singularity => ComponentUtilities.ToPoint(singularity.Point))
                .ToArray(),
            result.PositionSingularities
                .Select(singularity => ComponentUtilities.ToPoint(singularity.Point))
                .ToArray(),
            FormatDiagnostics(result.Diagnostics));
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

    private static double MapProgress(WorkerProgress progress) =>
        progress.Phase switch
        {
            "preprocess" => 0.20 * progress.Progress,
            "orientation" => 0.20 + 0.35 * progress.Progress,
            "position" => 0.55 + 0.35 * progress.Progress,
            "extract" => 0.90 + 0.10 * progress.Progress,
            _ => 0
        };

    private sealed record SolveInput(
        Mesh Mesh,
        RemeshSettings Settings,
        IReadOnlyList<OrientationConstraint> OrientationConstraints,
        IReadOnlyList<PositionConstraint> PositionConstraints,
        IReadOnlyList<CurveGuide> Guides,
        IReadOnlyList<SingularityPath> SingularityPaths,
        bool Run,
        int PreviewStride,
        string? WorkerPath);

    private sealed record SolvePresentation(
        Mesh Mesh,
        IReadOnlyList<Line> Orientations,
        IReadOnlyList<Point3d> Positions,
        IReadOnlyList<Point3d> OrientationSingularities,
        IReadOnlyList<Point3d> PositionSingularities,
        string Diagnostics);

    private sealed class InstantMeshWorker : WorkerInstance<InstantMeshComponent>
    {
        private SolveInput? _input;
        private RemeshResult? _result;
        private SolvePresentation? _presentation;
        private Exception? _failure;
        private bool _hasInputs;

        public InstantMeshWorker(
            InstantMeshComponent parent,
            string id = "baseworker",
            CancellationToken cancellationToken = default)
            : base(parent, id, cancellationToken)
        {
        }

        public override WorkerInstance<InstantMeshComponent> Duplicate(
            string id,
            CancellationToken cancellationToken) =>
            new InstantMeshWorker(Parent, id, cancellationToken);

        public override void GetData(
            IGH_DataAccess access,
            GH_ComponentParamServer parameters)
        {
            _hasInputs = Parent.TryCaptureInputs(access, out _input);
            if (_input?.Run == true)
                Parent.ResetProgress();
        }

        public override async Task DoWork(
            Action<string, double> reportProgress,
            Action done)
        {
            if (!_hasInputs || _input is null || !_input.Run)
            {
                done();
                return;
            }

            try
            {
                reportProgress(Id, 0);
                var progress = new InlineProgress<WorkerProgress>(value =>
                {
                    Parent.UpdateProgressPhase(value.Phase);
                    reportProgress(Id, MapProgress(value));
                });
                var request = BuildRequest(_input);
                _result = await new WorkerRunner().RunAsync(
                    request,
                    new WorkerOptions { ExecutablePath = _input.WorkerPath },
                    progress,
                    CancellationToken).ConfigureAwait(false);
                CancellationToken.ThrowIfCancellationRequested();
                _presentation = BuildPresentation(_result, _input.PreviewStride);
                reportProgress(Id, 1);
                done();
            }
            catch (OperationCanceledException) when (CancellationToken.IsCancellationRequested)
            {
                // The parent already reset this worker; cancelled work must not
                // expire the solution or publish stale results.
            }
            catch (Exception error)
            {
                _failure = error;
                done();
            }
        }

        public override void SetData(IGH_DataAccess access)
        {
            if (!_hasInputs || _input is null)
            {
                Parent.Message = null;
                return;
            }

            if (!_input.Run)
            {
                Parent.Message = "Paused";
                return;
            }

            if (_failure is not null)
            {
                Parent.Message = "Failed";
                Parent.AddRuntimeMessage(
                    _failure is OperationCanceledException
                        ? GH_RuntimeMessageLevel.Warning
                        : GH_RuntimeMessageLevel.Error,
                    _failure.Message);
                return;
            }

            if (_result is null || _presentation is null)
                return;

            access.SetData(0, _presentation.Mesh);
            access.SetData(1, _result);
            access.SetDataList(2, _presentation.Orientations);
            access.SetDataList(3, _presentation.Positions);
            access.SetDataList(4, _presentation.OrientationSingularities);
            access.SetDataList(5, _presentation.PositionSingularities);
            access.SetData(6, _presentation.Diagnostics);
            foreach (var warning in _result.Diagnostics.Warnings)
                Parent.AddRuntimeMessage(GH_RuntimeMessageLevel.Warning, warning);
            Parent.Message = $"{_result.FaceCount:N0} faces";
        }

        private sealed class InlineProgress<T>(Action<T> report) : IProgress<T>
        {
            public void Report(T value) => report(value);
        }
    }
}
