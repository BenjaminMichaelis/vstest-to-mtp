namespace VsTestToMtp.Tests.Inventory;

using System.Text.Json;
using System.Text.Json.Nodes;

using VsTestToMtp.Inventory;

/// <summary>
/// The launcher's choice between the machine-runtime host and the bundled host. The bundled host is simulated with the
/// framework-dependent apphost (it finds the machine runtime itself), which is enough to tell which copy was started.
/// </summary>
// Each case starts MSBuild host processes; running them in parallel only adds contention.
[NotInParallel("MSBuildEvaluation")]
public class MsBuildHostLauncherTests
{
    private static string BuiltHostDirectory => Path.Combine(AppContext.BaseDirectory, "msbuild-host");

    [Test]
    public async Task MissingDotNet_FallsBackToTheBundledHost()
    {
        using HostLayout layout = HostLayout.Create(frameworkHost: HostBehavior.Working, bundledHost: HostBehavior.Working);
        using EnvironmentVariableScope noDotNet = new("DOTNET_HOST_PATH", Path.Combine(layout.Root, "no-such-dotnet"));

        InventoryResult result = layout.Inventory();

        await Assert.That(result.Blockers).IsEmpty();
        await Assert.That(result.Projects.Single().Classification).IsEqualTo(ProjectClassification.Production);
    }

    [Test]
    public async Task MissingFramework_FallsBackToTheBundledHost()
    {
        // hostfxr exits with FrameworkMissingFailure when no installed runtime satisfies the host's runtimeconfig.
        using HostLayout layout = HostLayout.Create(frameworkHost: HostBehavior.NeedsAnUninstalledRuntime, bundledHost: HostBehavior.Working);

        InventoryResult result = layout.Inventory();

        await Assert.That(result.Blockers).IsEmpty();
        await Assert.That(result.Projects.Single().Classification).IsEqualTo(ProjectClassification.Production);
    }

    [Test]
    public async Task UnrelatedHostFailure_DoesNotFallBackToTheBundledHost()
    {
        // The framework-dependent host starts but crashes; running the bundled host would hide that and could mask a real defect.
        using HostLayout layout = HostLayout.Create(frameworkHost: HostBehavior.CrashesOnStart, bundledHost: HostBehavior.Working);

        InventoryResult result = layout.Inventory();

        await Assert.That(result.Blockers.Select(b => b.Code)).Contains(BlockerCode.MsBuildHostFailed);
        await Assert.That(result.Projects.Single().Blockers.Select(b => b.Code)).IsEquivalentTo([BlockerCode.MsBuildHostFailed]);
        await Assert.That(result.Projects.Single().Classification).IsEqualTo(ProjectClassification.Unknown);
    }

    [Test]
    public async Task MissingDotNetAndNoBundledHost_IsReportedAsMsBuildHostFailed()
    {
        using HostLayout layout = HostLayout.Create(frameworkHost: HostBehavior.Working, bundledHost: null);
        using EnvironmentVariableScope noDotNet = new("DOTNET_HOST_PATH", Path.Combine(layout.Root, "no-such-dotnet"));

        InventoryResult result = layout.Inventory();

        await Assert.That(result.Blockers.Select(b => b.Code)).Contains(BlockerCode.MsBuildHostFailed);
        await Assert.That(result.Projects.Single().Blockers.Select(b => b.Code)).IsEquivalentTo([BlockerCode.MsBuildHostFailed]);
    }

    [Test]
    public async Task BundledHostWithoutExecutePermission_IsMadeExecutable()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("Unix file modes only.");
            return;
        }

        using HostLayout layout = HostLayout.Create(frameworkHost: HostBehavior.NeedsAnUninstalledRuntime, bundledHost: HostBehavior.Working);
        string apphost = Directory.GetFiles(layout.BundledDirectory, "vstest-to-mtp-msbuild-host").Single();

        // What package extraction leaves behind: a regular file without the execute bits.
        File.SetUnixFileMode(apphost, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);

        InventoryResult result = layout.Inventory();

        await Assert.That(result.Blockers).IsEmpty();
    }

    public enum HostBehavior
    {
        Working,
        NeedsAnUninstalledRuntime,
        CrashesOnStart,
    }

    private sealed class HostLayout : IDisposable
    {
        private readonly ScenarioWorkspace _workspace;

        private HostLayout(ScenarioWorkspace workspace, string root)
        {
            _workspace = workspace;
            Root = root;
        }

        public string Root { get; }

        public string BundledDirectory => Path.Combine(Root, "msbuild-host-bundled");

        // Through the public API, with the host folders pointed at this layout instead of the tool's own directory.
        public InventoryResult Inventory() =>
            InventoryBuilder.Build(_workspace.PathOf("App/App.csproj"), new InventoryOptions(_workspace.RootPath, MsBuildHostDirectory: Root));

        public static HostLayout Create(HostBehavior? frameworkHost, HostBehavior? bundledHost)
        {
            ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject("App/App.csproj");
            string root = Path.Combine(workspace.RootPath, "layout");

            if (frameworkHost is { } framework)
            {
                CopyHost(Path.Combine(root, "msbuild-host"), framework);
            }

            if (bundledHost is { } bundled)
            {
                CopyHost(Path.Combine(root, "msbuild-host-bundled"), bundled);
            }

            return new HostLayout(workspace, root);
        }

        private static void CopyHost(string destination, HostBehavior behavior)
        {
            Directory.CreateDirectory(destination);
            foreach (string file in Directory.GetFiles(BuiltHostDirectory))
            {
                File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
            }

            switch (behavior)
            {
                case HostBehavior.NeedsAnUninstalledRuntime:
                    RewriteRuntimeConfig(destination);
                    break;
                case HostBehavior.CrashesOnStart:
                    // The host cannot load its contract assembly, so it dies with an unhandled exception after the runtime started.
                    File.Delete(Path.Combine(destination, "VsTestToMtp.Contracts.dll"));
                    break;
            }
        }

        private static void RewriteRuntimeConfig(string directory)
        {
            string path = Path.Combine(directory, "vstest-to-mtp-msbuild-host.runtimeconfig.json");
            JsonNode config = JsonNode.Parse(File.ReadAllText(path))!;
            JsonObject options = config["runtimeOptions"]!.AsObject();
            options.Remove("rollForward");
            options["framework"]!["version"] = "99.0.0";
            options["rollForward"] = "Disable";
            File.WriteAllText(path, config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }

        public void Dispose() => _workspace.Dispose();
    }
}
