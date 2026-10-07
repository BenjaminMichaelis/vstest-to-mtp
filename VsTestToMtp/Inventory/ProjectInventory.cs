namespace VsTestToMtp.Inventory;

/// <summary>A project in the selection with its evaluated, classified state.</summary>
/// <param name="Path">Project file path (same formatting as <see cref="SourceLocation.File"/>).</param>
/// <param name="TargetFrameworks">Evaluated target frameworks, ordinal order.</param>
/// <param name="Frameworks">Test frameworks detected from package evidence (e.g. <c>MSTest</c>, <c>xUnit v3</c>).</param>
public sealed record ProjectInventory(
    string Path,
    string Name,
    ProjectClassification Classification,
    bool HasTestLibraryDependency,
    IReadOnlyList<string> Frameworks,
    IReadOnlyList<TargetFrameworkState> TargetFrameworks,
    IReadOnlyList<ProjectReferenceState> ProjectReferences,
    IReadOnlyList<ImportRecord> Imports,
    IReadOnlyList<InventoryBlocker> Blockers);
