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
                evaluated.Path, name, ProjectClassification.Unknown, false, [],
                [.. evaluated.TargetFrameworks.Select(tf => ToState(tf, ProjectClassification.Unknown, false, []))],
                evaluated.ProjectReferences, evaluated.Imports, Sorted(blockers));
        }

        List<TargetFrameworkState> states = [];
        foreach (EvaluatedTargetFramework framework in evaluated.TargetFrameworks)
        {
            (ProjectClassification classification, bool hasLibraryDependency, List<string> evidence) =
                ClassifyTargetFramework(framework, evaluated.Path, blockers);
            states.Add(ToState(framework, classification, hasLibraryDependency, evidence));
        }

        ProjectClassification[] distinct = [.. states.Select(s => s.Classification).Distinct()];
        ProjectClassification overall = distinct.Length == 1 ? distinct[0] : ProjectClassification.Mixed;
        if (overall == ProjectClassification.Mixed)
        {
            string summary = string.Join(", ", states.Select(s => $"{s.TargetFramework}: {s.Classification}"));
            blockers.Add(new InventoryBlocker(
                BlockerCodes.TargetFrameworkClassificationDiffers, BlockerSeverity.Error,
                $"Target frameworks classify differently ({summary}).",
                "Decide per target framework whether this is a test application, or make the test packages and IsTestProject consistent across target frameworks.",
                evaluated.Path, null));
        }

        string[] frameworks =
        [
            .. evaluated.TargetFrameworks
                .SelectMany(f => f.Packages)
                .Select(p => TestPackageCatalog.TryGet(p.Name, out TestPackageRole role, out string framework) && role != TestPackageRole.Runner ? framework : null)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal),
        ];

        return new ProjectInventory(
            evaluated.Path, name, overall, states.Any(s => s.HasTestLibraryDependency), frameworks,
            states, evaluated.ProjectReferences, evaluated.Imports, Sorted(blockers));
    }

    private static (ProjectClassification Classification, bool HasLibraryDependency, List<string> Evidence) ClassifyTargetFramework(
        EvaluatedTargetFramework framework,
        string project,
        List<InventoryBlocker> blockers)
    {
        List<string> evidence = [];

        PackageReferenceState[] testPackages = [.. framework.Packages.Where(p => TestPackageCatalog.TryGet(p.Name, out _, out _))];
        foreach (PackageReferenceState package in testPackages)
        {
            evidence.Add($"package {package.Name} {package.Version ?? "(no version)"} at {package.Definition.Location}");
        }

        bool hasLibraryDependency = testPackages.Any(p =>
            TestPackageCatalog.TryGet(p.Name, out TestPackageRole role, out _) && role != TestPackageRole.Runner);

        PropertyState? isTestProject = framework.Properties.FirstOrDefault(p => p.Name == "IsTestProject");
        if (isTestProject is not null && bool.TryParse(isTestProject.Value, out bool explicitValue))
        {
            string where = isTestProject.Definition is null ? $"{isTestProject.Source}" : isTestProject.Definition.Location.ToString();
            evidence.Insert(0, $"IsTestProject={isTestProject.Value} at {where}");
            return (explicitValue ? ProjectClassification.TestApplication : ProjectClassification.Production, hasLibraryDependency, evidence);
        }

        // Only evidence declared in the project file itself can establish a test application: a package added to
        // every project through a shared props file says nothing about which projects are tests.
        PackageReferenceState[] local = [.. testPackages.Where(p => p.Definition.IsFromProjectFile)];
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
            blockers.Add(new InventoryBlocker(
                BlockerCodes.TestEvidenceOnlyFromImports, BlockerSeverity.Warning,
                $"Test framework and runner packages are only complete because of packages declared outside '{project}', so it is not treated as a test application.",
                "Set IsTestProject=true in the test project, or declare its test framework and runner packages in the project file.",
                project, null));
        }

        return (ProjectClassification.Production, hasLibraryDependency, evidence);
    }

    private static bool Has(PackageReferenceState package, bool framework) =>
        TestPackageCatalog.TryGet(package.Name, out TestPackageRole role, out _)
        && (role == TestPackageRole.FrameworkAndRunner || role == (framework ? TestPackageRole.Framework : TestPackageRole.Runner));

    private static TargetFrameworkState ToState(
        EvaluatedTargetFramework framework,
        ProjectClassification classification,
        bool hasLibraryDependency,
        IReadOnlyList<string> evidence) =>
        new(framework.TargetFramework, classification, hasLibraryDependency, evidence, framework.Properties, framework.Packages);

    private static List<InventoryBlocker> Sorted(List<InventoryBlocker> blockers) =>
        [.. blockers
            .DistinctBy(b => (b.Code, b.Location, b.Message))
            .OrderBy(b => b.Code, StringComparer.Ordinal)
            .ThenBy(b => b.Location?.File, StringComparer.Ordinal)
            .ThenBy(b => b.Location?.Line)
            .ThenBy(b => b.Message, StringComparer.Ordinal)];
}
