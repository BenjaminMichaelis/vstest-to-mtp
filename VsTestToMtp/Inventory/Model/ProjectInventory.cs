namespace VsTestToMtp.Inventory;

/// <summary>A project in the selection with its evaluated, classified state.</summary>
/// <param name="Path">Project file path (same formatting as <see cref="SourceLocation.File"/>).</param>
/// <param name="TargetFrameworks">Evaluated target frameworks, ordinal order.</param>
/// <param name="Frameworks">Test frameworks detected from package evidence, in enum order.</param>
/// <param name="Runners">
/// Platforms the project's runner packages target (VSTest and/or Microsoft.Testing.Platform), in enum order. A bare framework
/// package says nothing about the runner, so this can be empty even when <paramref name="Frameworks"/> is not.
/// </param>
/// <remarks>
/// Members that are lists are compared by reference by the generated record equality, so do not rely on <c>==</c> or
/// <c>Equals</c> for this type; compare the serialized output or specific fields.
/// </remarks>
public sealed record ProjectInventory(
    string Path,
    string Name,
    ProjectClassification Classification,
    bool HasTestLibraryDependency,
    IReadOnlyList<TestFramework> Frameworks,
    IReadOnlyList<TestRunnerKind> Runners,
    IReadOnlyList<TargetFrameworkState> TargetFrameworks,
    IReadOnlyList<ImportRecord> Imports,
    IReadOnlyList<InventoryBlocker> Blockers);
