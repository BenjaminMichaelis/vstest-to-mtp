namespace VsTestToMtp.Inventory;

/// <summary>
/// Classifies evaluated projects. Evaluated <c>IsTestProject</c> and package evidence decide; file names never do.
/// </summary>
internal static class TestProjectClassifier
{
    public static ProjectInventory Classify(EvaluatedProject evaluated, string name)
    {
        List<InventoryBlocker> blockers = [.. evaluated.Blockers];

        if (evaluated.HasErrors || evaluated.TargetFrameworks.Count == 0)
        {
            return new ProjectInventory(
                Path: evaluated.Path,
                Name: name,
                Classification: ProjectClassification.Unknown,
                HasTestLibraryDependency: false,
                Frameworks: [],
                Runners: [],
                TargetFrameworks: [.. evaluated.TargetFrameworks.Select(tf => ToState(tf, ProjectClassification.Unknown, false, []))],
                Imports: evaluated.Imports,
                Sdks: evaluated.Sdks,
                Blockers: BlockerOrdering.Normalize(blockers));
        }

        List<TargetFrameworkState> states = [];
        foreach (EvaluatedTargetFramework framework in evaluated.TargetFrameworks)
        {
            (ProjectClassification classification, bool hasLibraryDependency, List<string> evidence) =
                ClassifyTargetFramework(framework, evaluated, blockers);
            states.Add(ToState(framework, classification, hasLibraryDependency, evidence));
        }

        ProjectClassification[] distinct = [.. states.Select(s => s.Classification).Distinct()];
        ProjectClassification overall = distinct.Contains(ProjectClassification.Unknown) ? ProjectClassification.Unknown
            : distinct.Length == 1 ? distinct[0]
            : ProjectClassification.Mixed;
        if (overall == ProjectClassification.Mixed)
        {
            string summary = string.Join(", ", states.Select(s => $"{s.TargetFramework}: {s.Classification}"));
            blockers.Add(InventoryBlocker.Error(BlockerCode.TargetFrameworkClassificationDiffers,
                $"Target frameworks classify differently ({summary}).",
                "Decide per target framework whether this is a test application, or make the test packages and IsTestProject consistent across target frameworks.",
                evaluated.Path));
        }

        TestPackageInfo[] known =
        [
            .. evaluated.TargetFrameworks
                .SelectMany(f => f.Packages)
                .Select(p => TestPackageCatalog.TryGet(p.Name, out TestPackageInfo? info) ? info : null)
                .OfType<TestPackageInfo>(),
        ];

        // A framework comes from a framework package; a bare runner/adapter package only adapts one, so it does not count.
        TestFramework[] frameworks = [.. known
            .Where(i => i.Role != TestPackageRole.Runner)
            .Select(i => i.Framework)
            .OfType<TestFramework>()
            .Distinct()
            .Order()];
        TestRunnerKind[] runners = [.. known.Select(i => i.Runner).OfType<TestRunnerKind>().Distinct().Order()];

        return new ProjectInventory(
            Path: evaluated.Path,
            Name: name,
            Classification: overall,
            HasTestLibraryDependency: states.Any(s => s.HasTestLibraryDependency),
            Frameworks: frameworks,
            Runners: runners,
            TargetFrameworks: states,
            Imports: evaluated.Imports,
                Sdks: evaluated.Sdks,
            Blockers: BlockerOrdering.Normalize(blockers));
    }

    private static (ProjectClassification Classification, bool HasLibraryDependency, List<string> Evidence) ClassifyTargetFramework(
        EvaluatedTargetFramework framework,
        EvaluatedProject evaluated,
        List<InventoryBlocker> blockers)
    {
        string project = evaluated.Path;
        List<string> evidence = [];

        PackageReferenceState[] testPackages = [.. framework.Packages.Where(p => TestPackageCatalog.TryGet(p.Name, out _))];
        foreach (PackageReferenceState package in testPackages)
        {
            evidence.Add($"package {package.Name} {package.Version ?? "(no version)"} at {package.Definition.Location}");
        }

        bool hasLibraryDependency = testPackages.Any(p =>
            TestPackageCatalog.TryGet(p.Name, out TestPackageInfo? info) && info.Role != TestPackageRole.Runner);

        PropertyState? isTestProject = PropertyState.Find(framework.Properties, "IsTestProject");
        if (isTestProject is { Value.Length: > 0 } && !MsBuildBoolean.TryParse(isTestProject.Value, out _))
        {
            // The explicit property is authoritative, so a value that is not a boolean makes the classification undeterminable.
            blockers.Add(InventoryBlocker.Error(BlockerCode.InvalidIsTestProject,
                $"IsTestProject evaluates to '{isTestProject.Value}' for {framework.TargetFramework}, which is not a boolean.",
                "Set IsTestProject to true or false (check for a property that expands to something else), then rerun.",
                project, isTestProject.Definition?.Location));
            return (ProjectClassification.Unknown, hasLibraryDependency, evidence);
        }

        if (isTestProject is not null && MsBuildBoolean.TryParse(isTestProject.Value, out bool explicitValue))
        {
            string where = isTestProject.Definition is null ? $"{isTestProject.Source}" : isTestProject.Definition.Location.ToString();
            evidence.Insert(0, $"IsTestProject={isTestProject.Value} at {where}");
            return (explicitValue ? ProjectClassification.TestApplication : ProjectClassification.Production, hasLibraryDependency, evidence);
        }

        // Only evidence declared in the project file itself can establish a test application: a package added to
        // every project through a shared props file says nothing about which projects are tests. The project's own SDK
        // (Sdk="MSTest.Sdk/x.y") is the project file's choice too, so what that SDK declares counts as well.
        PackageReferenceState[] local = [.. testPackages.Where(p => p.Definition.IsFromProjectFile || IsFromProjectSdk(p, evaluated))];
        bool localFramework = local.Any(p => Has(p, framework: true));
        bool localRunner = local.Any(p => Has(p, framework: false));
        if (localFramework && localRunner)
        {
            return (ProjectClassification.TestApplication, hasLibraryDependency, evidence);
        }

        bool anyFramework = testPackages.Any(p => Has(p, framework: true));
        bool anyRunner = testPackages.Any(p => Has(p, framework: false));
        if (anyFramework && anyRunner)
        {
            blockers.Add(InventoryBlocker.Warning(BlockerCode.TestEvidenceOnlyFromImports,
                $"Test framework and runner packages are only complete because of packages declared outside '{project}', so it is not treated as a test application.",
                "Set IsTestProject=true in the test project, or declare its test framework and runner packages in the project file.",
                project));
        }

        return (ProjectClassification.Production, hasLibraryDependency, evidence);
    }

    /// <summary>
    /// Whether <paramref name="package"/> is declared by an SDK the project file references itself: in a file outside the repository,
    /// reached from the project file through SDK and package files only. A hop through any other repository file (a
    /// Directory.Build.props that imports an SDK, say) makes it shared. Every repository file a project imports is in its imports.
    /// </summary>
    private static bool IsFromProjectSdk(PackageReferenceState package, EvaluatedProject evaluated) =>
        !package.Definition.IsRepositoryFile
        && package.Definition.ImportChain is [var first, ..]
        && first.ImportedBy.File == evaluated.Path
        && package.Definition.ImportChain.All(step =>
            step.ImportedBy.File == evaluated.Path || !evaluated.Imports.Any(i => i.File == step.ImportedBy.File));

    private static bool Has(PackageReferenceState package, bool framework) =>
        TestPackageCatalog.TryGet(package.Name, out TestPackageInfo? info)
        && (info.Role == TestPackageRole.FrameworkAndRunner || info.Role == (framework ? TestPackageRole.Framework : TestPackageRole.Runner));

    private static TargetFrameworkState ToState(
        EvaluatedTargetFramework framework,
        ProjectClassification classification,
        bool hasLibraryDependency,
        IReadOnlyList<string> evidence) =>
        new(
            TargetFramework: framework.TargetFramework,
            Classification: classification,
            HasTestLibraryDependency: hasLibraryDependency,
            Evidence: evidence,
            Properties: framework.Properties,
            Packages: framework.Packages,
            ProjectReferences: framework.ProjectReferences);
}
