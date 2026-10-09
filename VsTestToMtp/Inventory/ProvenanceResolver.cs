using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace VsTestToMtp.Inventory;

/// <summary>Per-project helpers for turning MSBuild elements into <see cref="Provenance"/>.</summary>
internal sealed class ProvenanceResolver
{
    private readonly Dictionary<string, ResolvedImport> _importsByFile = new(PathComparison.Comparer);
    private readonly PathFormatter _formatter;
    private readonly string[] _externalRoots;
    private readonly Project _project;

    public ProvenanceResolver(Project project, string projectPath, PathFormatter formatter)
    {
        ProjectPath = projectPath;
        _formatter = formatter;
        _project = project;

        foreach (ResolvedImport import in project.Imports)
        {
            _importsByFile.TryAdd(import.ImportedProject.FullPath, import);
        }

        // Not user-owned: the .NET root (SDKs, packs, workload manifests), the NuGet cache, and every package an SDK was resolved from.
        // The SDK itself reports the .NET root ($(NetCoreRoot)); MSBuild reports where each SDK resolved, which also covers a versioned
        // SDK (MSTest.Sdk/x.y) restored to a globalPackagesFolder set in NuGet.config, which nothing else here can see.
        string toolsPath = project.GetPropertyValue("MSBuildToolsPath").TrimEnd('/', '\\');
        string? dotnetRoot = project.GetPropertyValue("NetCoreRoot") is { Length: > 0 } netCoreRoot ? netCoreRoot
            : toolsPath.Length == 0 ? null
            : Path.GetDirectoryName(Path.GetDirectoryName(toolsPath));
        string nugetRoot = project.GetPropertyValue("RestorePackagesPath") is { Length: > 0 } restorePackagesPath ? restorePackagesPath
            : Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } nugetPackages ? nugetPackages
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        IEnumerable<string?> sdkPackages = project.Imports
            .Where(i => i.SdkResult is { Success: true })
            .SelectMany(i => (IEnumerable<string?>)[i.SdkResult.Path, .. i.SdkResult.AdditionalPaths ?? []])
            .Select(path => path is null ? null : Path.GetDirectoryName(path.TrimEnd('/', '\\')));
        _externalRoots = [.. new[] { dotnetRoot, nugetRoot }.Concat(sdkPackages)
            .Where(r => !string.IsNullOrEmpty(r))
            .Select(r => Path.GetFullPath(r!).TrimEnd('/', '\\'))
            .Distinct(PathComparison.Comparer)
            // An SDK resolved from inside the repository must not turn the project's own directory tree into "external".
            .Where(r => !Path.GetFullPath(projectPath).StartsWith(r + Path.DirectorySeparatorChar, PathComparison.Comparison))];
    }

    public string ProjectPath { get; }

    /// <summary>The project's elements in evaluation order, built once and shared by every check that needs it.</summary>
    public EvaluationOrder Order => field ??= new EvaluationOrder(_project);

    public bool IsRepositoryFile(string file)
    {
        string full = Path.GetFullPath(file);
        return !_externalRoots.Any(root => full.StartsWith(root + Path.DirectorySeparatorChar, PathComparison.Comparison));
    }

    public SourceLocation LocationOf(ProjectElement element) =>
        _formatter.Location(element.Location.File, element.Location.Line, element.Location.Column);

    public static string? JoinConditions(ProjectElement element)
    {
        List<string> conditions = Conditions(element);
        return conditions.Count == 0 ? null : string.Join(" and ", conditions);
    }

    public Provenance Provenance(ProjectElement element)
    {
        string file = element.Location.File;
        List<ImportStep> chain = [];
        HashSet<string> seen = new(PathComparison.Comparer);
        for (string current = file;
            !PathComparison.Equal(current, ProjectPath) && _importsByFile.TryGetValue(current, out ResolvedImport import) && seen.Add(current);)
        {
            ProjectImportElement importing = import.ImportingElement;
            chain.Insert(0, new ImportStep(LocationOf(importing), JoinConditions(importing)));
            current = importing.ContainingProject.FullPath;
        }

        return new Provenance(
            LocationOf(element),
            Conditions(element),
            chain,
            PathComparison.Equal(file, ProjectPath),
            IsRepositoryFile(file));
    }

    public static List<string> Conditions(ProjectElement element)
    {
        List<string> conditions = [];
        for (ProjectElement? current = element; current is not null; current = current.Parent)
        {
            if (current is not ProjectRootElement && current.Condition.Trim() is { Length: > 0 } condition)
            {
                conditions.Insert(0, condition);
            }
        }

        return conditions;
    }
}
