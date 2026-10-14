namespace VsTestToMtp.Inventory;

/// <summary>An actionable reason the inventory could not (fully) determine something.</summary>
public sealed record InventoryBlocker(
    BlockerCode Code,
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

    /// <summary>An error blocker: state cannot be determined, or restore/evaluation would fail. Prefer these over the constructor.</summary>
    internal static InventoryBlocker Error(BlockerCode code, string message, string remediation, string? project = null, SourceLocation? location = null) =>
        new(code, BlockerSeverity.Error, message, remediation, project, location);

    /// <summary>A warning blocker: the result may be wrong or environment-dependent.</summary>
    internal static InventoryBlocker Warning(BlockerCode code, string message, string remediation, string? project = null, SourceLocation? location = null) =>
        new(code, BlockerSeverity.Warning, message, remediation, project, location);

    /// <summary>An informational blocker: nothing prevents migration.</summary>
    internal static InventoryBlocker Info(BlockerCode code, string message, string remediation, string? project = null, SourceLocation? location = null) =>
        new(code, BlockerSeverity.Info, message, remediation, project, location);
}
