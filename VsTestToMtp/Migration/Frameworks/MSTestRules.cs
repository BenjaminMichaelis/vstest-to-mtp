using NuGet.Versioning;

using VsTestToMtp.Inventory;

namespace VsTestToMtp.Migration;

/// <summary>
/// MSTest: MSTest.Sdk turns MTP on unless <c>UseVSTest=true</c> and brings its packages at its own version; package-based MSTest turns it
/// on with <c>EnableMSTestRunner=true</c>. Either way the MSTest packages must be 4.0 or later.
/// </summary>
internal static class MSTestRules
{
    public static FrameworkVerdict Assess(ProjectAssessment project)
    {
        if (project.UsesMSTestSdk)
        {
            bool sdkUpgradePlanned = AssessSdk(project, out bool sdkOnMtpV2);
            bool packagesBelow = project.ApplyFamilyRules(TestFramework.MSTest, sdkUpgradePlanned);
            return new FrameworkVerdict(
                IsOnMtpV2: sdkOnMtpV2 && !project.AnyTrue(MsBuildPropertyNames.UseVSTest) && !packagesBelow,
                SdkUpgradePlanned: sdkUpgradePlanned);
        }

        bool hasAdapter = project.PackagesNamed("MSTest", "MSTest.TestAdapter").Any();
        if (!hasAdapter)
        {
            project.Error(PreflightCode.MissingRunnerPackage,
                $"'{project.Project.Path}' references MSTest without MSTest.TestAdapter (or the MSTest package), so nothing runs its tests.",
                "Reference MSTest (or MSTest.TestAdapter), then rerun.");
        }

        bool below = project.ApplyFamilyRules(TestFramework.MSTest, sdkUpgradePlanned: false);
        return new FrameworkVerdict(
            IsOnMtpV2: hasAdapter && project.AllTrue(MsBuildPropertyNames.EnableMSTestRunner) && !below,
            SdkUpgradePlanned: false);
    }

    /// <summary>Plans an MSTest.Sdk upgrade when it is on MTP v1; returns whether one was planned.</summary>
    private static bool AssessSdk(ProjectAssessment project, out bool onMtpV2)
    {
        MigrationRuleSet rules = project.Rules;
        PackageRule rule = rules.MSTestSdk;
        SdkReferenceState? sdk = project.Project.Sdks.FirstOrDefault(s => string.Equals(s.Name, rule.Package, StringComparison.OrdinalIgnoreCase));
        if (sdk is null || !NuGetVersion.TryParse(sdk.ResolvedVersion ?? sdk.DeclaredVersion, out NuGetVersion? resolved))
        {
            // Without the reference the version cannot be edited, but MSTest.Sdk itself says which MTP it brings.
            string? platform = project.Property(MsBuildPropertyNames.MicrosoftTestingPlatformVersion)?.Value;
            onMtpV2 = NuGetVersion.TryParse(platform, out NuGetVersion? mtp) && mtp.Major >= 2;
            if (!onMtpV2)
            {
                project.Error(PreflightCode.PackageNotInRepository,
                    $"'{project.Project.Path}' uses MSTest.Sdk, but its version could not be read from a repository file ({MsBuildPropertyNames.MicrosoftTestingPlatformVersion} is '{platform}').",
                    "Reference MSTest.Sdk with a version in the project (Sdk=\"MSTest.Sdk/x.y.z\") or in global.json msbuild-sdks, then rerun.");
            }

            return false;
        }

        onMtpV2 = resolved >= rule.MinimumVersion;
        if (onMtpV2)
        {
            return false;
        }

        // A reference without a version takes it from global.json msbuild-sdks, so that is where it is edited.
        SourceLocation versionLocation = sdk.DeclaredVersion is null && project.Inventory.GlobalJson is { } globalJson
            ? SourceLocation.OfFile(globalJson.Path)
            : sdk.Location;
        project.Plan(new PackageChange(
            PackageChangeKind.SdkUpgrade,
            sdk.Name,
            resolved.ToNormalizedString(),
            sdk.Name,
            rule.UpgradeTo!.ToNormalizedString(),
            $"{sdk.Name} {resolved} runs on Microsoft.Testing.Platform v1; {rule.Evidence}",
            sdk.Location,
            versionLocation));
        return true;
    }
}
