using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Exceptions;

namespace VsTestToMtp.Inventory;

/// <summary>
/// Evaluates a project with MSBuild (no restore, no build) once per target framework and extracts the
/// properties, package references, project references and imports the inventory reports, with provenance.
/// Must only be used after <see cref="MsBuildEnvironment.TryRegister"/> succeeded.
/// </summary>
internal sealed class ProjectEvaluator(PathFormatter formatter, IReadOnlySet<string> selectedProjects)
{
    internal static readonly string[] RelevantProperties =
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

    private readonly ProjectBlockerDetector _blockerDetector = new(formatter);

    // Documented for evaluating several projects: one context extends the lifetime of evaluation caches (file system, SDK
    // resolution). It is created per inventory run and thrown away afterwards, as the docs require when the environment may change.
    private readonly Microsoft.Build.Evaluation.Context.EvaluationContext _msBuildContext =
        Microsoft.Build.Evaluation.Context.EvaluationContext.Create(Microsoft.Build.Evaluation.Context.EvaluationContext.SharingPolicy.Shared);

    private readonly Dictionary<string, string> _globalProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        // Ignore obj/*.nuget.g.props/targets so the result does not depend on whether (or when) the project was restored.
        // This also matters for correctness: Microsoft.NET.Test.Sdk's props set IsTestProject=true for any project that
        // references it, which would make a restored production project look like a test application.
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
            return new EvaluatedProject(displayPath, [], [], Sorted(blockers));
        }

        // Every evaluation gets its own context: an import conditioned on $(TargetFramework) only exists in the inner build.
        EvaluationContext outerContext = new(outer, projectPath, formatter);
        List<(Project Project, EvaluationContext Context)> evaluated = [(outer, outerContext)];

        string[] targetFrameworks = SplitFrameworks(outer.GetPropertyValue("TargetFrameworks"));
        if (targetFrameworks.Length == 0)
        {
            string single = outer.GetPropertyValue("TargetFramework").Trim();
            targetFrameworks = single.Length == 0 ? [] : [single];
        }

        List<EvaluatedTargetFramework> frameworks = [];
        if (targetFrameworks.Length == 0)
        {
            blockers.Add(new InventoryBlocker(
                BlockerCodes.NoTargetFramework, BlockerSeverity.Error,
                $"'{displayPath}' does not evaluate to a TargetFramework or TargetFrameworks value.",
                "Set TargetFramework (or TargetFrameworks) in the project, Directory.Build.props or an SDK-style import, then rerun.",
                displayPath, formatter.Location(projectPath)));
            frameworks.Add(Capture(outer, string.Empty, outerContext, displayPath, blockers));
        }
        else
        {
            foreach (string targetFramework in targetFrameworks.Order(StringComparer.Ordinal))
            {
                bool outerIsThisFramework = targetFramework == outer.GetPropertyValue("TargetFramework").Trim()
                    && outer.GetProperty("TargetFramework")?.IsGlobalProperty != true;

                Project? project = outerIsThisFramework ? outer : Load(collection, projectPath, targetFramework, displayPath, blockers);
                if (project is null)
                {
                    continue;
                }

                EvaluationContext context = outerIsThisFramework ? outerContext : new EvaluationContext(project, projectPath, formatter);
                if (!outerIsThisFramework)
                {
                    evaluated.Add((project, context));
                }

                frameworks.Add(Capture(project, targetFramework, context, displayPath, blockers));
            }
        }

        _blockerDetector.AddImportBlockers(logger, outerContext, displayPath, blockers);
        foreach ((Project project, EvaluationContext context) in evaluated)
        {
            _blockerDetector.AddEarlyIsTestProjectBlockers(project, context, displayPath, blockers);
        }

        return new EvaluatedProject(displayPath, frameworks, CaptureImports(evaluated), Sorted(blockers));
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
                EvaluationContext = _msBuildContext,

                // Missing/invalid/empty imports are tolerated so we can keep evaluating and report them with locations
                // (see ImportLogger). Anything else that makes the evaluation incomplete must fail loudly instead:
                // FailOnUnresolvedSdk keeps IgnoreMissingImports from also swallowing an unresolvable Sdk=, and
                // RejectCircularImports stops a circular import from being accepted as a complete evaluation.
                LoadSettings = ProjectLoadSettings.RecordEvaluatedItemElements
                    | ProjectLoadSettings.IgnoreMissingImports
                    | ProjectLoadSettings.IgnoreInvalidImports
                    | ProjectLoadSettings.IgnoreEmptyImports
                    | ProjectLoadSettings.FailOnUnresolvedSdk
                    | ProjectLoadSettings.RejectCircularImports,
            });
        }
        catch (InvalidProjectFileException ex)
        {
            // MSBuild reports unreadable, locked and missing project files this way too, not as IOException.
            string file = string.IsNullOrEmpty(ex.ProjectFile) ? projectPath : ex.ProjectFile;
            blockers.Add(new InventoryBlocker(
                BlockerCodes.EvaluationFailed, BlockerSeverity.Error,
                $"MSBuild could not evaluate '{displayPath}'{(targetFramework is null ? string.Empty : $" for {targetFramework}")}: {ex.ErrorCode} {ex.BaseMessage}",
                "Fix the reported problem (missing SDK, invalid XML or an invalid import), then rerun.",
                displayPath, formatter.Location(file, ex.LineNumber, ex.ColumnNumber)));
            return null;
        }
    }

    private EvaluatedTargetFramework Capture(Project project, string targetFramework, EvaluationContext context, string displayPath, List<InventoryBlocker> blockers)
    {
        List<PropertyState> properties = [];
        foreach (string name in RelevantProperties)
        {
            ProjectProperty? property = project.GetProperty(name);
            if (property is null)
            {
                continue;
            }

            properties.Add(CaptureProperty(property, name, context));
        }

        // A PackageVersion only supplies versions when Central Package Management is on; NuGet ignores it otherwise.
        // VersionOverride additionally needs overrides enabled, which is the default.
        bool centralManagement = MsBuildBoolean.TryParse(project.GetPropertyValue("ManagePackageVersionsCentrally"), out bool cpm) && cpm;
        bool overrideAllowed = centralManagement
            && !(MsBuildBoolean.TryParse(project.GetPropertyValue("CentralPackageVersionOverrideEnabled"), out bool enabled) && !enabled);
        Dictionary<string, ProjectItem> central = new(StringComparer.OrdinalIgnoreCase);
        if (centralManagement)
        {
            foreach (IGrouping<string, ProjectItem> group in project.GetItems("PackageVersion").GroupBy(v => v.EvaluatedInclude, StringComparer.OrdinalIgnoreCase))
            {
                if (group.Count() == 1)
                {
                    central[group.Key] = group.Single();
                    continue;
                }

                // NuGet reports NU1506 here because restore is inconsistent; do not pick a winner.
                Provenance[] definitions = [.. group.Select(v => context.Provenance(v.Xml))];
                blockers.Add(new InventoryBlocker(
                    BlockerCodes.DuplicatePackageVersion, BlockerSeverity.Warning,
                    $"PackageVersion '{group.Key}' is declared {definitions.Length} times ({string.Join(", ", definitions.Select(d => d.Location))}), so its central version is ambiguous.",
                    "Keep one PackageVersion per package (use Update to change a version declared elsewhere).",
                    displayPath, definitions[^1].Location));
            }
        }

        List<PackageReferenceState> packages = [];
        foreach (string itemType in new[] { "PackageReference", "GlobalPackageReference" })
        {
            foreach (ProjectItem item in project.GetItems(itemType))
            {
                packages.Add(CapturePackage(project, item, itemType == "GlobalPackageReference", central, overrideAllowed, context, displayPath, blockers));
            }
        }

        packages = [.. packages
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Definition.Location.File, StringComparer.Ordinal)
            .ThenBy(p => p.Definition.Location.Line)];

        ProjectBlockerDetector.AddConditionBlockers(project, context, displayPath, blockers);

        return new EvaluatedTargetFramework(targetFramework, properties, packages, CaptureProjectReferences(project, context));
    }

    private List<ProjectReferenceState> CaptureProjectReferences(Project project, EvaluationContext context)
    {
        List<ProjectReferenceState> references = [];
        foreach (ProjectItem item in project.GetItems("ProjectReference"))
        {
            if (item.Xml is null)
            {
                continue;
            }

            string full = Path.GetFullPath(item.GetMetadataValue("FullPath"));
            references.Add(new ProjectReferenceState(formatter.Format(full), selectedProjects.Contains(full), context.Provenance(item.Xml)));
        }

        return [.. references
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Definition.Location.File, StringComparer.Ordinal)
            .ThenBy(r => r.Definition.Location.Line)];
    }

    private static PropertyState CaptureProperty(ProjectProperty property, string name, EvaluationContext context)
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

        // MSBuild property names are case-insensitive: report the canonical name, not the spelling the file used.
        return new PropertyState(name, property.EvaluatedValue, source, definition, overridden);
    }

    private static PackageReferenceState CapturePackage(
        Project project,
        ProjectItem item,
        bool isGlobal,
        Dictionary<string, ProjectItem> central,
        bool overrideAllowed,
        EvaluationContext context,
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
            (version, versionSource, versionDefinition) = (inline, PackageVersionSource.Inline, MetadataProvenance(item, "Version", context, definition));
        }
        else if (versionOverride.Length > 0 && overrideAllowed)
        {
            (version, versionSource, versionDefinition) = (versionOverride, PackageVersionSource.VersionOverride, MetadataProvenance(item, "VersionOverride", context, definition));
        }
        else if (central.TryGetValue(name, out ProjectItem? centralItem) && centralItem.GetMetadataValue("Version") is { Length: > 0 } centralVersion)
        {
            (version, versionSource, versionDefinition) = (centralVersion, PackageVersionSource.Central, MetadataProvenance(centralItem, "Version", context, context.Provenance(centralItem.Xml)));
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

        if (versionOverride.Length > 0 && !overrideAllowed && definition.IsRepositoryFile)
        {
            blockers.Add(new InventoryBlocker(
                BlockerCodes.IneffectiveVersionOverride, BlockerSeverity.Warning,
                $"PackageReference '{name}' sets VersionOverride, which is ignored unless Central Package Management is on and CentralPackageVersionOverrideEnabled is not false.",
                "Enable Central Package Management (and version overrides), or use Version instead of VersionOverride.",
                displayPath, definition.Location));
        }

        return new PackageReferenceState(name, version, versionSource, isGlobal, definition, modifiers, versionDefinition);
    }

    /// <summary>
    /// Where the winning value of an item's metadata was declared: the Include, a later Update, or an item definition.
    /// Falls back to <paramref name="fallback"/> when MSBuild does not expose the declaring element.
    /// </summary>
    private static Provenance MetadataProvenance(ProjectItem item, string metadataName, EvaluationContext context, Provenance fallback) =>
        item.GetMetadata(metadataName)?.Xml is { } xml ? context.Provenance(xml) : fallback;

    private List<ImportRecord> CaptureImports(IEnumerable<(Project Project, EvaluationContext Context)> evaluated)
    {
        List<ImportRecord> imports = [];
        foreach ((Project project, EvaluationContext context) in evaluated)
        {
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
                    EvaluationContext.JoinConditions(element));
                if (!imports.Contains(record))
                {
                    imports.Add(record);
                }
            }
        }

        return [.. imports
            .OrderBy(i => i.File, StringComparer.Ordinal)
            .ThenBy(i => i.ImportedBy.File, StringComparer.Ordinal)
            .ThenBy(i => i.ImportedBy.Line)];
    }

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
}
