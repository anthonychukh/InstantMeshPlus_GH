using System.Drawing;
using Grasshopper.Kernel;
using InstantMeshPlus.Core;
using Rhino.Geometry;

namespace InstantMeshPlus.Grasshopper;

public sealed class OrientationConstraintComponent : GH_Component
{
    public OrientationConstraintComponent()
        : base(
            "Orientation Constraint",
            "Orient Field",
            "Constrain the surface orientation field at a projected point.",
            ComponentUtilities.Category,
            "Fields")
    {
    }

    public override Guid ComponentGuid => new("80A23959-0CFA-416A-B171-792AF4BBC64B");
    protected override Bitmap Icon => ComponentIcons.OrientationConstraint;

    protected override void RegisterInputParams(GH_InputParamManager inputs)
    {
        inputs.AddPointParameter("Point", "P", "Point projected to the input mesh.", GH_ParamAccess.item);
        inputs.AddVectorParameter("Direction", "D", "Preferred tangent direction.", GH_ParamAccess.item);
        inputs.AddNumberParameter("Weight", "W", "Constraint weight from 0 to 1.", GH_ParamAccess.item, 1);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager outputs) =>
        outputs.AddGenericParameter("Orientation Constraints", "OC", "Orientation field constraint.", GH_ParamAccess.item);

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var point = Point3d.Unset;
        var direction = Vector3d.Unset;
        var weight = 1.0;
        if (!data.GetData(0, ref point) || !data.GetData(1, ref direction) || !data.GetData(2, ref weight))
            return;
        if (!point.IsValid || !direction.IsValid || direction.IsTiny() || !double.IsFinite(weight) || weight is < 0 or > 1)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Supply a valid point, non-zero direction, and a weight between 0 and 1.");
            return;
        }
        data.SetData(0, new OrientationConstraint(
            ComponentUtilities.ToVector(point),
            ComponentUtilities.ToVector(direction),
            (float)weight));
    }
}

public sealed class PositionConstraintComponent : GH_Component
{
    public PositionConstraintComponent()
        : base(
            "Position Constraint",
            "Position Field",
            "Anchor the position field at a point projected to the mesh.",
            ComponentUtilities.Category,
            "Fields")
    {
    }

    public override Guid ComponentGuid => new("4244AA75-8BA6-4344-8172-A07574FA9F6B");
    protected override Bitmap Icon => ComponentIcons.PositionConstraint;

    protected override void RegisterInputParams(GH_InputParamManager inputs)
    {
        inputs.AddPointParameter("Point", "P", "Point projected to the input mesh.", GH_ParamAccess.item);
        inputs.AddPointParameter("Anchor", "A", "Target position-field anchor.", GH_ParamAccess.item);
        inputs.AddNumberParameter("Weight", "W", "Constraint weight from 0 to 1.", GH_ParamAccess.item, 1);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager outputs) =>
        outputs.AddGenericParameter("Position Constraints", "PC", "Position field constraint.", GH_ParamAccess.item);

    protected override void SolveInstance(IGH_DataAccess data)
    {
        var point = Point3d.Unset;
        var anchor = Point3d.Unset;
        var weight = 1.0;
        if (!data.GetData(0, ref point) || !data.GetData(1, ref anchor) || !data.GetData(2, ref weight))
            return;
        if (!point.IsValid || !anchor.IsValid || !double.IsFinite(weight) || weight is < 0 or > 1)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, "Supply valid points and a weight between 0 and 1.");
            return;
        }
        data.SetData(0, new PositionConstraint(
            ComponentUtilities.ToVector(point),
            ComponentUtilities.ToVector(anchor),
            (float)weight));
    }
}

public sealed class CurveGuideComponent : GH_Component
{
    public CurveGuideComponent()
        : base(
            "Field Curve Guide",
            "Field Guide",
            "Use a curve as an orientation guide or an orientation-plus-edge-path guide.",
            ComponentUtilities.Category,
            "Fields")
    {
    }

    public override Guid ComponentGuid => new("70868901-D73A-49D0-B098-C975D1D6387A");
    protected override Bitmap Icon => ComponentIcons.CurveGuide;

    protected override void RegisterInputParams(GH_InputParamManager inputs)
    {
        inputs.AddCurveParameter("Curve", "C", "Guide curve near the input mesh.", GH_ParamAccess.item);
        inputs.AddIntegerParameter("Mode", "Mode", "0 = orientation only, 1 = orientation and position edge path.", GH_ParamAccess.item, 0);
        inputs.AddNumberParameter("Weight", "W", "Constraint weight from 0 to 1.", GH_ParamAccess.item, 1);
        inputs.AddIntegerParameter("Samples", "N", "Samples used for non-polyline curves.", GH_ParamAccess.item, 32);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager outputs) =>
        outputs.AddGenericParameter("Curve Guides", "CG", "Sampled Field Curve Guide object.", GH_ParamAccess.item);

    protected override void SolveInstance(IGH_DataAccess data)
    {
        Curve? curve = null;
        var mode = 0;
        var weight = 1.0;
        var samples = 32;
        if (!data.GetData(0, ref curve) ||
            !data.GetData(1, ref mode) ||
            !data.GetData(2, ref weight) ||
            !data.GetData(3, ref samples) ||
            curve is null)
            return;
        try
        {
            var guideMode = mode switch
            {
                0 => GuideMode.Orientation,
                1 => GuideMode.EdgePath,
                _ => throw new ArgumentOutOfRangeException(nameof(mode), "Guide mode must be 0 or 1.")
            };
            if (!double.IsFinite(weight) || weight is < 0 or > 1)
                throw new ArgumentOutOfRangeException(nameof(weight), "Weight must be between 0 and 1.");
            data.SetData(0, new CurveGuide(
                ComponentUtilities.SampleCurve(curve, samples),
                guideMode,
                (float)weight));
        }
        catch (Exception error)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error.Message);
        }
    }
}

public sealed class SingularityPathComponent : GH_Component
{
    public SingularityPathComponent()
        : base(
            "Move Singularity",
            "Move Singularity",
            "Move the nearest detected singularity from the curve start along the projected path. Exact singularity creation is not implied.",
            ComponentUtilities.Category,
            "Fields")
    {
    }

    public override Guid ComponentGuid => new("683BA0EF-76F5-45C3-9B13-E24B26DEFEBD");
    protected override Bitmap Icon => ComponentIcons.SingularityPath;

    protected override void RegisterInputParams(GH_InputParamManager inputs)
    {
        inputs.AddCurveParameter("Path", "Path", "Path whose start lies near an existing singularity.", GH_ParamAccess.item);
        inputs.AddIntegerParameter("Field", "Field", "0 = orientation, 1 = position.", GH_ParamAccess.item, 0);
        inputs.AddIntegerParameter("Samples", "N", "Samples used for non-polyline curves.", GH_ParamAccess.item, 32);
    }

    protected override void RegisterOutputParams(GH_OutputParamManager outputs) =>
        outputs.AddGenericParameter("Singularity Path", "SP", "Detected-singularity movement object.", GH_ParamAccess.item);

    protected override void SolveInstance(IGH_DataAccess data)
    {
        Curve? curve = null;
        var field = 0;
        var samples = 32;
        if (!data.GetData(0, ref curve) ||
            !data.GetData(1, ref field) ||
            !data.GetData(2, ref samples) ||
            curve is null)
            return;
        try
        {
            var fieldKind = field switch
            {
                0 => FieldKind.Orientation,
                1 => FieldKind.Position,
                _ => throw new ArgumentOutOfRangeException(nameof(field), "Field must be 0 or 1.")
            };
            data.SetData(0, new SingularityPath(
                ComponentUtilities.SampleCurve(curve, samples),
                fieldKind));
        }
        catch (Exception error)
        {
            AddRuntimeMessage(GH_RuntimeMessageLevel.Error, error.Message);
        }
    }
}
