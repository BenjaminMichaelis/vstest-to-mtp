namespace VsTestToMtp.Inventory;

/// <summary>Options for <see cref="InventoryBuilder"/>.</summary>
/// <param name="RootPath">
/// Directory the result's relative paths and the CI/script scan are anchored to.
/// Defaults to the nearest ancestor of the selection containing <c>.git</c>, else the selection's directory.
/// </param>
public sealed record InventoryOptions(string? RootPath = null);
