namespace VsTestToMtp.Inventory;

/// <summary>An actionable reason the inventory could not (fully) determine something.</summary>
public sealed record InventoryBlocker(
    string Code,
    BlockerSeverity Severity,
    string Message,
    string Remediation,
    string? Project,
    SourceLocation? Location);
