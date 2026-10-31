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
            project.Error(PreflightCode.MissingRunnerPackage,
                $"'{project.Project.Path}' references NUnit without NUnit3TestAdapter, which is what runs NUnit tests on Microsoft.Testing.Platform.",
                "Reference NUnit3TestAdapter (or run the tests some other way and set IsTestProject=false), then rerun.");
        }

        bool below = project.ApplyFamilyRules(TestFramework.NUnit, sdkUpgradePlanned: false);
        return new FrameworkVerdict(
            IsOnMtpV2: hasAdapter && project.AllTrue(MsBuildPropertyNames.EnableNUnitRunner) && !below,
            SdkUpgradePlanned: false);
    }
}
