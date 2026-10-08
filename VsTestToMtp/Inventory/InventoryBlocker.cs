namespace VsTestToMtp.Inventory;

/// <summary>An actionable reason the inventory could not (fully) determine something.</summary>
public sealed record InventoryBlocker(
    string Code,
    BlockerSeverity Severity,
    string Message,
    string Remediation,
    string? Project,
    SourceLocation? Location)
{
    /// <summary>
    /// Other places the blocker is about, in a meaningful order: the candidate solutions of an ambiguous selection, every declaration
    /// of a duplicated PackageVersion, or where a property a condition depends on is defined later. Empty when there are none.
    /// </summary>
    public IReadOnlyList<SourceLocation> RelatedLocations { get; init; } = [];
}
