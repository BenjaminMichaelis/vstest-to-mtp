using VsTestToMtp.Inventory;

namespace VsTestToMtp.Migration;

/// <summary>TUnit only runs on MTP; from 0.71 on MTP v2. Its packages are versioned together.</summary>
internal static class TUnitRules
{
    public static FrameworkVerdict Assess(ProjectAssessment project) =>
        new(IsOnMtpV2: !project.ApplyFamilyRules(TestFramework.TUnit, sdkUpgradePlanned: false), SdkUpgradePlanned: false);
}
