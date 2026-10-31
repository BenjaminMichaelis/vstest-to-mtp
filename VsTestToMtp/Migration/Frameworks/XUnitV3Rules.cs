using NuGet.Versioning;

using VsTestToMtp.Inventory;

namespace VsTestToMtp.Migration;

/// <summary>
/// xUnit v3: every variant except <c>.mtp-off</c> runs on MTP, and MTP v2 needs the <c>.mtp-v2</c> variant, or the default one from 4.0.0.
/// A project not there moves to the <c>.mtp-v2</c> variant of the same package, at the same version when that variant exists for it.
/// </summary>
internal static class XUnitV3Rules
{
    public static FrameworkVerdict Assess(ProjectAssessment project)
    {
        MigrationRuleSet rules = project.Rules;
        bool notOnMtpV2 = false;
        NuGetVersion? raisedTo = null;
        foreach (PackageReferenceState package in project.Packages)
        {
            if (!XUnitV3Packages.TrySplit(package.Name, out string? baseName, out string? variant))
            {
                continue;
            }

            if (!project.TryResolve(package, out NuGetVersion? version))
            {
                notOnMtpV2 = true;
                continue;
            }

            if (variant is XUnitV3Packages.MtpV2 || (variant is "" && version >= rules.XUnitV3DefaultsToMtpV2))
            {
                continue;
            }

            notOnMtpV2 = true;
            NuGetVersion target = version >= rules.XUnitV3MtpV2Variant ? version : rules.XUnitV3UpgradeTo;
            if (target > version && (raisedTo is null || target > raisedTo))
            {
                raisedTo = target;
            }

            string current = variant switch
            {
                "" => $"defaults to Microsoft.Testing.Platform v1 before {rules.XUnitV3DefaultsToMtpV2}",
                XUnitV3Packages.MtpOff => "turns Microsoft.Testing.Platform off",
                _ => "selects Microsoft.Testing.Platform v1",
            };
            string upgrade = target == version ? "same version" : $"{rules.XUnitV3MtpV2Variant} is the first version with the .mtp-v2 variant";
            project.Plan(package, PackageChangeKind.Replace, baseName + XUnitV3Packages.MtpV2, target,
                $"{package.Name} {package.Version} {current}; {baseName}{XUnitV3Packages.MtpV2} is the same xUnit v3 on Microsoft.Testing.Platform v2 ({upgrade}).");
        }

        if (raisedTo is not null)
        {
            RaiseCompanions(project, raisedTo);
        }

        return new FrameworkVerdict(IsOnMtpV2: !notOnMtpV2, SdkUpgradePlanned: false);
    }

    // xUnit v3's packages depend on each other at exactly their own version (xunit.v3.mtp-v2 3.2.2 needs xunit.v3.assert [3.2.2]), so a
    // companion the project references itself (xunit.v3.assert, xunit.v3.extensibility.core, ...) has to move with the version, or restore
    // fails with a downgrade.
    private static void RaiseCompanions(ProjectAssessment project, NuGetVersion version)
    {
        foreach (PackageReferenceState companion in project.Packages)
        {
            if (companion.Name.StartsWith("xunit.v3.", StringComparison.OrdinalIgnoreCase)
                && !XUnitV3Packages.TrySplit(companion.Name, out _, out _)
                && project.TryResolve(companion, out NuGetVersion? current)
                && current < version)
            {
                project.Plan(companion, PackageChangeKind.Upgrade, companion.Name, version,
                    $"{companion.Name} {companion.Version} is versioned with xUnit v3, whose packages require each other at exactly the same version, {version}.");
            }
        }
    }
}
