namespace VsTestToMtp.Inventory;

/// <summary>The one place blockers are de-duplicated and put in their deterministic order.</summary>
internal static class BlockerOrdering
{
    /// <summary>Removes duplicates and orders by project, code, location and message, so output never depends on evaluation order.</summary>
    public static List<InventoryBlocker> Normalize(IEnumerable<InventoryBlocker> blockers) =>
        [.. blockers
            .DistinctBy(b => (b.Code, b.Project, b.Location, b.Message))
            .OrderBy(b => b.Project, StringComparer.Ordinal)
            .ThenBy(b => b.Code, StringComparer.Ordinal)
            .ThenBy(b => b.Location?.File, StringComparer.Ordinal)
            .ThenBy(b => b.Location?.Line)
            .ThenBy(b => b.Message, StringComparer.Ordinal)];
}
