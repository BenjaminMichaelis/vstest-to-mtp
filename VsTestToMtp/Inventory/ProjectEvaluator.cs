using System.Text.RegularExpressions;

using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;
using Microsoft.Build.Framework;

namespace VsTestToMtp.Inventory;

/// <summary>Raw evaluated state for one target framework, before classification.</summary>
internal sealed record EvaluatedTargetFramework(
    string TargetFramework,
    IReadOnlyList<PropertyState> Properties,
    IReadOnlyList<PackageReferenceState> Packages);

/// <summary>Raw evaluated state for one project, before classification.</summary>
internal sealed record EvaluatedProject(
    string Path,
    IReadOnlyList<EvaluatedTargetFramework> TargetFrameworks,
    IReadOnlyList<ProjectReferenceState> ProjectReferences,
    IReadOnlyList<ImportRecord> Imports,
    IReadOnlyList<InventoryBlocker> Blockers)
{
    /// <summary>Whether evaluation was incomplete in a way that makes classification a guess.</summary>
    public bool HasErrors => Blockers.Any(b => b.Severity == BlockerSeverity.Error);
}

/// <summary>
/// Evaluates a project with MSBuild (no restore, no build) once per target framework and extracts the
/// properties, package references, project references and imports the inventory reports, with provenance.
/// Must only be used after <see cref="MsBuildEnvironment.TryRegister"/> succeeded.
/// </summary>
internal sealed partial class ProjectEvaluator(PathFormatter formatter, IReadOnlySet<string> selectedProjects)
{
    private static readonly string[] RelevantProperties =
    [
        "CentralPackageVersionOverrideEnabled",
        "EnableMSTestRunner",
        "EnableNUnitRunner",
        "EnableTUnitRunner",
        "IsPackable",
        "IsTestProject",
        "ManagePackageVersionsCentrally",
        "OutputType",
        "TargetFramework",
        "TargetFrameworks",
        "TestingPlatformDotnetTestSupport",
        "UseMicrosoftTestingPlatformRunner",
    ];

    // Environment-provided properties that are present on every machine and not worth flagging.
    private static readonly HashSet<string> WellKnownEnvironmentProperties = new(StringComparer.OrdinalIgnoreCase) { "OS" };

    private readonly Dictionary<string, string> _globalProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ignore obj/*.nuget.g.props/targets so the result does not depend on whether (or when) the project was restored.
        ["ImportProjectExtensionProps"] = "false",
        ["ImportProjectExtensionTargets"] = "false",
        ["ExcludeRestorePackageImports"] = "true",
    };

    public EvaluatedProject Evaluate(string projectPath)
    {
        string displayPath = formatter.Format(projectPath);
        List<InventoryBlocker> blockers = [];
        ImportLogger logger = new();

        using ProjectCollection collection = new(_globalProperties, [logger], ToolsetDefinitionLocations.Default);

        Project? outer = Load(collection, projectPath, null, displayPath, blockers);
        if (outer is null)
        {
            return new EvaluatedProject(displayPath, [], [], [], Sorted(blockers));
        }

        Context context = new(outer, projectPath, formatter);

        string[] targetFrameworks = SplitFrameworks(outer.GetPropertyValue("TargetFrameworks"));
        if (targetFrameworks.Length == 0)
        {
            string single = outer.GetPropertyValue("TargetFramework").Trim();
            targetFrameworks = single.Length == 0 ? [] : [single];
        }

        List<EvaluatedTargetFramework> frameworks = [];
        List<ProjectReferenceState> references = [];
        List<Project> evaluated = [];

        if (targetFrameworks.Length == 0)
        {
            blockers.Add(new InventoryBlocker(
                BlockerCodes.NoTargetFramework, BlockerSeverity.Error,
                $"'{displayPath}' does not evaluate to a TargetFramework or TargetFrameworks value.",
                "Set TargetFramework (or TargetFrameworks) in the project, Directory.Build.props or an SDK-style import, then rerun.",
                displayPath, formatter.Location(projectPath)));
            frameworks.Add(Capture(outer, string.Empty, context, displayPath, blockers));
            evaluated.Add(outer);
        }
        else
        {
            foreach (string targetFramework in targetFrameworks.Order(StringComparer.Ordinal))
            {
                Project? project = targetFramework == outer.GetPropertyValue("TargetFramework").Trim() && outer.GetProperty("TargetFramework")?.IsGlobalProperty != true
                    ? outer
                    : Load(collection, projectPath, targetFramework, displayPath, blockers);
                if (project is null)
                {
                    continue;
                }

                frameworks.Add(Capture(project, targetFramework, context, displayPath, blockers));
                evaluated.Add(project);
            }
        }

        foreach (Project project in evaluated)
        {
            foreach (ProjectItem item in project.GetItems("ProjectReference"))
            {
                string full = Path.GetFullPath(item.GetMetadataValue("FullPath"));
                if (item.Xml is null || references.Any(r => r.Path == formatter.Format(full)))
                {
                    continue;
                }

                references.Add(new ProjectReferenceState(formatter.Format(full), selectedProjects.Contains(full), context.Provenance(item.Xml)));
            }
        }

        List<ImportRecord> imports = CaptureImports(outer, context);
        AddImportBlockers(logger, context, displayPath, blockers);
        AddEarlyIsTestProjectBlockers(outer, context, displayPath, blockers);

        return new EvaluatedProject(
            displayPath,
            frameworks,
            [.. references.OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase).ThenBy(r => r.Path, StringComparer.Ordinal)],
            imports,
            Sorted(blockers));
    }

    private Project? Load(ProjectCollection collection, string projectPath, string? targetFramework, string displayPath, List<InventoryBlocker> blockers)
    {
        Dictionary<string, string> global = new(_globalProperties, StringComparer.OrdinalIgnoreCase);
        if (targetFramework is not null)
        {
            global["TargetFramework"] = targetFramework;
        }

        try
        {
            return Project.FromFile(projectPath, new ProjectOptions
            {
                GlobalProperties = global,
                ProjectCollection = collection,
                LoadSettings = ProjectLoadSettings.RecordEvaluatedItemElements
                    | ProjectLoadSettings.IgnoreMissingImports
                    | ProjectLoadSettings.IgnoreInvalidImports
                    | ProjectLoadSettings.IgnoreEmptyImports,
            });
        }
        catch (InvalidProjectFileException ex)
        {
            string file = string.IsNullOrEmpty(ex.ProjectFile) ? projectPath : ex.ProjectFile;
            blockers.Add(new InventoryBlocker(
                BlockerCodes.EvaluationFailed, BlockerSeverity.Error,
                $"MSBuild could not evaluate '{displayPath}'{(targetFramework is null ? string.Empty : $" for {targetFramework}")}: {ex.ErrorCode} {ex.BaseMessage}",
                "Fix the reported problem (missing SDK, invalid XML or an invalid import), then rerun.",
                displayPath, formatter.Location(file, ex.LineNumber, ex.ColumnNumber)));
            return null;
        }
    }

    private static EvaluatedTargetFramework Capture(Project project, string targetFramework, Context context, string displayPath, List<InventoryBlocker> blockers)
    {
        List<PropertyState> properties = [];
        foreach (string name in RelevantProperties)
        {
            ProjectProperty? property = project.GetProperty(name);
            if (property is null)
            {
                continue;
            }

            properties.Add(CaptureProperty(property, context));
        }

        Dictionary<string, ProjectItem> central = new(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectItem version in project.GetItems("PackageVersion"))
        {
            central[version.EvaluatedInclude] = version;
        }

        List<PackageReferenceState> packages = [];
        foreach (string itemType in new[] { "PackageReference", "GlobalPackageReference" })
        {
            foreach (ProjectItem item in project.GetItems(itemType))
            {
                packages.Add(CapturePackage(project, item, itemType == "GlobalPackageReference", central, context, displayPath, blockers));
            }
        }

        packages = [.. packages
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Definition.Location.File, StringComparer.Ordinal)
            .ThenBy(p => p.Definition.Location.Line)];

        AddConditionBlockers(project, context, displayPath, blockers);

        return new EvaluatedTargetFramework(targetFramework, properties, packages);
    }

    private static PropertyState CaptureProperty(ProjectProperty property, Context context)
    {
        PropertySource source = property.IsGlobalProperty ? PropertySource.Global
            : property.IsEnvironmentProperty ? PropertySource.Environment
            : property.IsReservedProperty ? PropertySource.Reserved
            : PropertySource.File;

        Provenance? definition = null;
        List<Provenance> overridden = [];
        if (source == PropertySource.File && property.Xml is not null)
        {
            definition = context.Provenance(property.Xml);
            for (ProjectProperty? earlier = property.Predecessor; earlier is not null; earlier = earlier.Predecessor)
            {
                if (earlier.Xml is not null)
                {
                    overridden.Insert(0, context.Provenance(earlier.Xml));
                }
            }
        }

        return new PropertyState(property.Name, property.EvaluatedValue, source, definition, overridden);
    }

    private static PackageReferenceState CapturePackage(
        Project project,
        ProjectItem item,
        bool isGlobal,
        Dictionary<string, ProjectItem> central,
        Context context,
        string displayPath,
        List<InventoryBlocker> blockers)
    {
        Provenance definition = context.Provenance(item.Xml);
        List<Provenance> modifiers = [];
        try
        {
            bool foundInclude = false;
            foreach (ProvenanceResult result in project.GetItemProvenance(item))
            {
                Provenance provenance = context.Provenance(result.ItemElement);
                if (result.Operation == Operation.Include && !foundInclude)
                {
                    foundInclude = true;
                    definition = provenance;
                }
                else if (result.Operation is Operation.Update or Operation.Remove)
                {
                    modifiers.Add(provenance);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Provenance is best effort; the item's own element is already a usable location.
        }

        string name = item.EvaluatedInclude;
        string? version;
        PackageVersionSource versionSource;
        Provenance? versionDefinition;

        string inline = item.GetMetadataValue("Version");
        string versionOverride = item.GetMetadataValue("VersionOverride");
        if (inline.Length > 0)
        {
            (version, versionSource, versionDefinition) = (inline, PackageVersionSource.Inline, definition);
        }
        else if (versionOverride.Length > 0)
        {
            (version, versionSource, versionDefinition) = (versionOverride, PackageVersionSource.VersionOverride, definition);
        }
        else if (central.TryGetValue(name, out ProjectItem? centralItem) && centralItem.GetMetadataValue("Version") is { Length: > 0 } centralVersion)
        {
            (version, versionSource, versionDefinition) = (centralVersion, PackageVersionSource.Central, context.Provenance(centralItem.Xml));
        }
        else
        {
            (version, versionSource, versionDefinition) = (null, PackageVersionSource.None, null);
            if (definition.IsRepositoryFile)
            {
                blockers.Add(new InventoryBlocker(
                    BlockerCodes.UnresolvedPackageVersion, BlockerSeverity.Warning,
                    $"PackageReference '{name}' has no Version, VersionOverride or central PackageVersion.",
                    "Add a Version, or a PackageVersion entry in the nearest Directory.Packages.props.",
                    displayPath, definition.Location));
            }
        }

        return new PackageReferenceState(name, version, versionSource, isGlobal, definition, modifiers, versionDefinition);
    }

    private List<ImportRecord> CaptureImports(Project project, Context context)
    {
        List<ImportRecord> imports = [];
        foreach (ResolvedImport import in project.Imports)
        {
            string file = import.ImportedProject.FullPath;
            if (!context.IsRepositoryFile(file) || import.ImportingElement is null)
            {
                continue;
            }

            ProjectImportElement element = import.ImportingElement;
            bool isImplicit = !string.IsNullOrEmpty(element.Sdk) || !context.IsRepositoryFile(element.ContainingProject.FullPath);
            ImportRecord record = new(
                formatter.Format(file),
                KindOf(file),
                isImplicit,
                IsRepositoryFile: true,
                context.LocationOf(element),
                Context.JoinConditions(element));
            if (!imports.Contains(record))
            {
                imports.Add(record);
            }
        }

        return [.. imports
            .OrderBy(i => i.File, StringComparer.Ordinal)
            .ThenBy(i => i.ImportedBy.File, StringComparer.Ordinal)
            .ThenBy(i => i.ImportedBy.Line)];
    }

    private void AddImportBlockers(ImportLogger logger, Context context, string displayPath, List<InventoryBlocker> blockers)
    {
        foreach (SkippedImport skipped in logger.Skipped)
        {
            string importingFile = skipped.ImportingFile.Length == 0 ? context.ProjectPath : skipped.ImportingFile;
            if (!context.IsRepositoryFile(importingFile))
            {
                continue;
            }

            SourceLocation location = formatter.Location(importingFile, skipped.Line, skipped.Column);
            if (skipped.ImportedFile is null || !File.Exists(skipped.ImportedFile))
            {
                string target = skipped.ImportedFile is null ? skipped.UnexpandedProject : formatter.Format(skipped.ImportedFile);
                blockers.Add(new InventoryBlocker(
                    BlockerCodes.MissingImport, BlockerSeverity.Error,
                    $"Import '{target}' at {location} does not resolve to an existing file, so the evaluated state is incomplete.",
                    "Create the file, correct the import path, or guard the import with Condition=\"Exists('...')\" if it is intentionally optional.",
                    displayPath, location));
            }
            else
            {
                blockers.Add(new InventoryBlocker(
                    BlockerCodes.EvaluationFailed, BlockerSeverity.Error,
                    $"Import '{formatter.Format(skipped.ImportedFile)}' at {location} is empty or not a valid MSBuild file.",
                    "Fix the imported file, then rerun.",
                    displayPath, location));
            }
        }
    }

    /// <summary>
    /// Conditions on <c>$(IsTestProject)</c> inside early-evaluated <c>.props</c> files are unreliable: they run
    /// before the project body and before package-contributed props can set the property.
    /// </summary>
    private void AddEarlyIsTestProjectBlockers(Project project, Context context, string displayPath, List<InventoryBlocker> blockers)
    {
        foreach (ResolvedImport import in project.Imports)
        {
            string file = import.ImportedProject.FullPath;
            if (!context.IsRepositoryFile(file)
                || !file.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                || IsImportedFromProjectBody(import, context))
            {
                continue;
            }

            foreach (ProjectElement element in import.ImportedProject.AllChildren)
            {
                if (element.Condition.Length > 0 && IsTestProjectReference().IsMatch(element.Condition))
                {
                    blockers.Add(new InventoryBlocker(
                        BlockerCodes.IsTestProjectEarlyCondition, BlockerSeverity.Warning,
                        $"'{formatter.Format(file)}' is evaluated before the project body but conditions on $(IsTestProject), which is not yet reliable there.",
                        "Move this logic to Directory.Build.targets, or detect test projects from package references instead of IsTestProject.",
                        displayPath, context.LocationOf(element)));
                }
            }
        }
    }

    private static bool IsImportedFromProjectBody(ResolvedImport import, Context context) =>
        import.ImportingElement is { } element
        && string.IsNullOrEmpty(element.Sdk)
        && Context.PathsEqual(element.ContainingProject.FullPath, context.ProjectPath);

    /// <summary>
    /// Flags conditions on elements that matter for migration (relevant properties, package/project references, imports)
    /// that compare an unset or environment-provided property with a literal. Unevaluated elements are scanned too,
    /// because a false condition hides the very element whose state would change the outcome.
    /// </summary>
    private static void AddConditionBlockers(Project project, Context context, string displayPath, List<InventoryBlocker> blockers)
    {
        IEnumerable<ProjectRootElement> roots = [project.Xml, .. project.Imports.Select(i => i.ImportedProject)];
        foreach (ProjectRootElement root in roots.DistinctBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            if (!context.IsRepositoryFile(root.FullPath))
            {
                continue;
            }

            foreach (ProjectElement element in root.AllChildren.Where(IsRelevantElement))
            {
                SourceLocation location = context.LocationOf(element);
                foreach (string condition in Context.Conditions(element))
                {
                    AddConditionBlockers(project, condition, location, displayPath, blockers);
                }
            }
        }
    }

    private static void AddConditionBlockers(Project project, string condition, SourceLocation location, string displayPath, List<InventoryBlocker> blockers)
    {
        foreach (Match match in LiteralComparison().Matches(condition))
        {
            foreach (string side in new[] { match.Groups["l"].Value, match.Groups["r"].Value })
            {
                Match property = PropertyReference().Match(side);
                if (!property.Success)
                {
                    continue;
                }

                string name = property.Groups["n"].Value;
                if (string.Equals(name, "IsTestProject", StringComparison.OrdinalIgnoreCase)
                    || WellKnownEnvironmentProperties.Contains(name))
                {
                    continue;
                }

                ProjectProperty? value = project.GetProperty(name);
                if (value is null)
                {
                    blockers.Add(new InventoryBlocker(
                        BlockerCodes.ConditionDependsOnUnsetProperty, BlockerSeverity.Warning,
                        $"Condition '{condition}' at {location} depends on '{name}', which is not set, so the guarded element is treated as absent/false.",
                        $"Set '{name}' (for example in Directory.Build.props) or pass it as a global property if the guarded state matters.",
                        displayPath, location));
                }
                else if (value.IsEnvironmentProperty)
                {
                    blockers.Add(new InventoryBlocker(
                        BlockerCodes.ConditionDependsOnEnvironment, BlockerSeverity.Warning,
                        $"Condition '{condition}' at {location} depends on the environment variable '{name}', so the result can differ between machines and CI.",
                        $"Define '{name}' explicitly in MSBuild if the guarded state matters.",
                        displayPath, location));
                }
            }
        }
    }

    private static bool IsRelevantElement(ProjectElement element) => element switch
    {
        ProjectPropertyElement property => RelevantProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase),
        ProjectItemElement item => item.ItemType is "PackageReference" or "PackageVersion" or "GlobalPackageReference" or "ProjectReference",
        ProjectImportElement => true,
        _ => false,
    };

    private static string[] SplitFrameworks(string value) =>
        [.. value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.Ordinal)];

    private static ImportKind KindOf(string file) => Path.GetFileName(file).ToLowerInvariant() switch
    {
        "directory.build.props" => ImportKind.DirectoryBuildProps,
        "directory.build.targets" => ImportKind.DirectoryBuildTargets,
        "directory.packages.props" => ImportKind.DirectoryPackagesProps,
        _ => ImportKind.Other,
    };

    private static List<InventoryBlocker> Sorted(List<InventoryBlocker> blockers) =>
        [.. blockers
            .DistinctBy(b => (b.Code, b.Location, b.Message))
            .OrderBy(b => b.Code, StringComparer.Ordinal)
            .ThenBy(b => b.Location?.File, StringComparer.Ordinal)
            .ThenBy(b => b.Location?.Line)
            .ThenBy(b => b.Message, StringComparer.Ordinal)];

    [GeneratedRegex(@"\$\(\s*IsTestProject\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex IsTestProjectReference();

    [GeneratedRegex(@"'(?<l>[^']*)'\s*(?:==|!=)\s*'(?<r>[^']*)'")]
    private static partial Regex LiteralComparison();

    [GeneratedRegex(@"^\$\((?<n>[A-Za-z_][A-Za-z0-9_]*)\)$")]
    private static partial Regex PropertyReference();

    /// <summary>Per-project helpers for turning MSBuild elements into <see cref="Provenance"/>.</summary>
    private sealed class Context
    {
        private readonly Dictionary<string, ResolvedImport> _importsByFile = new(StringComparer.OrdinalIgnoreCase);
        private readonly PathFormatter _formatter;
        private readonly string[] _externalRoots;

        public Context(Project outer, string projectPath, PathFormatter formatter)
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
                !Context.PathsEqual(current, ProjectPath) && _importsByFile.TryGetValue(current, out ResolvedImport import) && seen.Add(current);)
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

    private sealed record SkippedImport(string? ImportedFile, string UnexpandedProject, string ImportingFile, int Line, int Column);

    /// <summary>Collects imports MSBuild skipped because the file was missing, empty or invalid.</summary>
    private sealed class ImportLogger : ILogger
    {
        public List<SkippedImport> Skipped { get; } = [];

        public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;

        public string? Parameters { get; set; }

        public void Initialize(IEventSource eventSource) => eventSource.MessageRaised += OnMessage;

        public void Shutdown()
        {
        }

        private void OnMessage(object sender, BuildMessageEventArgs args)
        {
            // A skipped import because of a false condition carries no imported file; missing/empty/invalid ones do.
            if (args is ProjectImportedEventArgs { ImportIgnored: true, ImportedProjectFile: { Length: > 0 } imported } import)
            {
                Skipped.Add(new SkippedImport(imported, import.UnexpandedProject ?? imported, import.ProjectFile ?? string.Empty, import.LineNumber, import.ColumnNumber));
            }
        }
    }
}
