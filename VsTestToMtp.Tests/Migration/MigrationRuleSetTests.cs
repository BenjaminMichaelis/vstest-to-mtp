namespace VsTestToMtp.Tests.Migration;

using VsTestToMtp.Migration;

/// <summary>
/// Invariants the rule matrix must keep whatever its numbers are. What each threshold means for a project (below it is upgraded, at it
/// nothing changes, xUnit v2 and NUnit 2 are blocked, ...) is tested against evaluated projects in <see cref="PreflightScenarioTests"/>.
/// </summary>
public class MigrationRuleSetTests
{
    private static readonly MigrationRuleSet Rules = MigrationRuleSet.Current;

    [Test]
    public async Task EveryUpgradeTarget_IsAReleaseThatSatisfiesItsOwnThreshold()
    {
        foreach (PackageRule rule in Rules.Packages.Where(r => r.UpgradeTo is not null))
        {
            await Assert.That(rule.UpgradeTo! >= rule.MinimumVersion!).IsTrue().Because(rule.Package);
            await Assert.That(rule.UpgradeTo!.IsPrerelease).IsFalse().Because(rule.Package);
        }

        await Assert.That(Rules.XUnitV3UpgradeTo >= Rules.XUnitV3MtpV2Variant).IsTrue();
        await Assert.That(Rules.XUnitV3UpgradeTo.IsPrerelease).IsFalse();
    }

    // Packages that ship together must be moved together: a mismatched MSTest adapter and framework, or TUnit engine and core, do not
    // load. The platform's extensions are versioned with the platform.
    [Test]
    [Arguments("MSTest", "MSTest.TestAdapter", "MSTest.TestFramework", "MSTest.Analyzers")]
    [Arguments("TUnit", "TUnit.Engine", "TUnit.Core", "TUnit.Assertions")]
    [Arguments("Microsoft.Testing.Platform", "Microsoft.Testing.Platform.MSBuild", "Microsoft.Testing.Extensions.TrxReport", "Microsoft.Testing.Extensions.Retry")]
    public async Task PackagesVersionedTogether_ShareTheirThresholdAndUpgradeTarget(string first, string second, string third, string fourth)
    {
        PackageRule[] family = [.. new[] { first, second, third, fourth }.Select(p => Rules.FindPackage(p)!)];

        await Assert.That(family.Select(r => r.MinimumVersion).Distinct()).HasSingleItem();
        await Assert.That(family.Select(r => r.UpgradeTo).Distinct()).HasSingleItem();
    }

    [Test]
    public async Task EveryRule_SaysWhereItsThresholdComesFrom()
    {
        await Assert.That(Rules.Packages.Where(r => string.IsNullOrWhiteSpace(r.Evidence)).Select(r => r.Package)).IsEmpty();
    }

    [Test]
    public async Task PackageLookup_IsCaseInsensitiveLikeNuGet()
    {
        await Assert.That(Rules.FindPackage("nunit3testadapter")).IsSameReferenceAs(Rules.FindPackage("NUnit3TestAdapter"));
    }
}
