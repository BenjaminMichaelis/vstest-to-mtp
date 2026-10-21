namespace VsTestToMtp.Inventory;

/// <summary>What the CLI asks the MSBuild host to evaluate; sent as JSON on the host's standard input.</summary>
/// <param name="SelectionDirectory">Directory MSBuild is located for (honoring its global.json).</param>
/// <param name="RootPath">The inventory root the result's relative paths are anchored to.</param>
/// <param name="ProjectPaths">Full paths of the projects to evaluate.</param>
/// <param name="SdkPackagesDirectory">See <see cref="InventoryOptions.SdkPackagesDirectory"/>.</param>
public sealed record HostRequest(
    string SelectionDirectory,
    string RootPath,
    IReadOnlyList<string> ProjectPaths,
    string? SdkPackagesDirectory);
