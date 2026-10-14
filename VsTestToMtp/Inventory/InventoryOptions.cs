namespace VsTestToMtp.Inventory;

/// <summary>Options for <see cref="InventoryBuilder"/>.</summary>
/// <param name="RootPath">
/// Directory the result's relative paths and the CI/script scan are anchored to.
/// Defaults to the nearest ancestor of the selection containing <c>.git</c>, else the selection's directory.
/// </param>
/// <param name="SdkPackagesDirectory">
/// Where MSBuild's NuGet SDK resolver may extract a missing versioned SDK (for example <c>MSTest.Sdk/x.y</c>) while evaluating,
/// instead of any packages folder a <c>NuGet.config</c> in the repository would choose. Defaults to NuGet's per-user packages folder.
/// Ignored when the <c>NUGET_PACKAGES</c> environment variable is already set.
/// </param>
public sealed record InventoryOptions(string? RootPath = null, string? SdkPackagesDirectory = null);
