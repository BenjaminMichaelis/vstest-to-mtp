namespace VsTestToMtp.Tests;

using System.CommandLine;

using VsTestToMtp;

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

    private static (int ExitCode, string Output, string Error) Invoke(params string[] args)
    {
        using StringWriter output = new();
        using StringWriter error = new();
        InvocationConfiguration configuration = new() { Output = output, Error = error };

        int exitCode = Tool.Run(args, configuration);

        return (exitCode, output.ToString(), error.ToString());
    }
}
