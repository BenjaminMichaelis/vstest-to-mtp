namespace VsTestToMtp.Inventory;

/// <summary>The MSBuild host's answer; written as JSON to its standard output.</summary>
/// <param name="Blockers">Blockers that apply to the whole run, such as MSBuild not being locatable.</param>
/// <param name="Projects">One entry per requested project, evaluated and classified.</param>
public sealed record HostResponse(
    IReadOnlyList<InventoryBlocker> Blockers,
    IReadOnlyList<ProjectInventory> Projects);
