namespace VsTestToMtp.Tests.Migration;

using System.Collections.Concurrent;
using System.Text.Json;

using VsTestToMtp.Inventory;
using VsTestToMtp.Migration;
using VsTestToMtp.Tests.Fixtures;

/// <summary>
/// The rule matrix against the committed VSTest fixtures, evaluated with MSBuild: MSTest, NUnit and xUnit v3 pass (with the in-family
/// package changes they need), xUnit v2 is blocked.
/// </summary>
// MSBuild evaluation blocks threads; running many evaluations in parallel starves the thread pool and is far slower than serial.
[NotInParallel("MSBuildEvaluation")]
public class PreflightFixtureTests
{
    private const string TestProjectPath = "tests/Calculator.Tests/Calculator.Tests.csproj";
    private const string ProductionProjectPath = "src/Calculator/Calculator.csproj";

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Preflight_RecordsTheRuleSetAndLeavesTheProductionProjectAlone(FixtureInfo fixture)
    {
        PreflightResult result = Preflight(fixture);

        await Assert.That(result.RuleSet).IsEqualTo(MigrationRuleSet.Current.Id);
        await Assert.That(result.RuleSetVersion).IsEqualTo(MigrationRuleSet.Current.Version);
        await Assert.That(result.Projects.Select(p => p.Path)).IsEquivalentTo([ProductionProjectPath, TestProjectPath]);

        ProjectPreflight production = result.Projects.Single(p => p.Path == ProductionProjectPath);
        await Assert.That(production.Readiness).IsEqualTo(MigrationReadiness.NotATestApplication);
        await Assert.That(production.PackageChanges).IsEmpty();
        await Assert.That(production.Diagnostics).IsEmpty();
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Preflight_PassesEveryFrameworkWithMtpV2Support_AndBlocksXUnitV2(FixtureInfo fixture)
    {
        PreflightResult result = Preflight(fixture);
        ProjectPreflight tests = result.Projects.Single(p => p.Path == TestProjectPath);

        if (fixture.Framework == "xunit-v2")
        {
            await Assert.That(tests.Readiness).IsEqualTo(MigrationReadiness.Blocked);
            await Assert.That(tests.Framework).IsNull();
            await Assert.That(tests.PackageChanges).IsEmpty();
            await Assert.That(tests.Diagnostics.Where(d => d.Severity == BlockerSeverity.Error).Select(d => d.Code))
                .IsEquivalentTo([PreflightCode.XUnitV2NotSupported]);
            PreflightDiagnostic blocker = tests.Diagnostics.Single(d => d.Code == PreflightCode.XUnitV2NotSupported);
            await Assert.That(LineAt(fixture, blocker.Location!)).Contains("Include=\"xunit\"");
            await Assert.That(result.CanMigrate).IsFalse();
            return;
        }

        TestFramework expected = fixture.Framework switch
        {
            "mstest" => TestFramework.MSTest,
            "nunit" => TestFramework.NUnit,
            "xunit-v3" => TestFramework.XUnitV3,
            _ => throw new InvalidOperationException($"Unknown framework '{fixture.Framework}'."),
        };
        await Assert.That(tests.Readiness).IsEqualTo(MigrationReadiness.Eligible);
        await Assert.That(tests.Framework).IsEqualTo(expected);
        await Assert.That(tests.UsesMSTestSdk).IsFalse();
        await Assert.That(tests.Diagnostics.Any(d => d.Severity == BlockerSeverity.Error)).IsFalse();
        await Assert.That(result.CanMigrate).IsTrue();
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Preflight_PlansOnlyTheInFamilyPackageChangesEachFrameworkNeeds(FixtureInfo fixture)
    {
        ProjectPreflight tests = Preflight(fixture).Projects.Single(p => p.Path == TestProjectPath);

        (PackageChangeKind Kind, string Package, string From, string ToPackage, string To)[] expected = fixture.Framework switch
        {
            // MSTest 4.0.2 already runs on MTP v2; only EnableMSTestRunner is missing, which is not a package change.
            "mstest" => [],
            "nunit" => [(PackageChangeKind.Upgrade, "NUnit3TestAdapter", "5.0.0", "NUnit3TestAdapter", "6.3.1")],

            // Still xUnit v3 at the same version: only the MTP variant changes.
            "xunit-v3" => [(PackageChangeKind.Replace, "xunit.v3.mtp-off", "4.0.1", "xunit.v3.mtp-v2", "4.0.1")],
            _ => [],
        };

        await Assert.That(tests.PackageChanges.Select(c => (c.Kind, c.Package, c.FromVersion, c.ToPackage, c.ToVersion))).IsEquivalentTo(expected);
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task PackageChanges_PointAtTheReferenceAndAtWhereTheVersionIsDeclared(FixtureInfo fixture)
    {
        foreach (PackageChange change in Preflight(fixture).Projects.Single(p => p.Path == TestProjectPath).PackageChanges)
        {
            await Assert.That(change.Reference.File).IsEqualTo(TestProjectPath);
            await Assert.That(LineAt(fixture, change.Reference)).Contains($"Include=\"{change.Package}\"");

            string versionFile = fixture.UsesCentralPackageManagement ? "Directory.Packages.props" : TestProjectPath;
            await Assert.That(change.VersionLocation!.File).IsEqualTo(versionFile);
            string versionLine = LineAt(fixture, change.VersionLocation);
            await Assert.That(versionLine).Contains($"Include=\"{change.Package}\"");
            await Assert.That(versionLine).Contains($"Version=\"{change.FromVersion}\"");
        }
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task VsTestOnlyCoverageCollector_IsReportedWithoutBlocking(FixtureInfo fixture)
    {
        PreflightDiagnostic[] warnings = [.. Preflight(fixture).Projects.Single(p => p.Path == TestProjectPath).Diagnostics
            .Where(d => d.Code == PreflightCode.VsTestOnlyPackage)];

        // The NUnit and xUnit v2 templates reference coverlet.collector; MSTest and xUnit v3 do not.
        bool hasCollector = fixture.Framework is "nunit" or "xunit-v2";
        await Assert.That(warnings.Length).IsEqualTo(hasCollector ? 1 : 0);
        if (hasCollector)
        {
            await Assert.That(warnings[0].Severity).IsEqualTo(BlockerSeverity.Warning);
            await Assert.That(LineAt(fixture, warnings[0].Location!)).Contains("Include=\"coverlet.collector\"");
        }
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Preflight_DoesNotMutateTheFixtureAndIsDeterministic(FixtureInfo fixture)
    {
        string before = FixtureWorkspace.ComputeHash(fixture.RootPath);

        string first = JsonSerializer.Serialize(CompatibilityPreflight.Assess(Build(fixture)));
        string second = JsonSerializer.Serialize(CompatibilityPreflight.Assess(Build(fixture)));

        await Assert.That(FixtureWorkspace.ComputeHash(fixture.RootPath)).IsEqualTo(before);
        await Assert.That(second).IsEqualTo(first);
    }

    private static string LineAt(FixtureInfo fixture, SourceLocation location) =>
        File.ReadAllLines(Path.Combine(fixture.RootPath, location.File.Replace('/', Path.DirectorySeparatorChar)))[location.Line - 1];

    // MSBuild evaluation is slow, so evaluate each fixture once and share the (immutable) result between tests.
    private static readonly ConcurrentDictionary<string, Lazy<PreflightResult>> Cache = new();

    private static PreflightResult Preflight(FixtureInfo fixture) =>
        Cache.GetOrAdd(fixture.Name, _ => new Lazy<PreflightResult>(() => CompatibilityPreflight.Assess(Build(fixture)))).Value;

    private static InventoryResult Build(FixtureInfo fixture) =>
        InventoryBuilder.Build(Path.Combine(fixture.RootPath, "Fixture.slnx"), new InventoryOptions(fixture.RootPath));
}
