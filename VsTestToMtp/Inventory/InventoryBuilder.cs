using System.Runtime.CompilerServices;

namespace VsTestToMtp.Inventory;

/// <summary>
/// Builds a read-only inventory of a <c>.csproj</c>, <c>.sln</c> or <c>.slnx</c>: exact solution membership,
/// per-target-framework evaluated MSBuild state with provenance, and test-application classification.
/// Nothing is restored, built or written, and anything that cannot be determined becomes a blocker.
/// </summary>
public static class InventoryBuilder
{
    // MSBuild's evaluation caches are process-wide; evaluating concurrently only adds contention (several times slower).
    private static readonly Lock EvaluationLock = new();

    public static InventoryResult Build(string path, InventoryOptions? options = null)
    {
        string fullPath = Path.GetFullPath(path);
        string startDirectory = Directory.Exists(fullPath) ? fullPath : Path.GetDirectoryName(fullPath)!;
        string root = options?.RootPath is { } configured
            ? Path.GetFullPath(configured)
            : FindRepositoryRoot(startDirectory) ?? startDirectory;
        PathFormatter formatter = new(root);

        SolutionReader.Selection selection = SolutionReader.Resolve(fullPath, formatter);
        string selectionDirectory = Directory.Exists(selection.Path) ? selection.Path : Path.GetDirectoryName(selection.Path)!;

        List<InventoryBlocker> blockers = [.. selection.Blockers];
        GlobalJsonInfo? globalJson = RepositoryScanner.ReadGlobalJson(selectionDirectory, formatter, blockers);
        IReadOnlyList<AutomationFile> automation = RepositoryScanner.FindAutomation(formatter, blockers);

        List<ProjectInventory> projects = selection.ProjectPaths.Count == 0
            ? []
            : EvaluateProjects(selection, selectionDirectory, formatter, blockers);

        projects = [.. projects
            .OrderBy(p => p.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Path, StringComparer.Ordinal)];

        IEnumerable<InventoryBlocker> all = blockers.Concat(projects.SelectMany(p => p.Blockers));
        return new InventoryResult(
            formatter.Root,
            new SelectionInfo(formatter.Format(selection.Path), selection.Kind),
            projects,
            globalJson,
            automation,
            [.. all
                .DistinctBy(b => (b.Code, b.Project, b.Location, b.Message))
                .OrderBy(b => b.Project, StringComparer.Ordinal)
                .ThenBy(b => b.Code, StringComparer.Ordinal)
                .ThenBy(b => b.Location?.File, StringComparer.Ordinal)
                .ThenBy(b => b.Location?.Line)
                .ThenBy(b => b.Message, StringComparer.Ordinal)]);
    }

    // Kept separate and un-inlined: MSBuild must be registered before anything touching Microsoft.Build is JIT-compiled.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<ProjectInventory> EvaluateProjects(
        SolutionReader.Selection selection,
        string selectionDirectory,
        PathFormatter formatter,
        List<InventoryBlocker> blockers)
    {
        if (!MsBuildEnvironment.TryRegister(selectionDirectory, out string? errorCode, out string? error))
        {
            InventoryBlocker blocker = errorCode == BlockerCodes.MsBuildSdkMismatch
                ? new(
                    BlockerCodes.MsBuildSdkMismatch, BlockerSeverity.Error,
                    error!,
                    "Inventory repositories that need different SDKs in separate processes (one vstest-to-mtp run per global.json).",
                    null, null)
                : new(
                    BlockerCodes.MsBuildNotFound, BlockerSeverity.Error,
                    $"MSBuild could not be located: {error}",
                    "Install the .NET SDK that the repository's global.json selects, then rerun.",
                    null, null);
            blockers.Add(blocker);
            return
            [
                .. selection.ProjectPaths.Select(p => new ProjectInventory(
                    formatter.Format(p), Path.GetFileNameWithoutExtension(p), ProjectClassification.Unknown, false,
                    [], [], [], [blocker])),
            ];
        }

        return EvaluateRegistered(selection, formatter);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<ProjectInventory> EvaluateRegistered(SolutionReader.Selection selection, PathFormatter formatter)
    {
        HashSet<string> selected = new(selection.ProjectPaths, PathComparison.Comparer);
        ProjectEvaluator evaluator = new(formatter, selected);

        lock (EvaluationLock)
        {
            return
            [
                .. selection.ProjectPaths.Select(p =>
                    TestProjectClassifier.Classify(evaluator.Evaluate(p), Path.GetFileNameWithoutExtension(p))),
            ];
        }
    }

    private static string? FindRepositoryRoot(string startDirectory)
    {
        for (DirectoryInfo? directory = new(startDirectory); directory is not null; directory = directory.Parent)
        {
            string git = Path.Combine(directory.FullName, ".git");
            if (Directory.Exists(git) || File.Exists(git))
            {
                return directory.FullName;
            }
        }

        return null;
    }
}
