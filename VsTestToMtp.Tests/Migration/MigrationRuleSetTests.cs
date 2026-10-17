namespace VsTestToMtp.Tests.Migration;

using VsTestToMtp.Inventory;
using VsTestToMtp.Migration;

/// <summary>Pins the rule matrix, so a change to a threshold or upgrade target is a deliberate, reviewed change with a new rule set version.</summary>
public class MigrationRuleSetTests
{
    private static readonly MigrationRuleSet Rules = MigrationRuleSet.Current;

    [Test]
    public async Task RuleSet_IsIdentifiedAndVersioned()
    {
        await Assert.That(Rules.Id).IsEqualTo("mtp-v2-dotnet10");
        await Assert.That(Rules.Version).IsEqualTo(1);
        await Assert.That(Rules.MinimumSdkVersion).IsEqualTo(PackageVersion.Parse("10.0.100"));
    }

    [Test]
    public async Task RuntimeTargets_AreNet8AndNetFramework462()
    {
        await Assert.That(Rules.RuntimeTargets.Select(t => (t.Identifier, t.MinimumVersion.ToString(), t.Moniker)))
            .IsEquivalentTo([(".NETCoreApp", "8.0", "net8.0"), (".NETFramework", "4.6.2", "net462")]);
    }

    [Test]
    public async Task XUnitV3_NeedsNetFramework472()
    {
        IReadOnlyList<RuntimeTarget> targets = Rules.RuntimeTargetsFor(TestFramework.XUnitV3);

        await Assert.That(targets.Single(t => t.Identifier == ".NETFramework").Moniker).IsEqualTo("net472");
        await Assert.That(targets.Single(t => t.Identifier == ".NETCoreApp").Moniker).IsEqualTo("net8.0");
        await Assert.That(Rules.RuntimeTargetsFor(TestFramework.MSTest).Single(t => t.Identifier == ".NETFramework").Moniker).IsEqualTo("net462");
    }

    [Test]
    [Arguments("MSTest", "4.0.0", "4.0.2")]
    [Arguments("MSTest.TestAdapter", "4.0.0", "4.0.2")]
    [Arguments("MSTest.TestFramework", "4.0.0", "4.0.2")]
    [Arguments("MSTest.Sdk", "4.0.0", "4.0.2")]
    [Arguments("NUnit3TestAdapter", "6.0.0", "6.3.1")]
    [Arguments("TUnit", "0.71.0", "1.45.0")]
    [Arguments("TUnit.Engine", "0.71.0", "1.45.0")]
    [Arguments("Microsoft.Testing.Platform", "2.0.0", "2.5.1")]
    [Arguments("Microsoft.Testing.Extensions.TrxReport", "2.0.0", "2.5.1")]
    [Arguments("Microsoft.Testing.Extensions.CodeCoverage", "18.1.0", "18.8.0")]
    public async Task FrameworkAndExtensionPackages_HaveAnMtpV2ThresholdAndATestedUpgrade(string package, string minimum, string upgradeTo)
    {
        PackageRule? rule = Rules.FindPackage(package.ToLowerInvariant());

        await Assert.That(rule).IsNotNull();
        await Assert.That(rule!.MinimumVersion!.ToString()).IsEqualTo(minimum);
        await Assert.That(rule.UpgradeTo!.ToString()).IsEqualTo(upgradeTo);
    }

    [Test]
    public async Task EveryUpgradeTarget_SatisfiesItsOwnThreshold_AndStaysOnARelease()
    {
        foreach (PackageRule rule in Rules.Packages.Where(r => r.UpgradeTo is not null))
        {
            await Assert.That(rule.UpgradeTo! >= rule.MinimumVersion!).IsTrue().Because(rule.Package);
            await Assert.That(rule.UpgradeTo!.IsPrerelease).IsFalse().Because(rule.Package);
        }
    }

    [Test]
    public async Task NUnitBelow3_IsBlockedNotUpgraded()
    {
        PackageRule nunit = Rules.FindPackage("NUnit")!;

        await Assert.That(nunit.MinimumVersion!.ToString()).IsEqualTo("3.0.0");
        await Assert.That(nunit.UpgradeTo).IsNull();
    }

    [Test]
    public async Task XUnitV2Packages_HaveNoRule_SoTheyAreNeverUpgradedToV3()
    {
        await Assert.That(Rules.FindPackage("xunit")).IsNull();
        await Assert.That(Rules.FindPackage("xunit.core")).IsNull();
        await Assert.That(Rules.Packages.Any(r => r.Framework == TestFramework.XUnitV2)).IsFalse();
    }

    [Test]
    public async Task XUnitV3Variants_DefaultToMtpV2From4_AndHaveAnMtpV2VariantFrom3_2()
    {
        await Assert.That(Rules.XUnitV3Packages).IsEquivalentTo(["xunit.v3", "xunit.v3.core"]);
        await Assert.That(Rules.XUnitV3DefaultsToMtpV2.ToString()).IsEqualTo("4.0.0");
        await Assert.That(Rules.XUnitV3MtpV2Variant.ToString()).IsEqualTo("3.2.0");
        await Assert.That(Rules.XUnitV3UpgradeTo >= Rules.XUnitV3MtpV2Variant).IsTrue();
    }

    [Test]
    public async Task VsTestOnlyPackages_AreFlaggedWithoutAThreshold()
    {
        PackageRule[] vstestOnly = [.. Rules.Packages.Where(r => r.Kind == PackageRuleKind.VsTestOnly)];

        await Assert.That(vstestOnly.Select(r => r.Package)).IsEquivalentTo(["coverlet.collector", "coverlet.msbuild"]);
        await Assert.That(vstestOnly.All(r => r.MinimumVersion is null && r.UpgradeTo is null)).IsTrue();
    }

    [Test]
    public async Task EveryRule_SaysWhyItsThresholdIsWhatItIs()
    {
        await Assert.That(Rules.Packages.All(r => r.Evidence.Length > 0)).IsTrue();
    }
}
