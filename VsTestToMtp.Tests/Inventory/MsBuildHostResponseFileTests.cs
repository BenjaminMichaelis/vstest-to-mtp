namespace VsTestToMtp.Tests.Inventory;

using System.Diagnostics;
using System.Text.Json;

using VsTestToMtp.Inventory;

/// <summary>
/// How the MSBuild host process writes its answer: to a file named on its command line, which lives in the shared temp directory and describes
/// a private repository. Run as a process, the way the launcher runs it, so this needs nothing internal.
/// </summary>
// Each case starts an MSBuild host process; other tests in this group also change process-wide environment variables the host inherits.
[NotInParallel("MSBuildEvaluation")]
public class MsBuildHostResponseFileTests
{
    [Test]
    public async Task ResponseFile_IsOwnerOnly_FromTheMomentItExists()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.Test("Unix file modes only.");
            return;
        }

        using ScenarioWorkspace workspace = new();
        string response = Path.Combine(workspace.RootPath, "response.json");

        (int exitCode, string error) = RunHost(workspace, response);

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(error).IsEmpty();
        await Assert.That(File.GetUnixFileMode(response)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task ResponseFile_ThatAlreadyExists_IsNeverOverwritten()
    {
        // The launcher names a fresh path, so a file already there was put there by someone else; the host must refuse it, not write through it.
        using ScenarioWorkspace workspace = new();
        string response = Path.Combine(workspace.RootPath, "response.json");
        File.WriteAllText(response, "not the host's");

        (int exitCode, _) = RunHost(workspace, response);

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(File.ReadAllText(response)).IsEqualTo("not the host's");
    }

    private static (int ExitCode, string Error) RunHost(ScenarioWorkspace workspace, string responseFile)
    {
        string host = Path.Combine(AppContext.BaseDirectory, "msbuild-host", "vstest-to-mtp-msbuild-host.dll");
        ProcessStartInfo start = new("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        start.ArgumentList.Add(host);
        start.ArgumentList.Add(responseFile);

        // No projects: the host still registers MSBuild for the directory and answers, which is all the response file needs.
        HostRequest request = new(workspace.RootPath, workspace.RootPath, [], null);

        using Process process = Process.Start(start)!;
        Task<string> output = process.StandardOutput.ReadToEndAsync();
        Task<string> error = process.StandardError.ReadToEndAsync();
        process.StandardInput.Write(JsonSerializer.Serialize(request, InventoryJsonContext.Default.HostRequest));
        process.StandardInput.Close();
        process.WaitForExit();
        output.GetAwaiter().GetResult();
        return (process.ExitCode, error.GetAwaiter().GetResult());
    }
}
