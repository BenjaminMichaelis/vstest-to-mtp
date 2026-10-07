namespace VsTestToMtp.Inventory;

/// <summary>Raw evaluated state for one target framework, before classification.</summary>
internal sealed record EvaluatedTargetFramework(
    string TargetFramework,
    IReadOnlyList<PropertyState> Properties,
    IReadOnlyList<PackageReferenceState> Packages);
