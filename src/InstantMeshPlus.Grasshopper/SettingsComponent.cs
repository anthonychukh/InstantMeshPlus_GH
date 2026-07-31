using System.Drawing;
using Grasshopper.Kernel;
using InstantMeshPlus.Core;

namespace InstantMeshPlus.Grasshopper;

public sealed class SettingsComponent : GH_Component
{
    public SettingsComponent()
        : base(
            "Instant Mesh Settings",
            "IM Settings",
            "Configure target density, field symmetry, alignment, extraction, and reproducibility.",
            ComponentUtilities.Category,
            "Solve")
    {
    }

    public override Guid ComponentGuid => new("8A3BB3D2-5140-40CD-9DD0-D5468DFDB94D");
    protected override Bitmap Icon => null!;
    public override GH_Exposure Exposure => GH_Exposure.primary;

    protected override void RegisterInputParams(GH_InputParamManager inputs)
    {
        inputs.AddIntegerParameter("Target Mode", "Mode", "0 = face count, 1 = vertex count, 2 = edge length.", GH_ParamAccess.item, 0);
        inputs.AddNumberParameter("Target", "Target", "Face count, vertex count, or model-unit edge length.", GH_ParamAccess.item, 1000);
        inputs.AddIntegerParameter("Orientation Symmetry", "RoSy", "2, 4, or 6-fold orientation symmetry.", GH_ParamAccess.item, 4);
        inputs.AddIntegerParameter("Position Symmetry", "PoSy", "4 for quad layout or 6 for triangular layout.", GH_ParamAccess.item, 4);
        inputs.AddBooleanParameter("Extrinsic", "Extrinsic", "Use extrinsic instead of intrinsic field optimization.", GH_ParamAccess.item, true);
        inputs.AddBooleanParameter("Align Boundaries", "Boundary", "Align orientation and position fields to open boundaries.", GH_ParamAccess.item, true);
        inputs.AddNumberParameter("Crease Angle", "Crease", "Feature angle in degrees; -1 disables crease detection.", GH_ParamAccess.item, 90);
        inputs.AddIntegerParameter("Smooth Iterations", "Smooth", "Extraction smoothing iterations.", GH_ParamAccess.item, 2);
        inputs.AddBooleanParameter("Pure Output", "Pure", "Prefer pure quads or pure triangles over dominant mixed output.", GH_ParamAccess.item, true);
        inputs.AddBooleanParameter("Deterministic", "Deterministic", "Use deterministic preprocessing and extraction.", GH_ParamAccess.item, false);
        inputs.AddIntegerParameter("Threads", "Threads", "-1 uses the available processors; positive values set a worker limit.", GH_ParamAccess.item, -1);
        inputs.AddNumberParameter("Projection Tolerance", "Tolerance", "Maximum constraint projection distance; 0 derives it from target scale.", GH_ParamAccess.item, 0);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager outputs)
    {
        outputs.AddGenericParameter("Settings", "Settings", "Instant Mesh+ solver settings.", GH_ParamAccess.item);
    }

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var mode = 0;
        var target = 1000.0;
        var rosy = 4;
        var posy = 4;
        var extrinsic = true;
        var boundary = true;
        var crease = 90.0;
        var smooth = 2;
        var pure = true;
        var deterministic = false;
        var threads = -1;
        var tolerance = 0.0;
        if (!data.GetData(0, ref mode) ||
            !data.GetData(1, ref target) ||
            !data.GetData(2, ref rosy) ||
            !data.GetData(3, ref posy) ||
            !data.GetData(4, ref extrinsic) ||
            !data.GetData(5, ref boundary) ||
            !data.GetData(6, ref crease) ||
            !data.GetData(7, ref smooth) ||
            !data.GetData(8, ref pure) ||
            !data.GetData(9, ref deterministic) ||
            !data.GetData(10, ref threads) ||
            !data.GetData(11, ref tolerance))
            return;

        try
        {
            var settings = new RemeshSettings
            {
                TargetMode = mode switch
                {
                    0 => TargetMode.FaceCount,
                    1 => TargetMode.VertexCount,
                    2 => TargetMode.EdgeLength,
                    _ => throw new ArgumentOutOfRangeException(nameof(mode), "Target mode must be 0, 1, or 2.")
                },
                TargetValue = checked((float)target),
                OrientationSymmetry = rosy,
                PositionSymmetry = posy,
                Extrinsic = extrinsic,
                AlignBoundaries = boundary,
                CreaseAngleDegrees = checked((float)crease),
                SmoothIterations = smooth,
                PureOutput = pure,
                Deterministic = deterministic,
                ThreadCount = threads,
                ProjectionTolerance = checked((float)tolerance)
            };
            settings.Validate();
            data.SetData(0, settings);
        }
        catch (Exception error)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error.Message);
        }
    }
}
