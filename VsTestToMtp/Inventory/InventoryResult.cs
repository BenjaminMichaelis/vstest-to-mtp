namespace VsTestToMtp.Inventory;

/// <summary>The complete, deterministic, read-only inventory.</summary>
/// <param name="Root">Absolute inventory root; relative paths in the result are relative to it.</param>
/// <param name="Projects">Selected projects ordered by path.</param>
/// <param name="Blockers">Every blocker (selection-level and per project), ordered.</param>
public sealed record InventoryResult(
    string Root,
    SelectionInfo Selection,
    IReadOnlyList<ProjectInventory> Projects,
    GlobalJsonInfo? GlobalJson,
    IReadOnlyList<AutomationFile> Automation,
    IReadOnlyList<InventoryBlocker> Blockers)
{
    public IEnumerable<ProjectInventory> TestApplications =>
        Projects.Where(p => p.Classification == ProjectClassification.TestApplication);
}
