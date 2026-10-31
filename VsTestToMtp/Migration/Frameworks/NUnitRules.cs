using NuGet.Versioning;

using VsTestToMtp.Inventory;

namespace VsTestToMtp.Migration;

/// <summary>
/// NUnit: NUnit3TestAdapter runs NUnit 3 and later on MTP once <c>EnableNUnitRunner=true</c>, from adapter 6.0 on MTP v2. NUnit 2 (and its
/// adapter) has no MTP support, and moving to NUnit 3 is a framework migration.
/// </summary>
internal static class NUnitRules
{
    public static FrameworkVerdict Assess(ProjectAssessment project)
    {
        foreach (PackageReferenceState legacy in project.PackagesNamed("NUnitTestAdapter"))
        {
            project.Error(PreflightCode.UnsupportedTestFramework,
                $"'{project.Project.Path}' uses the NUnit 2 adapter (NUnitTestAdapter {legacy.Version}), which has no Microsoft.Testing.Platform support.",
                "Moving to NUnit 3 and NUnit3TestAdapter is a framework migration this tool does not do; do it first, then rerun.",
                legacy.Definition.Location);
        }

        bool hasAdapter = project.PackagesNamed("NUnit3TestAdapter").Any();
        if (!hasAdapter)
        {
            AddAdapter(project);
        }

        bool below = project.ApplyFamilyRules(TestFramework.NUnit, sdkUpgradePlanned: false);
        return new FrameworkVerdict(
            IsOnMtpV2: hasAdapter && project.AllTrue(MsBuildPropertyNames.EnableNUnitRunner) && !below,
            SdkUpgradePlanned: false);
    }

    // The adapter is part of NUnit's own family, so adding it is no framework migration. But a project that runs its NUnit tests some other
    // way (NUnit.Console) would change how they run, which is the user's call; so is one whose NUnit reference is not in the project file.
    private static void AddAdapter(ProjectAssessment project)
    {
        PackageReferenceState? nunit = project.PackagesNamed("NUnit").FirstOrDefault();
        PackageRule adapter = project.Rules.FindPackage("NUnit3TestAdapter")!;

        // NUnit 2 is reported on its own: the adapter only runs NUnit 3 and later, and getting there is a framework migration.
        if (nunit is null || project.PackagesNamed("NUnit.Console", "NUnit.ConsoleRunner").Any()
            || !project.TryResolve(nunit, out NuGetVersion? nunitVersion) || nunitVersion < project.Rules.FindPackage("NUnit")!.MinimumVersion
            || !project.PlanAddition(adapter.Package, nunit, adapter.UpgradeTo!,
                $"NUnit tests run on Microsoft.Testing.Platform through {adapter.Package}, which the project does not reference. {adapter.Evidence}"))
        {
            project.Error(PreflightCode.MissingRunnerPackage,
                $"'{project.Project.Path}' references NUnit without NUnit3TestAdapter, which is what runs NUnit tests on Microsoft.Testing.Platform, and it is not clear where or whether to add it (another NUnit runner is referenced, or NUnit is not referenced in the project file).",
                "Reference NUnit3TestAdapter in the project (or run the tests some other way and set IsTestProject=false), then rerun.");
        }
    }
}
