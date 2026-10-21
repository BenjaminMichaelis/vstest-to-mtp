namespace VsTestToMtp.Inventory;

/// <summary>The complete, deterministic, read-only inventory.</summary>
/// <param name="Root">Absolute inventory root; relative paths in the result are relative to it.</param>
/// <param name="Projects">Selected projects ordered by path.</param>
/// <param name="Blockers">Every blocker (selection-level and per project), ordered.</param>
/// <remarks>
/// Members that are lists are compared by reference by the generated record equality, so do not rely on <c>==</c> or
/// <c>Equals</c> for this type; compare the serialized output or specific fields.
/// </remarks>
public sealed record InventoryResult(
    string Root,
    SelectionInfo Selection,
    IReadOnlyList<ProjectInventory> Projects,
    GlobalJsonInfo? GlobalJson,
    IReadOnlyList<AutomationFile> Automation,
    IReadOnlyList<InventoryBlocker> Blockers)
{
    /// <summary>The projects classified as test applications, in the same order as <see cref="Projects"/>.</summary>
    public IReadOnlyList<ProjectInventory> TestApplications =>
        [.. Projects.Where(p => p.Classification == ProjectClassification.TestApplication)];
}
