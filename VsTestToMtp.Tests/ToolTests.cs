namespace VsTestToMtp.Tests;

using System.CommandLine;
using System.Text.Json;

using VsTestToMtp;
using VsTestToMtp.Tests.Fixtures;

public class ToolTests
{
    [Test]
    public async Task Run_WithHelpOption_PrintsDescriptionAndSucceeds()
    {
        (int exitCode, string output, _) = Invoke("--help");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(output).Contains(Tool.Description);
    }

    [Test]
    public async Task Run_WithNoArguments_PrintsHelpAndSucceeds()
    {
        (int exitCode, string output, _) = Invoke();

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(output).Contains(Tool.Description);
    }

    [Test]
    public async Task Run_WithVersionOption_Succeeds()
    {
        (int exitCode, string output, _) = Invoke("--version");

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(output.Trim()).IsNotEmpty();
    }

    [Test]
    public async Task Run_WithUnknownOption_Fails()
    {
        (int exitCode, _, string error) = Invoke("--not-a-real-option");

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(error).Contains("--not-a-real-option");
    }


    [Test]
    public async Task Run_InventoryOnAFixture_PrintsTheInventoryAsJson()
    {
        string solution = Path.Combine(FixtureCatalog.All[0].RootPath, "Fixture.slnx");

        (int exitCode, string output, string error) = Invoke("inventory", solution);

        await Assert.That(error).IsEmpty();
        await Assert.That(exitCode).IsEqualTo(0);
        using JsonDocument json = JsonDocument.Parse(output);
        JsonElement projects = json.RootElement.GetProperty("projects");
        await Assert.That(projects.GetArrayLength()).IsEqualTo(2);
        await Assert.That(json.RootElement.GetProperty("blockers").GetArrayLength()).IsEqualTo(0);
        await Assert.That(projects.EnumerateArray().Select(p => p.GetProperty("classification").GetString()!))
            .IsEquivalentTo(["Production", "TestApplication"]);
    }

    [Test]
    [Arguments("auto")]
    [Arguments("machine")]
    public async Task Run_InventoryWithAHostRuntime_Succeeds(string hostRuntime)
    {
        // The test output ships only the machine-runtime host, so 'bundled' is covered by the launcher tests and the packed-tool smoke test.
        string solution = Path.Combine(FixtureCatalog.All[0].RootPath, "Fixture.slnx");

        (int exitCode, string output, string error) = Invoke("inventory", solution, "--host-runtime", hostRuntime);

        await Assert.That(error).IsEmpty();
        await Assert.That(exitCode).IsEqualTo(0);
        using JsonDocument json = JsonDocument.Parse(output);
        await Assert.That(json.RootElement.GetProperty("blockers").GetArrayLength()).IsEqualTo(0);
    }

    [Test]
    public async Task Run_InventoryWithAnUnknownHostRuntime_Fails()
    {
        (int exitCode, _, string error) = Invoke("inventory", "somewhere", "--host-runtime", "fastest");

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(error).Contains("fastest");
    }

    [Test]
    public async Task Run_InventoryWithoutAPath_Fails()
    {
        (int exitCode, _, string error) = Invoke("inventory");

        await Assert.That(exitCode).IsNotEqualTo(0);
        await Assert.That(error).Contains("Required argument missing");
    }

    private static (int ExitCode, string Output, string Error) Invoke(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        InvocationConfiguration configuration = new() { Output = output, Error = error };

        int exitCode = Tool.Run(args, configuration);

        return (exitCode, output.ToString(), error.ToString());
    }
}
