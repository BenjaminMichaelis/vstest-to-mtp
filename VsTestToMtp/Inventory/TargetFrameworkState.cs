namespace VsTestToMtp.Inventory;

/// <summary>The classification for one target framework with the reasons behind it.</summary>
public sealed record TargetFrameworkState(
    string TargetFramework,
    ProjectClassification Classification,
    bool HasTestLibraryDependency,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<PropertyState> Properties,
    IReadOnlyList<PackageReferenceState> Packages)
{
    public PropertyState? GetProperty(string name) =>
        Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
