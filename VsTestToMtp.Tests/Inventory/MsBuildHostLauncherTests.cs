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

        HostResponse response = MsBuildHostLauncher.Run(layout.Request, layout.Root);

        await Assert.That(response.Blockers).IsEmpty();
        await Assert.That(response.Projects.Single().Classification).IsEqualTo(ProjectClassification.Production);
    }

    [Test]
    public async Task MissingFramework_FallsBackToTheBundledHost()
    {
        // hostfxr exits with FrameworkMissingFailure when no installed runtime satisfies the host's runtimeconfig.
        using HostLayout layout = HostLayout.Create(frameworkHost: HostBehavior.NeedsAnUninstalledRuntime, bundledHost: HostBehavior.Working);

        HostResponse response = MsBuildHostLauncher.Run(layout.Request, layout.Root);

        await Assert.That(response.Blockers).IsEmpty();
        await Assert.That(response.Projects.Single().Classification).IsEqualTo(ProjectClassification.Production);
    }

    [Test]
    public async Task UnrelatedHostFailure_DoesNotFallBackToTheBundledHost()
    {
        // The framework-dependent host starts but crashes; running the bundled host would hide that and could mask a real defect.
        using HostLayout layout = HostLayout.Create(frameworkHost: HostBehavior.CrashesOnStart, bundledHost: HostBehavior.Working);

        HostResponse response = MsBuildHostLauncher.Run(layout.Request, layout.Root);

        await Assert.That(response.Blockers.Select(b => b.Code)).IsEquivalentTo([BlockerCode.MsBuildHostFailed]);
        await Assert.That(response.Projects.Single().Classification).IsEqualTo(ProjectClassification.Unknown);
    }

    [Test]
    public async Task MissingDotNetAndNoBundledHost_IsReportedAsMsBuildHostFailed()
    {
        using HostLayout layout = HostLayout.Create(frameworkHost: HostBehavior.Working, bundledHost: null);
        using EnvironmentVariableScope noDotNet = new("DOTNET_HOST_PATH", Path.Combine(layout.Root, "no-such-dotnet"));

        HostResponse response = MsBuildHostLauncher.Run(layout.Request, layout.Root);

        await Assert.That(response.Blockers.Select(b => b.Code)).IsEquivalentTo([BlockerCode.MsBuildHostFailed]);
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

        HostResponse response = MsBuildHostLauncher.Run(layout.Request, layout.Root);

        await Assert.That(response.Blockers).IsEmpty();
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

        private HostLayout(ScenarioWorkspace workspace, string root, HostRequest request)
        {
            _workspace = workspace;
            Root = root;
            Request = request;
        }

        public string Root { get; }

        public string BundledDirectory => Path.Combine(Root, "msbuild-host-bundled");

        public HostRequest Request { get; }

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

            HostRequest request = new(workspace.RootPath, workspace.RootPath, [workspace.PathOf("App/App.csproj")], null);
            return new HostLayout(workspace, root, request);
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
