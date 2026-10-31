namespace VsTestToMtp.Tests.Inventory;

using VsTestToMtp.Inventory;

/// <summary>
/// Lets a <see cref="ScenarioWorkspace"/> resolve versioned SDKs (<c>Sdk="Id/1.2.3"</c>) from a local feed into a disposable packages
/// folder. The machine's own <c>NUGET_PACKAGES</c> (set on many developer machines) is cleared for the lifetime of the feed so it cannot
/// leak in, which changes process-wide state: tests that use this must be <c>[NotInParallel]</c> without a key.
/// </summary>
public sealed class LocalSdkFeed : IDisposable
{
    /// <summary>A NuGet.config with only the local feed, whose packages folder points inside the repository (which evaluation must not use).</summary>
    public const string NuGetConfig = """
        <configuration>
          <config>
            <add key="globalPackagesFolder" value="packages" />
          </config>
          <packageSources>
            <clear />
            <add key="local" value="feed" />
          </packageSources>
        </configuration>
        """;

    private readonly ScenarioWorkspace _workspace;
    private readonly EnvironmentVariableScope _withoutNuGetPackages = new("NUGET_PACKAGES", null);

    public LocalSdkFeed(ScenarioWorkspace workspace)
    {
        _workspace = workspace;
        workspace.Write("NuGet.config", NuGetConfig);
    }

    /// <summary>Where versioned SDKs are extracted; outside the workspace, deleted on dispose.</summary>
    public string PackagesDirectory { get; } = Path.Combine(Path.GetTempPath(), "vstest-to-mtp-sdk-cache", Guid.NewGuid().ToString("N"));

    public LocalSdkFeed AddSdk(string id, string version, string props, string targets = "<Project />")
    {
        ScenarioWorkspace.WriteSdkPackage(_workspace.PathOf("feed"), id, version, props, targets);
        return this;
    }

    public InventoryResult Inventory(string selection) =>
        InventoryBuilder.Build(_workspace.PathOf(selection), new InventoryOptions(_workspace.RootPath, SdkPackagesDirectory: PackagesDirectory));

    public void Dispose()
    {
        if (Directory.Exists(PackagesDirectory))
        {
            Directory.Delete(PackagesDirectory, recursive: true);
        }

        _withoutNuGetPackages.Dispose();
    }
}
