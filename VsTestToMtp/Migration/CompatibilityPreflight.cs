using NuGet.Versioning;

using VsTestToMtp.Inventory;

namespace VsTestToMtp.Migration;

/// <summary>
/// Decides, before anything is changed, which inventoried projects can move to Microsoft.Testing.Platform v2 under
/// <see cref="MigrationRuleSet.Current"/>, which are already there and which are blocked, and plans the in-family package changes the move
/// needs. It works only from the inventory's evaluated state (properties, package references and their declarations); file and project
/// names never decide anything. Whatever cannot be decided is an explicit error diagnostic, never a guess: a test framework is never
/// migrated (xUnit v2 stays xUnit v2) and a project is never retargeted. Each project is assessed by <see cref="ProjectAssessment"/>;
/// what applies to the selection as a whole (its SDK, its <c>global.json</c> runner, mixing runners) is decided here.
/// </summary>
public static class CompatibilityPreflight
{
    public static PreflightResult Assess(InventoryResult inventory)
    {
        MigrationRuleSet rules = MigrationRuleSet.Current;
        List<PreflightDiagnostic> selection = [];
        foreach (InventoryBlocker blocker in inventory.Blockers.Where(b => b.Project is null && b.Severity != BlockerSeverity.Info))
        {
            selection.Add(PreflightDiagnostic.Error(PreflightCode.UnresolvedSelection,
                $"The selection could not be inventoried completely ({blocker.Code}): {blocker.Message}",
                blocker.Remediation,
                location: blocker.Location));
        }

        List<(ProjectInventory Inventory, ProjectPreflight Preflight)> projects =
            [.. inventory.Projects.Select(p => (p, new ProjectAssessment(p, inventory, rules).Assess()))];

        AddSdkDiagnostics(inventory, rules, selection);
        AddRunnerDiagnostics(projects, inventory, rules, selection);

        return new PreflightResult(
            rules.Id,
            rules.Version,
            [.. projects.Select(p => p.Preflight)],
            PreflightDiagnostic.Normalize(selection.Concat(projects.SelectMany(p => p.Preflight.Diagnostics))));
    }

    // MTP v2 only runs under the MTP mode of `dotnet test`, which the .NET 10 SDK introduced. The SDK MSBuild evaluated with is the one
    // global.json resolves to for the selection, and every evaluated project reports it as NETCoreSdkVersion. (A test application that
    // does not report it is blocked on its own; see ProjectAssessment.)
    private static void AddSdkDiagnostics(InventoryResult inventory, MigrationRuleSet rules, List<PreflightDiagnostic> diagnostics)
    {
        string[] versions = [.. inventory.Projects
            .SelectMany(p => p.TargetFrameworks)
            .Select(tf => tf.GetProperty(MsBuildPropertyNames.NETCoreSdkVersion)?.Value)
            .OfType<string>()
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        foreach (string version in versions)
        {
            if (!NuGetVersion.TryParse(version, out NuGetVersion? sdk) || sdk < rules.MinimumSdkVersion)
            {
                diagnostics.Add(PreflightDiagnostic.Error(PreflightCode.UnsupportedSdk,
                    $"The selection is evaluated with .NET SDK {version}, but Microsoft.Testing.Platform v2 needs the MTP mode of `dotnet test`, which the .NET SDK {rules.MinimumSdkVersion} and later provide.",
                    $"Install the .NET SDK {rules.MinimumSdkVersion.Major} or later and allow it in global.json (sdk.version/rollForward), then rerun.",
                    location: GlobalJsonLocation(inventory)));
            }
        }
    }

    // global.json's test.runner switches every project `dotnet test` runs from that directory: the MTP mode refuses VSTest projects
    // ("All projects must use that test runner") and the VSTest mode cannot run MTP v2 ones.
    private static void AddRunnerDiagnostics(
        List<(ProjectInventory Inventory, ProjectPreflight Preflight)> projects,
        InventoryResult inventory,
        MigrationRuleSet rules,
        List<PreflightDiagnostic> diagnostics)
    {
        string[] mtp = [.. projects
            .Where(p => p.Preflight.Readiness is MigrationReadiness.Eligible or MigrationReadiness.AlreadyMigrated)
            .Select(p => p.Inventory.Path)];
        if (mtp.Length == 0)
        {
            return;
        }

        bool selectsMtp = string.Equals(inventory.GlobalJson?.TestRunner, rules.TestRunner, StringComparison.OrdinalIgnoreCase);
        string where = inventory.GlobalJson is { } globalJson
            ? $"'{globalJson.Path}' selects {(inventory.GlobalJson.TestRunner is { Length: > 0 } runner ? runner : "VSTest (no test.runner)")}"
            : "There is no global.json, so `dotnet test` uses VSTest";

        // A test application already on MTP v2 does not run under the VSTest mode at all, so it is broken until global.json selects MTP.
        // For one that still has to move, selecting MTP is part of the migration. Either way it is not a reason to stop.
        if (!selectsMtp)
        {
            diagnostics.Add(PreflightDiagnostic.Warning(PreflightCode.TestRunnerNotSelected,
                $"{where}, but `dotnet test` only runs Microsoft.Testing.Platform v2 test applications in its MTP mode. These run (or will run) on it: {string.Join(", ", mtp)}.",
                $"Select it in global.json at the root of the run configuration: \"test\": {{ \"runner\": \"{rules.TestRunner}\" }}.",
                location: GlobalJsonLocation(inventory)));
        }

        // Migrating only some test applications of a run configuration leaves it unrunnable, and a project that could not be classified
        // may be either.
        string[] vstest = [.. projects
            .Where(p => p is { Preflight.Readiness: MigrationReadiness.Blocked, Inventory.Classification: ProjectClassification.TestApplication }
                && (p.Inventory.Runners is [] || p.Inventory.Runners.Contains(TestRunnerKind.VSTest)))
            .Select(p => p.Inventory.Path)];
        string[] undetermined = [.. projects
            .Where(p => p.Inventory.Classification is ProjectClassification.Unknown or ProjectClassification.Mixed)
            .Select(p => p.Inventory.Path)];
        if (vstest.Length == 0 && undetermined.Length == 0)
        {
            return;
        }

        string remaining = string.Join("; ", [
            .. vstest.Length > 0 ? [$"would stay on VSTest: {string.Join(", ", vstest)}"] : Array.Empty<string>(),
            .. undetermined.Length > 0 ? [$"could not be classified: {string.Join(", ", undetermined)}"] : Array.Empty<string>()]);
        diagnostics.Add(PreflightDiagnostic.Error(PreflightCode.MixedTestRunners,
            $"Migrating would mix test runners in one `dotnet test` run configuration. {where} for every project in it; these would run on Microsoft.Testing.Platform: {string.Join(", ", mtp)}, but these {remaining}.",
            "Resolve the blocked projects first, or migrate a selection (solution or global.json scope) that does not contain them.",
            location: GlobalJsonLocation(inventory)) with
        {
            RelatedLocations = [.. vstest.Concat(undetermined).Select(SourceLocation.OfFile)],
        });
    }

    private static SourceLocation? GlobalJsonLocation(InventoryResult inventory) =>
        inventory.GlobalJson is { } globalJson ? SourceLocation.OfFile(globalJson.Path) : null;
}
