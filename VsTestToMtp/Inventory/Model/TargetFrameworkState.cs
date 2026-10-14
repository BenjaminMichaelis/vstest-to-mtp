namespace VsTestToMtp.Inventory;

/// <summary>The classification for one target framework with the reasons behind it.</summary>
/// <remarks>
/// Members that are lists are compared by reference by the generated record equality, so do not rely on <c>==</c> or
/// <c>Equals</c> for this type; compare the serialized output or specific fields.
/// </remarks>
public sealed record TargetFrameworkState(
    string TargetFramework,
    ProjectClassification Classification,
    bool HasTestLibraryDependency,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<PropertyState> Properties,
    IReadOnlyList<PackageReferenceState> Packages,
    IReadOnlyList<ProjectReferenceState> ProjectReferences)
{
    public PropertyState? GetProperty(string name) => PropertyState.Find(Properties, name);
}
