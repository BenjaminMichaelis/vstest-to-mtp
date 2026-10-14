namespace VsTestToMtp.Inventory;

/// <summary>
/// Builds a read-only inventory of a <c>.csproj</c>, <c>.sln</c> or <c>.slnx</c>: exact solution membership,
/// per-target-framework evaluated MSBuild state with provenance, and test-application classification.
/// Nothing in the repository is restored, built or written, and anything that cannot be determined becomes a blocker.
/// As with any MSBuild evaluation, an SDK referenced with a version (such as <c>MSTest.Sdk/3.6.4</c>) that is not yet in the
/// NuGet global packages folder is downloaded there by MSBuild's NuGet SDK resolver, as <c>dotnet restore</c> would.
/// </summary>
public static class InventoryBuilder
{
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
            : EvaluateProjects(selection, selectionDirectory, formatter, options, blockers);

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
            BlockerOrdering.Normalize(all));
    }

    // MSBuild is evaluated in a separate host process (the CLI is Native AOT and cannot load it). Each run gets its own host, so
    // MSBuild is registered once per process and different global.json selections never share one.
    private static List<ProjectInventory> EvaluateProjects(
        SolutionReader.Selection selection,
        string selectionDirectory,
        PathFormatter formatter,
        InventoryOptions? options,
        List<InventoryBlocker> blockers)
    {
        HostResponse response = MsBuildHostLauncher.Run(
            new HostRequest(selectionDirectory, formatter.Root, selection.ProjectPaths, options?.SdkPackagesDirectory));
        blockers.AddRange(response.Blockers);
        return [.. response.Projects];
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
