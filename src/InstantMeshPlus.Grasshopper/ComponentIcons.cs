using System.Drawing;
using System.Resources;

namespace InstantMeshPlus.Grasshopper;

internal static class ComponentIcons
{
    public static Bitmap InstantMesh { get; } = Load(nameof(InstantMesh));
    public static Bitmap Settings { get; } = Load(nameof(Settings));
    public static Bitmap OrientationConstraint { get; } = Load(nameof(OrientationConstraint));
    public static Bitmap PositionConstraint { get; } = Load(nameof(PositionConstraint));
    public static Bitmap CurveGuide { get; } = Load(nameof(CurveGuide));
    public static Bitmap SingularityPath { get; } = Load(nameof(SingularityPath));

    private static Bitmap Load(string name)
    {
        var assembly = typeof(ComponentIcons).Assembly;
        var resourceName = $"{typeof(ComponentIcons).Namespace}.Resources.Icons.{name}.png";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new MissingManifestResourceException(
                $"Embedded component icon '{resourceName}' was not found.");
        using var source = new Bitmap(stream);
        return new Bitmap(source);
    }
}
