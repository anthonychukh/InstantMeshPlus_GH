using Grasshopper.Kernel;

namespace InstantMeshPlus.Grasshopper;

public sealed class InstantMeshPlusInfo : GH_AssemblyInfo
{
    public override string Name => "Instant Mesh Plus";
    public override string Description =>
        "Constraint-aware orientation and position field control for Instant Meshes.";
    public override Guid Id => new("A8053C38-FDFA-4D99-BC31-B285E4BB59C5");
    public override string AuthorName => "anthonychukh";
    public override string AuthorContact => "https://github.com/anthonychukh/InstantMeshPlus_GH";
    public override string Version => typeof(InstantMeshPlusInfo).Assembly.GetName().Version?.ToString() ?? "0.1.2";
}
