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
/// The host ships twice. The framework-dependent copy runs on the machine's .NET runtime when one is installed (rolling forward
/// to a newer major, which a newer SDK's MSBuild needs); the self-contained copy is used only when there is no usable machine runtime.
/// </remarks>
internal static class MsBuildHostLauncher
{
    private const string HostName = "vstest-to-mtp-msbuild-host";
    private const string FrameworkDirectory = "msbuild-host";
    private const string BundledDirectory = "msbuild-host-bundled";

    // hostfxr's FrameworkMissingFailure (0x80008096). Unix exit codes keep only the low byte.
    private const int FrameworkMissingWindows = unchecked((int)0x80008096);
    private const int FrameworkMissingUnix = 0x96;

    // The process could not be started at all (for example there is no `dotnet` on the machine).
    private const int NotStarted = -1;

    /// <param name="request">What to evaluate.</param>
    /// <param name="baseDirectory">Where the host folders are laid out; the running tool's own directory when not given.</param>
    public static HostResponse Run(HostRequest request, string? baseDirectory)
    {
        baseDirectory ??= AppContext.BaseDirectory;
        string frameworkHost = Path.Combine(baseDirectory, FrameworkDirectory, HostName + ".dll");
        string bundledHost = Path.Combine(baseDirectory, BundledDirectory, HostName + (OperatingSystem.IsWindows() ? ".exe" : string.Empty));
        bool hasFramework = File.Exists(frameworkHost);
        bool hasBundled = File.Exists(bundledHost);

        if (!hasFramework && !hasBundled)
        {
            return Failure(request, $"The MSBuild host was not found next to vstest-to-mtp ('{Path.Combine(baseDirectory, FrameworkDirectory)}').",
                "Reinstall vstest-to-mtp.");
        }

        if (hasFramework)
        {
            (HostResponse? response, int exitCode, string error) = TryRun(DotNetHostPath(), [frameworkHost], request);
            if (response is not null)
            {
                return response;
            }

            if (!hasBundled || !IsRuntimeUnavailable(exitCode))
            {
                return Failure(request, error, "Install the .NET SDK that the repository's global.json selects, then rerun.");
            }
        }

        EnsureExecutable(bundledHost);
        (HostResponse? bundled, _, string bundledError) = TryRun(bundledHost, [], request);
        return bundled ?? Failure(request, bundledError, "Reinstall vstest-to-mtp.");
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
                    Sdks: [],
                    Blockers: [blocker])),
            ]);
    }
}
