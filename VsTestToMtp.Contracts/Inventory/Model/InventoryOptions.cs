namespace VsTestToMtp.Inventory;

/// <summary>Options for <see cref="InventoryBuilder"/>.</summary>
/// <param name="RootPath">
/// Directory the result's relative paths and the CI/script scan are anchored to.
/// Defaults to the nearest ancestor of the selection containing <c>.git</c>, else the selection's directory.
/// </param>
/// <param name="SdkPackagesDirectory">
/// Where MSBuild's NuGet SDK resolver may extract a missing versioned SDK (for example <c>MSTest.Sdk/x.y</c>) while evaluating,
/// instead of any packages folder a <c>NuGet.config</c> in the repository would choose. When not given, NuGet's own default (its per-user
/// packages folder) applies, and is only forced when a <c>NuGet.config</c> that applies to a project sets <c>globalPackagesFolder</c>.
/// Ignored when the <c>NUGET_PACKAGES</c> environment variable is already set. Neither value is checked against the repository: a
/// location the caller chooses, inside the repository or not, is used as given.
/// </param>
/// <param name="MsBuildHostDirectory">
/// Directory holding the <c>msbuild-host</c> (and, when shipped, <c>msbuild-host-bundled</c>) folders with the process that evaluates
/// projects with the .NET SDK's MSBuild. Defaults to the directory of the running tool, which is where the package installs them; set it
/// when the library is hosted somewhere that lays the host out elsewhere.
/// </param>
/// <param name="HostRuntime">
/// Which .NET runtime the MSBuild host runs on: <see cref="MsBuildHostRuntime.Auto"/> (the default) prefers the machine's runtime unless the
/// bundled one is newer; the other values pin one runtime and never fall back to the other.
/// </param>
public sealed record InventoryOptions(
    string? RootPath = null,
    string? SdkPackagesDirectory = null,
    string? MsBuildHostDirectory = null,
    MsBuildHostRuntime HostRuntime = MsBuildHostRuntime.Auto);
