using System.CommandLine;
using System.CommandLine.Help;
using System.Text.Json;

using VsTestToMtp.Inventory;

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
        rootCommand.Subcommands.Add(CreateInventoryCommand());

        return rootCommand;
    }

    /// <summary>
    /// Parses and invokes the command line, returning the process exit code.
    /// </summary>
    public static int Run(string[] args, InvocationConfiguration? configuration = null) =>
        CreateRootCommand().Parse(args).Invoke(configuration);

    // A string option rather than an enum one: the parser restricts the values, and it stays trim-safe under Native AOT.
    private static MsBuildHostRuntime ParseHostRuntime(string value) => value switch
    {
        "machine" => MsBuildHostRuntime.Machine,
        "bundled" => MsBuildHostRuntime.Bundled,
        _ => MsBuildHostRuntime.Auto,
    };

    private static Command CreateInventoryCommand()
    {
        Argument<string> path = new("path")
        {
            Description = "A .csproj, .sln or .slnx file, or a directory containing exactly one solution or project.",
        };
        Option<string?> root = new("--root")
        {
            Description = "Directory the output's relative paths are anchored to. Defaults to the nearest ancestor containing .git.",
        };

        Option<string> hostRuntime = new("--host-runtime")
        {
            Description = "Which .NET runtime evaluates the projects: auto (the machine's runtime unless the bundled one is newer), machine, or bundled.",
            DefaultValueFactory = _ => "auto",
        };
        hostRuntime.AcceptOnlyFromAmong("auto", "machine", "bundled");

        Command inventory = new("inventory", "Prints a read-only JSON inventory of the selected projects: MSBuild-evaluated state, test project classification and blockers.")
        {
            path,
            root,
            hostRuntime,
        };

        inventory.SetAction(parseResult =>
        {
            InventoryResult result = InventoryBuilder.Build(
                parseResult.GetRequiredValue(path),
                new InventoryOptions(
                    RootPath: parseResult.GetValue(root),
                    HostRuntime: ParseHostRuntime(parseResult.GetRequiredValue(hostRuntime))));

            parseResult.InvocationConfiguration.Output.WriteLine(
                JsonSerializer.Serialize(result, InventoryOutputJsonContext.Default.InventoryResult));
            return 0;
        });

        return inventory;
    }
}
