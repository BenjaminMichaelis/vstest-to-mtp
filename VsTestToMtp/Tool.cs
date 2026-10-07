using System.CommandLine;
using System.CommandLine.Help;

namespace VsTestToMtp;

/// <summary>
/// Builds and runs the <c>vstest-to-mtp</c> command line, kept separate from Program.cs for testability.
/// </summary>
public static class Tool
{
    /// <summary>
    /// The name users type to invoke the tool.
    /// </summary>
    public const string CommandName = "vstest-to-mtp";

    /// <summary>
    /// The description shown in <c>--help</c> output.
    /// </summary>
    public const string Description =
        "Migrates .NET test projects from VSTest to Microsoft.Testing.Platform (MTP).";

    /// <summary>
    /// Creates the root command for the tool.
    /// </summary>
    public static RootCommand CreateRootCommand()
    {
        RootCommand rootCommand = new(Description);

        // No migration behavior yet: invoking the tool without arguments shows help.
        rootCommand.SetAction(parseResult => new HelpAction().Invoke(parseResult));

        return rootCommand;
    }

    /// <summary>
    /// Parses and invokes the command line, returning the process exit code.
    /// </summary>
    public static int Run(string[] args, InvocationConfiguration? configuration = null) =>
        CreateRootCommand().Parse(args).Invoke(configuration);
}
