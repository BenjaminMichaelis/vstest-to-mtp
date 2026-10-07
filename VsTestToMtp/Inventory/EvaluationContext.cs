using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace VsTestToMtp.Inventory;

/// <summary>Per-project helpers for turning MSBuild elements into <see cref="Provenance"/>.</summary>
internal sealed class EvaluationContext
{
    private readonly Dictionary<string, ResolvedImport> _importsByFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly PathFormatter _formatter;
    private readonly string[] _externalRoots;

    public EvaluationContext(Project outer, string projectPath, PathFormatter formatter)
    {
        ProjectPath = projectPath;
        _formatter = formatter;

        foreach (ResolvedImport import in outer.Imports)
        {
            _importsByFile.TryAdd(import.ImportedProject.FullPath, import);
        }

        // The .NET root (SDKs, packs, workload manifests) and the NuGet cache are not user-owned.
        string toolsPath = outer.GetPropertyValue("MSBuildToolsPath").TrimEnd('/', '\\');
        string? dotnetRoot = toolsPath.Length == 0 ? null : Path.GetDirectoryName(Path.GetDirectoryName(toolsPath));
        string nugetRoot = Environment.GetEnvironmentVariable("NUGET_PACKAGES")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages");
        _externalRoots = [.. new[] { dotnetRoot, nugetRoot }.Where(r => !string.IsNullOrEmpty(r)).Select(r => Path.GetFullPath(r!))];
    }

    public string ProjectPath { get; }

    public static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public bool IsRepositoryFile(string file)
    {
        string full = Path.GetFullPath(file);
        return !_externalRoots.Any(root => full.StartsWith(root + Path.DirectorySeparatorChar, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
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
        HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
        for (string current = file;
            !PathsEqual(current, ProjectPath) && _importsByFile.TryGetValue(current, out ResolvedImport import) && seen.Add(current);)
        {
            ProjectImportElement importing = import.ImportingElement;
            chain.Insert(0, new ImportStep(LocationOf(importing), JoinConditions(importing)));
            current = importing.ContainingProject.FullPath;
        }

        return new Provenance(
            LocationOf(element),
            Conditions(element),
            chain,
            PathsEqual(file, ProjectPath),
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
