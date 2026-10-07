namespace VsTestToMtp.Tests.Fixtures;

using System.Diagnostics;
using System.Text.RegularExpressions;

using TUnit.Core.Interfaces;

/// <summary>
/// Runs the real <c>dotnet test</c> (VSTest mode) against an isolated copy of each committed fixture
/// and checks the documented baseline. Needs the .NET 10 SDK and NuGet access.
/// </summary>
[Category("Integration")]
[ParallelLimiter<DotNetTestParallelLimit>]
public partial class FixtureBaselineTests
{
    private static readonly string[] InheritedMsBuildVariables =
    [
        "MSBUILD_EXE_PATH",
        "MSBuildSDKsPath",
        "MSBuildExtensionsPath",
        "DOTNET_CLI_TELEMETRY_SESSIONID",
    ];

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Fixture_DotNetTestInVsTestMode_MatchesDocumentedBaseline(FixtureInfo fixture)
    {
        string originalHash = FixtureWorkspace.ComputeHash(fixture.RootPath);
        using FixtureWorkspace workspace = FixtureWorkspace.Create(fixture);

        (int exitCode, string output) = await RunDotNetTestAsync(workspace.RootPath);

        Match summary = TestSummaryRegex().Match(output);
        await Assert.That(summary.Success).IsTrue().Because(output);

        // Report every mismatch at once; the committed original must also be untouched.
        using (Assert.Multiple())
        {
            await Assert.That(exitCode).IsEqualTo(0);
            await Assert.That(int.Parse(summary.Groups["failed"].Value)).IsEqualTo(0);
            await Assert.That(int.Parse(summary.Groups["passed"].Value)).IsEqualTo(fixture.ExpectedTotalTests);
            await Assert.That(int.Parse(summary.Groups["total"].Value)).IsEqualTo(fixture.ExpectedTotalTests);
            await Assert.That(FixtureWorkspace.ComputeHash(fixture.RootPath)).IsEqualTo(originalHash);
        }
    }

    private static async Task<(int ExitCode, string Output)> RunDotNetTestAsync(string workingDirectory)
    {
        ProcessStartInfo startInfo = new("dotnet", ["test", "Fixture.slnx", "--nologo"])
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (string variable in InheritedMsBuildVariables)
        {
            startInfo.Environment.Remove(variable);
        }

        // Keep the copy's own build self-contained: no shared node reuse / build server with the outer test run.
        startInfo.Environment["DOTNET_CLI_UI_LANGUAGE"] = "en";
        startInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";

        using Process process = Process.Start(startInfo)!;
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync();
        Task<string> standardError = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        return (process.ExitCode, await standardOutput + await standardError);
    }

    [GeneratedRegex(@"Failed:\s+(?<failed>\d+),\s+Passed:\s+(?<passed>\d+),\s+Skipped:\s+(?<skipped>\d+),\s+Total:\s+(?<total>\d+)")]
    private static partial Regex TestSummaryRegex();
}

/// <summary>Caps concurrent real <c>dotnet test</c> runs so they don't contend for CPU, NuGet and build servers.</summary>
public record DotNetTestParallelLimit : IParallelLimit
{
    public int Limit => 2;
}
