namespace VsTestToMtp.Tests.Migration;

using System.Text.Json;

using VsTestToMtp.Inventory;
using VsTestToMtp.Migration;
using VsTestToMtp.Tests.Fixtures;

/// <summary>
/// The rules against the committed VSTest fixtures, evaluated with MSBuild: MSTest, NUnit and xUnit v3 pass (with the in-family package
/// changes they need), xUnit v2 is blocked, and nothing is written.
/// </summary>
// MSBuild evaluation blocks threads; running many evaluations in parallel starves the thread pool and is far slower than serial.
[NotInParallel("MSBuildEvaluation")]
public class PreflightFixtureTests
{
    private static readonly MigrationRuleSet Rules = MigrationRuleSet.Current;

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Preflight_SaysWhichRulesItUsed_AndLeavesTheProductionProjectAlone(FixtureInfo fixture)
    {
        PreflightResult result = Preflight(fixture);

        await Assert.That((result.RuleSet, result.RuleSetVersion)).IsEqualTo((Rules.Id, Rules.Version));
        await Assert.That(result.Projects.Select(p => p.Path)).IsEquivalentTo([FixtureInventory.ProductionProject, FixtureInventory.TestProject]);

        ProjectPreflight production = Project(result, FixtureInventory.ProductionProject);
        await Assert.That(production.Readiness).IsEqualTo(MigrationReadiness.NotATestApplication);
        await Assert.That(production.PackageChanges).IsEmpty();
        await Assert.That(production.Diagnostics).IsEmpty();
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Preflight_PassesFrameworksWithMtpV2Support_AndBlocksXUnitV2(FixtureInfo fixture)
    {
        PreflightResult result = Preflight(fixture);
        ProjectPreflight tests = Project(result, FixtureInventory.TestProject);
        TestFramework detected = FixtureInventory.Of(fixture).Projects.Single(p => p.Path == FixtureInventory.TestProject).Frameworks.Single();

        if (detected == TestFramework.XUnitV2)
        {
            await Assert.That(tests.Readiness).IsEqualTo(MigrationReadiness.Blocked);
            await Assert.That(Errors(tests)).IsEquivalentTo([PreflightCode.XUnitV2NotSupported]);
            await Assert.That(FixtureInventory.LineAt(fixture, tests.Diagnostics.Single(d => d.Code == PreflightCode.XUnitV2NotSupported).Location!))
                .Contains("Include=\"xunit\"");
            await Assert.That(tests.PackageChanges).IsEmpty();
            await Assert.That(result.CanMigrate).IsFalse();
        }
        else
        {
            // The framework the rules matched is the one the inventory detected: it is kept, never swapped.
            await Assert.That(tests.Readiness).IsEqualTo(MigrationReadiness.Eligible);
            await Assert.That(tests.Framework).IsEqualTo(detected);
            await Assert.That(Errors(tests)).IsEmpty();
            await Assert.That(result.CanMigrate).IsTrue();
        }
    }

    // The fixtures' starting points: MSTest 4 is already on MTP v2, the NUnit template's adapter (5.x) is not, and the xUnit v3 template
    // turns MTP off.
    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Preflight_PlansOnlyTheInFamilyPackageChangesTheFixtureNeeds(FixtureInfo fixture)
    {
        TargetFrameworkState inventory = FixtureInventory.Of(fixture).Projects.Single(p => p.Path == FixtureInventory.TestProject).TargetFrameworks.Single();
        string VersionOf(string package) => inventory.Packages.Single(p => p.Name == package).Version!;

        (PackageChangeKind, string, string, string, string)[] expected = fixture.Framework switch
        {
            "nunit" => [(PackageChangeKind.Upgrade, "NUnit3TestAdapter", VersionOf("NUnit3TestAdapter"), "NUnit3TestAdapter", Rules.FindPackage("NUnit3TestAdapter")!.UpgradeTo!.ToString())],

            // Still xUnit v3, at the same version: only the MTP variant changes.
            "xunit-v3" => [(PackageChangeKind.Replace, "xunit.v3.mtp-off", VersionOf("xunit.v3.mtp-off"), "xunit.v3.mtp-v2", VersionOf("xunit.v3.mtp-off"))],
            _ => [],
        };

        ProjectPreflight tests = Project(Preflight(fixture), FixtureInventory.TestProject);
        await Assert.That(tests.PackageChanges.Select(c => (c.Kind, c.Package, c.FromVersion, c.ToPackage, c.ToVersion))).IsEquivalentTo(expected);
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task PackageChanges_PointAtTheReferenceAndAtWhereTheVersionIsDeclared(FixtureInfo fixture)
    {
        foreach (PackageChange change in Project(Preflight(fixture), FixtureInventory.TestProject).PackageChanges)
        {
            await Assert.That(change.Reference.File).IsEqualTo(FixtureInventory.TestProject);
            await Assert.That(FixtureInventory.LineAt(fixture, change.Reference)).Contains($"Include=\"{change.Package}\"");

            await Assert.That(change.VersionLocation!.File).IsEqualTo(fixture.UsesCentralPackageManagement ? "Directory.Packages.props" : FixtureInventory.TestProject);
            string versionLine = FixtureInventory.LineAt(fixture, change.VersionLocation);
            await Assert.That(versionLine).Contains($"Include=\"{change.Package}\"");
            await Assert.That(versionLine).Contains($"Version=\"{change.FromVersion}\"");
        }
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task CoverletCollector_IsReportedWhereItIsReferenced_WithoutBlocking(FixtureInfo fixture)
    {
        SourceLocation[] collectors = [.. FixtureInventory.Of(fixture).Projects
            .Single(p => p.Path == FixtureInventory.TestProject).TargetFrameworks.Single().Packages
            .Where(p => p.Name == "coverlet.collector")
            .Select(p => p.Definition.Location)];

        PreflightDiagnostic[] warnings = [.. Project(Preflight(fixture), FixtureInventory.TestProject).Diagnostics.Where(d => d.Code == PreflightCode.VsTestOnlyPackage)];

        await Assert.That(warnings.Select(w => w.Location!)).IsEquivalentTo(collectors);
        await Assert.That(warnings.All(w => w.Severity == BlockerSeverity.Warning)).IsTrue();
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Preflight_DoesNotMutateTheFixtureAndIsDeterministic(FixtureInfo fixture)
    {
        string before = FixtureWorkspace.ComputeHash(fixture.RootPath);

        string first = JsonSerializer.Serialize(CompatibilityPreflight.Assess(FixtureInventory.Build(fixture)));
        string second = JsonSerializer.Serialize(CompatibilityPreflight.Assess(FixtureInventory.Build(fixture)));

        await Assert.That(FixtureWorkspace.ComputeHash(fixture.RootPath)).IsEqualTo(before);
        await Assert.That(second).IsEqualTo(first);
    }

    private static PreflightResult Preflight(FixtureInfo fixture) => CompatibilityPreflight.Assess(FixtureInventory.Of(fixture));

    private static ProjectPreflight Project(PreflightResult result, string path) => result.Projects.Single(p => p.Path == path);

    private static IEnumerable<PreflightCode> Errors(ProjectPreflight project) =>
        project.Diagnostics.Where(d => d.Severity == BlockerSeverity.Error).Select(d => d.Code);
}
