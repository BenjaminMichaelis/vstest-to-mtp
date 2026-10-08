using Microsoft.Build.Construction;
using Microsoft.Build.Definition;
using Microsoft.Build.Evaluation;
using Microsoft.Build.Evaluation.Context;
using Microsoft.Build.Exceptions;

namespace VsTestToMtp.Inventory;

/// <summary>
/// Evaluates a project with MSBuild (no restore, no build) once per target framework and extracts the
/// properties, package references, project references and imports the inventory reports, with provenance.
/// Must only be used after <see cref="MsBuildEnvironment.TryRegister"/> succeeded.
/// </summary>
internal sealed class ProjectEvaluator(PathFormatter formatter, IReadOnlySet<string> selectedProjects) : IDisposable
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
    private readonly EvaluationContext _msBuildContext =
        EvaluationContext.Create(EvaluationContext.SharingPolicy.Shared);

    private readonly ImportLogger _logger = new();

    // One collection per inventory run, as Roslyn's MSBuildWorkspace and slngen do: EvaluationContext does not cache parsed XML,
    // so a collection per project re-parsed every SDK and Directory.*.props file for each project. Global properties are passed on
    // each load (ProjectOptions), not on the collection. Projects are unloaded after each evaluation; disposing does not unload them.
    // https://github.com/dotnet/roslyn/blob/33c9ed52c54827abaff4d4a2fb1f45efd7e5ac99/src/Workspaces/MSBuild/BuildHost/Build/ProjectBuildManager.cs#L258
    // https://github.com/dotnet/msbuild/blob/74878b50aab1c07a7cdcaa15f28115768388cdcc/src/Build/Evaluation/Context/EvaluationContext.cs#L54-L71
    private ProjectCollection Collection => field ??= new(globalProperties: null, [_logger], ToolsetDefinitionLocations.Default);

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

        // Projects are evaluated one at a time (InventoryBuilder holds a lock), so everything the shared logger records from here on
        // belongs to this project, including imports skipped by a load that then failed.
        int firstSkipped = _logger.Skipped.Count;
        try
        {
            return Evaluate(projectPath, displayPath, blockers, () => _logger.Skipped[firstSkipped..]);
        }
        finally
        {
            Collection.UnloadAllProjects();
        }
    }

    public void Dispose()
    {
        Collection.UnloadAllProjects();
        Collection.Dispose();
    }

    private EvaluatedProject Evaluate(string projectPath, string displayPath, List<InventoryBlocker> blockers, Func<IReadOnlyList<SkippedImport>> skippedImports)
    {
        ProjectCollection collection = Collection;
        Project? outer = Load(collection, projectPath, null, displayPath, blockers);
        if (outer is null)
        {
            return new EvaluatedProject(displayPath, [], [], BlockerOrdering.Normalize(blockers));
        }

        // Every evaluation gets its own resolver: an import conditioned on $(TargetFramework) only exists in the inner build.
        ProvenanceResolver outerResolver = new(outer, projectPath, formatter);
        List<(Project Project, ProvenanceResolver Resolver)> evaluated = [(outer, outerResolver)];

        // Same rule as the SDK: a project only dispatches to inner builds when TargetFrameworks is set and TargetFramework is empty.
        // Like Roslyn, NuGet and `dotnet new`, each inner build is then a re-evaluation with TargetFramework as a global property.
        // https://github.com/dotnet/sdk/blob/78c57f0692e746e84becc60c287634326d6a96db/src/Tasks/Microsoft.NET.Build.Tasks/sdk/Sdk.targets#L16-L18
        // https://github.com/dotnet/roslyn/blob/33c9ed52c54827abaff4d4a2fb1f45efd7e5ac99/src/Workspaces/MSBuild/BuildHost/Build/ProjectBuildManager.cs#L266-L305
        string single = outer.GetPropertyValue("TargetFramework").Trim();
        string[] targetFrameworks = single.Length > 0 ? [single] : SplitFrameworks(outer.GetPropertyValue("TargetFrameworks"));

        List<EvaluatedTargetFramework> frameworks = [];
        if (targetFrameworks.Length == 0)
        {
            blockers.Add(new InventoryBlocker(
                BlockerCodes.NoTargetFramework, BlockerSeverity.Error,
                $"'{displayPath}' does not evaluate to a TargetFramework or TargetFrameworks value.",
                "Set TargetFramework (or TargetFrameworks) in the project, Directory.Build.props or an SDK-style import, then rerun.",
                displayPath, formatter.Location(projectPath)));
            frameworks.Add(Capture(outer, string.Empty, outerResolver, displayPath, blockers));
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

                ProvenanceResolver resolver = outerIsThisFramework ? outerResolver : new ProvenanceResolver(project, projectPath, formatter);
                if (!outerIsThisFramework)
                {
                    evaluated.Add((project, resolver));
                }

                frameworks.Add(Capture(project, targetFramework, resolver, displayPath, blockers));
            }
        }

        _blockerDetector.AddImportBlockers(skippedImports(), outerResolver, displayPath, blockers);
        foreach ((Project project, ProvenanceResolver resolver) in evaluated)
        {
            _blockerDetector.AddEarlyIsTestProjectBlockers(project, resolver, displayPath, blockers);
        }

        return new EvaluatedProject(displayPath, frameworks, CaptureImports(evaluated), BlockerOrdering.Normalize(blockers));
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
            // A versioned Sdk="Name/1.2.3" (for example MSTest.Sdk) is resolved by MSBuild's NuGet SDK resolver, which downloads a missing
            // package into the NuGet global packages folder (never the repository). Like Roslyn, slngen and `dotnet new`, we leave it on;
            // MSBUILDDISABLENUGETSDKRESOLVER=1 is its only off switch.
            // https://github.com/NuGet/NuGet.Client/blob/b337f5b80d3363a61f773f2e1c3757526a474610/src/NuGet.Core/Microsoft.Build.NuGetSdkResolver/NuGetSdkResolver.cs#L56-L60
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

    private EvaluatedTargetFramework Capture(Project project, string targetFramework, ProvenanceResolver resolver, string displayPath, List<InventoryBlocker> blockers)
    {
        List<PropertyState> properties = [];
        foreach (string name in RelevantProperties)
        {
            ProjectProperty? property = project.GetProperty(name);
            if (property is null)
            {
                continue;
            }

            properties.Add(CaptureProperty(property, name, resolver));
        }

        PackageVersionResolver versions = new(project, resolver, displayPath, blockers);
        List<PackageReferenceState> packages = [];
        foreach (string itemType in new[] { "PackageReference", "GlobalPackageReference" })
        {
            foreach (ProjectItem item in project.GetItems(itemType))
            {
                packages.Add(CapturePackage(project, item, itemType == "GlobalPackageReference", versions, resolver));
            }
        }

        // NuGet turns each GlobalPackageReference into a generated PackageReference from its own targets; the user-owned declaration
        // is the global one, so the generated twin (declared outside the repository) is not reported again.
        packages = [.. packages.Where(p => p.IsGlobal || p.Definition.IsRepositoryFile
            || !packages.Any(g => g.IsGlobal && string.Equals(g.Name, p.Name, StringComparison.OrdinalIgnoreCase)))];

        packages = [.. packages
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Name, StringComparer.Ordinal)
            .ThenBy(p => p.Definition.Location.File, StringComparer.Ordinal)
            .ThenBy(p => p.Definition.Location.Line)];

        ProjectBlockerDetector.AddConditionBlockers(project, resolver, displayPath, blockers);

        return new EvaluatedTargetFramework(targetFramework, properties, packages, CaptureProjectReferences(project, resolver));
    }

    private List<ProjectReferenceState> CaptureProjectReferences(Project project, ProvenanceResolver resolver)
    {
        List<ProjectReferenceState> references = [];
        foreach (ProjectItem item in project.GetItems("ProjectReference"))
        {
            if (item.Xml is null)
            {
                continue;
            }

            string full = Path.GetFullPath(item.GetMetadataValue("FullPath"));
            references.Add(new ProjectReferenceState(formatter.Format(full), selectedProjects.Contains(full), resolver.Provenance(item.Xml)));
        }

        return [.. references
            .OrderBy(r => r.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Path, StringComparer.Ordinal)
            .ThenBy(r => r.Definition.Location.File, StringComparer.Ordinal)
            .ThenBy(r => r.Definition.Location.Line)];
    }

    private static PropertyState CaptureProperty(ProjectProperty property, string name, ProvenanceResolver resolver)
    {
        PropertySource source = property.IsGlobalProperty ? PropertySource.Global
            : property.IsEnvironmentProperty ? PropertySource.Environment
            : property.IsReservedProperty ? PropertySource.Reserved
            : PropertySource.File;

        Provenance? definition = null;
        List<Provenance> overridden = [];
        if (source == PropertySource.File && property.Xml is not null)
        {
            definition = resolver.Provenance(property.Xml);
            for (ProjectProperty? earlier = property.Predecessor; earlier is not null; earlier = earlier.Predecessor)
            {
                if (earlier.Xml is not null)
                {
                    overridden.Insert(0, resolver.Provenance(earlier.Xml));
                }
            }
        }

        // MSBuild property names are case-insensitive: report the canonical name, not the spelling the file used.
        return new PropertyState(name, property.EvaluatedValue, source, definition, overridden);
    }

    private static PackageReferenceState CapturePackage(Project project, ProjectItem item, bool isGlobal, PackageVersionResolver versions, ProvenanceResolver resolver)
    {
        Provenance definition = resolver.Provenance(item.Xml);
        List<Provenance> modifiers = [];
        try
        {
            // GetItemProvenance is documented as not handling Update/Remove yet but returns them; InventoryProvenanceTests pins that.
            bool foundInclude = false;
            foreach (ProvenanceResult result in project.GetItemProvenance(item))
            {
                Provenance provenance = resolver.Provenance(result.ItemElement);
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

        (string? version, PackageVersionSource versionSource, Provenance? versionDefinition) = versions.Resolve(item, isGlobal, definition);
        return new PackageReferenceState(item.EvaluatedInclude, version, versionSource, isGlobal, definition, modifiers, versionDefinition);
    }

    private List<ImportRecord> CaptureImports(IEnumerable<(Project Project, ProvenanceResolver Resolver)> evaluated)
    {
        List<ImportRecord> imports = [];
        foreach ((Project project, ProvenanceResolver resolver) in evaluated)
        {
            foreach (ResolvedImport import in project.Imports)
            {
                string file = import.ImportedProject.FullPath;
                if (!resolver.IsRepositoryFile(file) || import.ImportingElement is null)
                {
                    continue;
                }

                ProjectImportElement element = import.ImportingElement;
                bool isImplicit = !string.IsNullOrEmpty(element.Sdk) || !resolver.IsRepositoryFile(element.ContainingProject.FullPath);
                ImportRecord record = new(
                    formatter.Format(file),
                    KindOf(file),
                    isImplicit,
                    IsRepositoryFile: true,
                    resolver.LocationOf(element),
                    ProvenanceResolver.JoinConditions(element));
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
}
