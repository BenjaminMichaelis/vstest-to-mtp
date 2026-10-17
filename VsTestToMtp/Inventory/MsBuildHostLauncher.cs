using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VsTestToMtp.Inventory;

/// <summary>
/// Runs the MSBuild host process (vstest-to-mtp-msbuild-host) that evaluates projects with the .NET SDK's MSBuild.
/// The CLI is Native AOT and cannot load managed MSBuild assemblies itself, so the host is a separate CoreCLR program
/// that exchanges <see cref="HostRequest"/>/<see cref="HostResponse"/> JSON over its standard streams.
/// </summary>
/// <remarks>
/// The host ships twice. The framework-dependent copy runs on the machine's .NET runtime (rolling forward to a newer major, which a
/// newer SDK's MSBuild needs); the self-contained copy carries its own runtime. <see cref="MsBuildHostRuntime.Auto"/> uses the machine's
/// runtime unless the bundled one is newer, and either copy can be pinned.
/// </remarks>
internal static class MsBuildHostLauncher
{
    private const string HostName = "vstest-to-mtp-msbuild-host";
    private const string FrameworkDirectory = "msbuild-host";
    private const string BundledDirectory = "msbuild-host-bundled";
    private const string BundledRuntimeVersionFile = "runtime-version.txt";

    // hostfxr's FrameworkMissingFailure (0x80008096). Unix exit codes keep only the low byte.
    private const int FrameworkMissingWindows = unchecked((int)0x80008096);
    private const int FrameworkMissingUnix = 0x96;

    // The process could not be started at all (for example there is no `dotnet` on the machine).
    private const int NotStarted = -1;

    /// <param name="request">What to evaluate.</param>
    /// <param name="baseDirectory">Where the host folders are laid out; the running tool's own directory when not given.</param>
    /// <param name="runtime">Which runtime to use; see <see cref="MsBuildHostRuntime"/>.</param>
    public static HostResponse Run(HostRequest request, string? baseDirectory, MsBuildHostRuntime runtime)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        string frameworkHost = Path.Combine(baseDirectory, FrameworkDirectory, HostName + ".dll");
        string bundledDirectory = Path.Combine(baseDirectory, BundledDirectory);
        string bundledHost = Path.Combine(bundledDirectory, HostName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        bool hasMachineHost = File.Exists(frameworkHost);
        bool hasBundledHost = File.Exists(bundledHost);

        if (!hasMachineHost && !hasBundledHost)
        {
            return Failure(request, $"The MSBuild host was not found next to vstest-to-mtp ('{Path.Combine(baseDirectory, FrameworkDirectory)}').",
                "Reinstall vstest-to-mtp.");
        }

        if (runtime == MsBuildHostRuntime.Machine && !hasMachineHost)
        {
            return Failure(request, "This installation has no MSBuild host that runs on the machine's .NET runtime.", "Use --host-runtime bundled or auto, or reinstall vstest-to-mtp.");
        }

        if (runtime == MsBuildHostRuntime.Bundled && !hasBundledHost)
        {
            return Failure(request, "This installation has no bundled .NET runtime (the portable 'any' package relies on the runtime installed on the machine).",
                "Use --host-runtime machine or auto, or install the platform-specific vstest-to-mtp package.");
        }

        // The order to try; a copy that cannot start for want of a runtime hands over to the next one (Auto only).
        List<HostKind> order = runtime switch
        {
            MsBuildHostRuntime.Machine => [HostKind.Machine],
            MsBuildHostRuntime.Bundled => [HostKind.Bundled],
            _ => AutoOrder(hasMachineHost, hasBundledHost, bundledDirectory),
        };

        string error = string.Empty;
        foreach (HostKind kind in order)
        {
            (HostResponse? response, int exitCode, error) = kind == HostKind.Machine
                ? TryRun(DotNetHostPath(), [frameworkHost], request)
                : TryRunBundled(bundledHost, request);
            if (response is not null)
            {
                return response;
            }

            if (!IsRuntimeUnavailable(exitCode))
            {
                break;
            }
        }

        return Failure(request, error, runtime == MsBuildHostRuntime.Bundled
            ? "Reinstall vstest-to-mtp."
            : "Install the .NET SDK that the repository's global.json selects, or use --host-runtime bundled where a bundled runtime ships.");
    }

    private enum HostKind
    {
        Machine,
        Bundled,
    }

    // The machine's runtime unless the bundled one is newer: a newer runtime is never worse for loading the SDK's MSBuild, and on a tie the
    // machine's wins (it is shared with other .NET processes and tends to carry newer patches). When the machine has no runtime we can find,
    // or the bundled version is unknown, there is nothing to compare, so the order falls back to what is known.
    private static List<HostKind> AutoOrder(bool hasMachineHost, bool hasBundledHost, string bundledDirectory)
    {
        if (!hasBundledHost)
        {
            return [HostKind.Machine];
        }

        if (!hasMachineHost)
        {
            return [HostKind.Bundled];
        }

        Version? machine = MachineRuntimeVersion();
        Version? bundled = BundledRuntimeVersion(bundledDirectory);
        bool preferBundled = machine is null || (bundled is not null && bundled > machine);
        return preferBundled ? [HostKind.Bundled, HostKind.Machine] : [HostKind.Machine, HostKind.Bundled];
    }

    // The newest Microsoft.NETCore.App the machine's `dotnet` reports; null when `dotnet` is missing or does not answer.
    private static Version? MachineRuntimeVersion()
    {
        ProcessStartInfo start = new(DotNetHostPath(), "--list-runtimes")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
            Task<string> output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(30)) || process.ExitCode != 0)
            {
                return null;
            }

            return ParseNewestRuntime(output.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    // Lines look like: Microsoft.NETCore.App 10.0.13 [path to the shared runtime folder]
    // Pre-release runtimes are skipped: the host's roll-forward ignores them too, so they are never what it would run on.
    private static Version? ParseNewestRuntime(string listRuntimes)
    {
        Version? newest = null;
        foreach (string line in listRuntimes.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0] == "Microsoft.NETCore.App"
                && !parts[1].Contains('-') && Version.TryParse(parts[1], out Version? version) && (newest is null || version > newest))
            {
                newest = version;
            }
        }

        return newest;
    }

    // Written next to the bundled host when the package is built (the runtime pack version the self-contained publish used).
    private static Version? BundledRuntimeVersion(string bundledDirectory)
    {
        try
        {
            string path = Path.Combine(bundledDirectory, BundledRuntimeVersionFile);
            return File.Exists(path) && Version.TryParse(File.ReadAllText(path).Trim(), out Version? version) ? version : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static (HostResponse? Response, int ExitCode, string Error) TryRunBundled(string bundledHost, HostRequest request)
    {
        EnsureExecutable(bundledHost);
        return TryRun(bundledHost, [], request);
    }

    // Package content does not keep the execute bit on Linux/macOS (the SDK only makes the tool's own entry point executable),
    // so the bundled apphost must be made executable before it can be started.
    private static void EnsureExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            UnixFileMode mode = File.GetUnixFileMode(path);
            UnixFileMode execute = UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
            if ((mode & execute) != execute)
            {
                File.SetUnixFileMode(path, mode | execute);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting it reports the real problem (permission denied) as an MsBuildHostFailed blocker.
        }
    }

    private static bool IsRuntimeUnavailable(int exitCode) => exitCode is NotStarted or FrameworkMissingWindows or FrameworkMissingUnix;

    private static string DotNetHostPath()
    {
        if (Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host)
        {
            return host;
        }

        string executable = OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
        return Environment.GetEnvironmentVariable("DOTNET_ROOT") is { Length: > 0 } root && File.Exists(Path.Combine(root, executable))
            ? Path.Combine(root, executable)
            : "dotnet";
    }

    private static (HostResponse? Response, int ExitCode, string Error) TryRun(string fileName, string[] arguments, HostRequest request)
    {
        ProcessStartInfo start = new(fileName)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        // The host writes its response to a file: native code it loads (hostfxr) prints diagnostics to standard output.
        string responseFile = Path.Combine(Path.GetTempPath(), $"vstest-to-mtp-{Guid.NewGuid():N}.json");
        start.ArgumentList.Add(responseFile);

        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException($"'{fileName}' did not start.");

            // Drain both streams while writing the request so a full pipe can never stall the host.
            Task<string> error = process.StandardError.ReadToEndAsync();
            Task<string> output = process.StandardOutput.ReadToEndAsync();

            process.StandardInput.Write(JsonSerializer.Serialize(request, InventoryJsonContext.Default.HostRequest));
            process.StandardInput.Close();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                return (null, process.ExitCode, $"The MSBuild host exited with code {process.ExitCode}: {error.GetAwaiter().GetResult().Trim()}");
            }

            using FileStream result = File.OpenRead(responseFile);
            HostResponse? response = JsonSerializer.Deserialize(result, InventoryJsonContext.Default.HostResponse);
            return response is null
                ? (null, 0, "The MSBuild host returned no result.")
                : (response, 0, string.Empty);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        {
            return (null, NotStarted, $"The MSBuild host could not be run: {ex.Message}");
        }
        finally
        {
            File.Delete(responseFile);
        }
    }

    private static HostResponse Failure(HostRequest request, string message, string remediation)
    {
        InventoryBlocker blocker = InventoryBlocker.Error(BlockerCode.MsBuildHostFailed, message, remediation);
        PathFormatter formatter = new(request.RootPath);
        return new HostResponse(
            [blocker],
            [
                .. request.ProjectPaths.Select(p => new ProjectInventory(
                    Path: formatter.Format(p),
                    Name: Path.GetFileNameWithoutExtension(p),
                    Classification: ProjectClassification.Unknown,
                    HasTestLibraryDependency: false,
                    Frameworks: [],
                    Runners: [],
                    TargetFrameworks: [],
                    Imports: [],
                    Blockers: [blocker])),
            ]);
    }
}
