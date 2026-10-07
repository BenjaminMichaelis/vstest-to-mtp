namespace VsTestToMtp.Inventory;

/// <summary>
/// A position in a file. <see cref="File"/> is relative to the inventory root with <c>/</c> separators
/// when the file lives under it, otherwise an absolute path with <c>/</c> separators.
/// </summary>
public sealed record SourceLocation(string File, int Line, int Column)
{
    public override string ToString() => $"{File}({Line},{Column})";
}

/// <summary>
/// One <c>&lt;Import&gt;</c> hop between the evaluated project file and the file that holds an element.
/// </summary>
/// <param name="ImportedBy">Location of the importing element (or the <c>Sdk</c> attribute for implicit SDK imports).</param>
/// <param name="Condition">The import element's own <c>Condition</c>, if any.</param>
public sealed record ImportStep(SourceLocation ImportedBy, string? Condition);

/// <summary>
/// Where an evaluated value or item was declared, so later edits target the right file.
/// </summary>
/// <param name="Location">The declaring element.</param>
/// <param name="Conditions">Conditions guarding the element, outermost first (element and its ancestors).</param>
/// <param name="ImportChain">Imports from the project file down to the declaring file (empty when declared in the project file).</param>
/// <param name="IsFromProjectFile">Whether the element lives in the evaluated project file itself.</param>
/// <param name="IsRepositoryFile">Whether the declaring file is user-owned (not part of the .NET SDK or a NuGet package).</param>
public sealed record Provenance(
    SourceLocation Location,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<ImportStep> ImportChain,
    bool IsFromProjectFile,
    bool IsRepositoryFile);

public enum PropertySource
{
    /// <summary>Defined by an element in a project or imported file.</summary>
    File,

    /// <summary>Provided as a global property by the inventory (or the caller).</summary>
    Global,

    /// <summary>Read from an environment variable.</summary>
    Environment,

    /// <summary>MSBuild reserved or well-known property.</summary>
    Reserved,
}

/// <summary>An evaluated MSBuild property for one target framework.</summary>
/// <param name="Definition">The winning definition; <see langword="null"/> unless <see cref="Source"/> is <see cref="PropertySource.File"/>.</param>
/// <param name="Overridden">Earlier definitions that the winning one replaced, oldest first.</param>
public sealed record PropertyState(
    string Name,
    string Value,
    PropertySource Source,
    Provenance? Definition,
    IReadOnlyList<Provenance> Overridden);

public enum PackageVersionSource
{
    /// <summary>No version could be resolved.</summary>
    None,

    /// <summary><c>Version</c> metadata on the <c>PackageReference</c>.</summary>
    Inline,

    /// <summary><c>VersionOverride</c> metadata on the <c>PackageReference</c> (Central Package Management).</summary>
    VersionOverride,

    /// <summary>A <c>PackageVersion</c> item (Central Package Management).</summary>
    Central,
}

/// <summary>An evaluated <c>PackageReference</c> (or <c>GlobalPackageReference</c>) for one target framework.</summary>
/// <param name="Definition">Where the reference is declared.</param>
/// <param name="Modifiers"><c>Update</c>/<c>Remove</c> operations that also touched the reference.</param>
/// <param name="VersionDefinition">Where the effective version is declared (the reference itself, or the central <c>PackageVersion</c>).</param>
public sealed record PackageReferenceState(
    string Name,
    string? Version,
    PackageVersionSource VersionSource,
    bool IsGlobal,
    Provenance Definition,
    IReadOnlyList<Provenance> Modifiers,
    Provenance? VersionDefinition);

/// <summary>A <c>ProjectReference</c> found in the evaluated project.</summary>
/// <param name="Path">Referenced project path (same formatting as <see cref="SourceLocation.File"/>).</param>
/// <param name="IsInSelection">Whether the referenced project is part of the inventoried selection.</param>
public sealed record ProjectReferenceState(string Path, bool IsInSelection, Provenance Definition);

public enum ImportKind
{
    Other,
    DirectoryBuildProps,
    DirectoryBuildTargets,
    DirectoryPackagesProps,
}

/// <summary>A file imported while evaluating a project.</summary>
/// <param name="IsImplicit">Whether the import comes from the SDK rather than an explicit <c>&lt;Import&gt;</c> in a repository file.</param>
/// <param name="IsRepositoryFile">Whether the imported file is user-owned (not part of the .NET SDK or a NuGet package).</param>
public sealed record ImportRecord(
    string File,
    ImportKind Kind,
    bool IsImplicit,
    bool IsRepositoryFile,
    SourceLocation ImportedBy,
    string? Condition);

public enum ProjectClassification
{
    /// <summary>State could not be evaluated; see the project's blockers.</summary>
    Unknown,

    /// <summary>Not a test application (may still reference a test library).</summary>
    Production,

    /// <summary>A test application.</summary>
    TestApplication,

    /// <summary>Target frameworks disagree.</summary>
    Mixed,
}

/// <summary>The classification for one target framework with the reasons behind it.</summary>
public sealed record TargetFrameworkState(
    string TargetFramework,
    ProjectClassification Classification,
    bool HasTestLibraryDependency,
    IReadOnlyList<string> Evidence,
    IReadOnlyList<PropertyState> Properties,
    IReadOnlyList<PackageReferenceState> Packages)
{
    public PropertyState? GetProperty(string name) =>
        Properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}

public enum BlockerSeverity
{
    /// <summary>Informational; nothing prevents migration.</summary>
    Info,

    /// <summary>The result may be wrong or environment-dependent.</summary>
    Warning,

    /// <summary>State could not be determined; do not guess.</summary>
    Error,
}

/// <summary>An actionable reason the inventory could not (fully) determine something.</summary>
public sealed record InventoryBlocker(
    string Code,
    BlockerSeverity Severity,
    string Message,
    string Remediation,
    string? Project,
    SourceLocation? Location);

/// <summary>Well-known blocker codes.</summary>
public static class BlockerCodes
{
    public const string SelectionNotFound = "SelectionNotFound";
    public const string UnsupportedSelection = "UnsupportedSelection";
    public const string AmbiguousSelection = "AmbiguousSelection";
    public const string MalformedSolution = "MalformedSolution";
    public const string MissingProject = "MissingProject";
    public const string UnsupportedProjectLanguage = "UnsupportedProjectLanguage";
    public const string MsBuildNotFound = "MsBuildNotFound";
    public const string EvaluationFailed = "EvaluationFailed";
    public const string MissingImport = "MissingImport";
    public const string NoTargetFramework = "NoTargetFramework";
    public const string MalformedGlobalJson = "MalformedGlobalJson";
    public const string ConditionDependsOnUnsetProperty = "ConditionDependsOnUnsetProperty";
    public const string ConditionDependsOnEnvironment = "ConditionDependsOnEnvironment";
    public const string IsTestProjectEarlyCondition = "IsTestProjectEarlyCondition";
    public const string UnresolvedPackageVersion = "UnresolvedPackageVersion";
    public const string TestEvidenceOnlyFromImports = "TestEvidenceOnlyFromImports";
    public const string TargetFrameworkClassificationDiffers = "TargetFrameworkClassificationDiffers";
}

/// <summary>A project in the selection with its evaluated, classified state.</summary>
/// <param name="Path">Project file path (same formatting as <see cref="SourceLocation.File"/>).</param>
/// <param name="TargetFrameworks">Evaluated target frameworks, ordinal order.</param>
/// <param name="Frameworks">Test frameworks detected from package evidence (e.g. <c>MSTest</c>, <c>xUnit v3</c>).</param>
public sealed record ProjectInventory(
    string Path,
    string Name,
    ProjectClassification Classification,
    bool HasTestLibraryDependency,
    IReadOnlyList<string> Frameworks,
    IReadOnlyList<TargetFrameworkState> TargetFrameworks,
    IReadOnlyList<ProjectReferenceState> ProjectReferences,
    IReadOnlyList<ImportRecord> Imports,
    IReadOnlyList<InventoryBlocker> Blockers);

/// <summary>The effective <c>global.json</c> for the selection.</summary>
/// <param name="TestRunner"><c>test.runner</c>, e.g. <c>Microsoft.Testing.Platform</c>; <see langword="null"/> means VSTest.</param>
public sealed record GlobalJsonInfo(
    string Path,
    string? SdkVersion,
    string? RollForward,
    string? TestRunner,
    IReadOnlyList<string> MsBuildSdks);

public enum AutomationKind
{
    GitHubWorkflow,
    AzurePipelines,
    PowerShellScript,
    ShellScript,
    BatchScript,
    Makefile,
}

/// <summary>A CI definition or script found under the inventory root.</summary>
public sealed record AutomationFile(string Path, AutomationKind Kind, bool MentionsDotNetTest);

/// <summary>What the caller selected.</summary>
public sealed record SelectionInfo(string Path, string Kind);

/// <summary>The complete, deterministic, read-only inventory.</summary>
/// <param name="Root">Absolute inventory root; relative paths in the result are relative to it.</param>
/// <param name="Projects">Selected projects ordered by path.</param>
/// <param name="Blockers">Every blocker (selection-level and per project), ordered.</param>
public sealed record InventoryResult(
    string Root,
    SelectionInfo Selection,
    IReadOnlyList<ProjectInventory> Projects,
    GlobalJsonInfo? GlobalJson,
    IReadOnlyList<AutomationFile> Automation,
    IReadOnlyList<InventoryBlocker> Blockers)
{
    public IEnumerable<ProjectInventory> TestApplications =>
        Projects.Where(p => p.Classification == ProjectClassification.TestApplication);
}

/// <summary>Options for <see cref="InventoryBuilder"/>.</summary>
/// <param name="RootPath">
/// Directory the result's relative paths and the CI/script scan are anchored to.
/// Defaults to the nearest ancestor of the selection containing <c>.git</c>, else the selection's directory.
/// </param>
public sealed record InventoryOptions(string? RootPath = null);
