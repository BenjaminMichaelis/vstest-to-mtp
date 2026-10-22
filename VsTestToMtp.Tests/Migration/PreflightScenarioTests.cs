namespace VsTestToMtp.Tests.Migration;

using VsTestToMtp.Inventory;
using VsTestToMtp.Migration;
using VsTestToMtp.Tests.Inventory;

/// <summary>
/// What the rules mean for a project, each case evaluated with MSBuild from a small repository so the verdicts come from evaluated
/// properties and package references, not from names. Package versions here are facts about the packages (MSTest 3.11 runs on MTP v1,
/// 4.0.0 on v2); the versions the rules upgrade to are read from <see cref="MigrationRuleSet"/>, so choosing a newer tested version does
/// not touch these tests.
/// </summary>
// MSBuild evaluation blocks threads; running many evaluations in parallel starves the thread pool and is far slower than serial.
[NotInParallel("MSBuildEvaluation")]
public class PreflightScenarioTests
{
    private const string App = "Tests/Tests.csproj";

    // Test applications that run on MTP v2 as they are, apart from enabling the runner.
    private const string MSTest = "MSTest:4.0.2";
    private const string NUnit = "NUnit:4.3.2,NUnit3TestAdapter:6.3.1";
    private const string XUnitV3 = "xunit.v3:4.0.1";
    private const string TUnit = "TUnit:1.45.0";

    private const string XUnitV2 = "Microsoft.NET.Test.Sdk:17.14.1,xunit:2.9.3,xunit.runner.visualstudio:3.1.4";

    private static readonly MigrationRuleSet Rules = MigrationRuleSet.Current;

    [Test]
    [Arguments("MSTest with its runner", "<PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner><OutputType>Exe</OutputType></PropertyGroup>", MSTest, TestFramework.MSTest)]
    [Arguments("NUnit with its runner", "<PropertyGroup><EnableNUnitRunner>true</EnableNUnitRunner><OutputType>Exe</OutputType></PropertyGroup>", NUnit, TestFramework.NUnit)]
    [Arguments("xunit.v3 4.x, MTP v2 by default", "", XUnitV3, TestFramework.XUnitV3)]
    [Arguments("xunit.v3.mtp-v2", "", "xunit.v3.mtp-v2:3.2.2", TestFramework.XUnitV3)]
    [Arguments("TUnit", "", TUnit, TestFramework.TUnit)]
    public async Task ProjectAlreadyOnMtpV2_IsAlreadyMigrated(string scenario, string body, string packages, TestFramework framework)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, body + References(packages));

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.AlreadyMigrated).Because(scenario);
        await Assert.That(project.Framework).IsEqualTo(framework);
        await Assert.That(project.PackageChanges).IsEmpty();
        await Assert.That(project.Diagnostics).IsEmpty();
    }

    // The first version of each package that runs on MTP v2 needs no change: the thresholds are inclusive.
    [Test]
    [Arguments("MSTest:4.0.0")]
    [Arguments("MSTest.TestAdapter:4.0.0,MSTest.TestFramework:4.0.0")]
    [Arguments("NUnit:3.0.0,NUnit3TestAdapter:6.0.0")]
    [Arguments("xunit.v3:4.0.0")]
    [Arguments("xunit.v3.mtp-v2:3.2.0")]
    [Arguments("TUnit:0.71.0")]
    [Arguments(MSTest + ",Microsoft.Testing.Extensions.TrxReport:2.0.0")]
    [Arguments(MSTest + ",Microsoft.Testing.Extensions.CodeCoverage:18.1.0")]
    [Arguments(MSTest + ",coverlet.MTP:8.0.0")]
    public async Task FirstVersionOnMtpV2_NeedsNoPackageChange(string packages)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References(packages));

        ProjectPreflight project = Assess(workspace);

        // Eligible or already migrated, depending on whether the runner still has to be enabled; either way nothing to upgrade.
        await Assert.That(project.Readiness).IsNotEqualTo(MigrationReadiness.Blocked);
        await Assert.That(project.PackageChanges).IsEmpty();
        await Assert.That(project.Diagnostics).IsEmpty();
    }

    // The last version of each package that runs on MTP v1 is upgraded within its own family, to the version the rules tested.
    [Test]
    [Arguments("MSTest:3.11.0", "MSTest")]
    [Arguments("NUnit:4.3.2,NUnit3TestAdapter:5.2.0", "NUnit3TestAdapter")]
    [Arguments("TUnit:0.70.7", "TUnit")]
    [Arguments(MSTest + ",Microsoft.Testing.Extensions.TrxReport:1.9.0", "Microsoft.Testing.Extensions.TrxReport")]
    [Arguments(MSTest + ",Microsoft.Testing.Extensions.CodeCoverage:18.0.6", "Microsoft.Testing.Extensions.CodeCoverage")]
    public async Task LastVersionOnMtpV1_IsUpgradedVisiblyInItsFamily(string packages, string upgraded)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References(packages));
        string from = VersionIn(packages, upgraded);

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Eligible);
        await Assert.That(project.Diagnostics).IsEmpty();
        PackageChange change = project.PackageChanges.Single();
        await Assert.That((change.Kind, change.Package, change.FromVersion, change.ToPackage, change.ToVersion))
            .IsEqualTo((PackageChangeKind.Upgrade, upgraded, from, upgraded, Rules.FindPackage(upgraded)!.UpgradeTo!.ToString()));
        await Assert.That(change.Reason).Contains($"{upgraded} {from}");

        // An inline version is edited where it is written: on the reference itself.
        int line = workspace.LineOf(App, $"Include=\"{upgraded}\"");
        await Assert.That((change.Reference.Line, change.VersionLocation!.Line)).IsEqualTo((line, line));
    }

    [Test]
    public async Task PackagesVersionedTogether_AreUpgradedTogether()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References("MSTest.TestAdapter:3.6.4,MSTest.TestFramework:3.6.4"));

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.PackageChanges.Select(c => c.Package)).IsEquivalentTo(["MSTest.TestAdapter", "MSTest.TestFramework"]);
        await Assert.That(project.PackageChanges.Select(c => c.ToVersion).Distinct()).HasSingleItem();
    }

    // Moving to another variant keeps the test framework (and, from 3.2.0, the version): xUnit v3 stays xUnit v3.
    [Test]
    [Arguments("xunit.v3", "3.2.1", "xunit.v3.mtp-v2", true)]
    [Arguments("xunit.v3.mtp-v1", "3.2.2", "xunit.v3.mtp-v2", true)]
    [Arguments("xunit.v3.mtp-off", "4.0.1", "xunit.v3.mtp-v2", true)]
    [Arguments("xunit.v3.core.mtp-off", "4.0.1", "xunit.v3.core.mtp-v2", true)]
    [Arguments("xunit.v3", "3.1.0", "xunit.v3.mtp-v2", false)]
    public async Task XUnitV3NotOnMtpV2_MovesToTheMtpV2VariantOfTheSameXUnitV3(string package, string version, string variant, bool keepsVersion)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References($"{package}:{version}"));

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Eligible);
        await Assert.That(project.Framework).IsEqualTo(TestFramework.XUnitV3);
        PackageChange change = project.PackageChanges.Single();
        string expectedVersion = keepsVersion ? version : Rules.XUnitV3UpgradeTo.ToString();
        await Assert.That((change.Kind, change.Package, change.FromVersion, change.ToPackage, change.ToVersion))
            .IsEqualTo((PackageChangeKind.Replace, package, version, variant, expectedVersion));
    }

    [Test]
    public async Task UnderCentralPackageManagement_AVersionOverrideIsEditedWhereItIsWritten()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Packages.props", """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="NUnit" Version="4.3.2" />
                    <PackageVersion Include="NUnit3TestAdapter" Version="6.3.1" />
                  </ItemGroup>
                </Project>
                """)
            .WriteProject(App, """
                  <ItemGroup>
                    <PackageReference Include="NUnit" />
                    <PackageReference Include="NUnit3TestAdapter" VersionOverride="5.2.0" />
                  </ItemGroup>
                """);

        PackageChange change = Assess(workspace).PackageChanges.Single();

        // The override in the project wins over the central PackageVersion, so the project's VersionOverride is what to edit.
        int line = workspace.LineOf(App, "VersionOverride");
        await Assert.That((change.Package, change.FromVersion)).IsEqualTo(("NUnit3TestAdapter", "5.2.0"));
        await Assert.That((change.VersionLocation!.File, change.VersionLocation.Line)).IsEqualTo((App, line));
        await Assert.That(change.Reference.Line).IsEqualTo(line);
    }

    [Test]
    public async Task XUnitV2_IsBlocked_AndNeverPlannedOntoV3()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References(XUnitV2));

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        await Assert.That(Codes(project)).IsEquivalentTo([PreflightCode.XUnitV2NotSupported]);
        await Assert.That(project.PackageChanges).IsEmpty();
    }

    [Test]
    public async Task NUnit2_IsBlocked_NotMigratedToNUnit3()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References("NUnit:2.6.4,NUnitTestAdapter:2.3.0"));

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        await Assert.That(project.PackageChanges).IsEmpty();
        await Assert.That(Codes(project)).IsEquivalentTo(
            [PreflightCode.MissingRunnerPackage, PreflightCode.UnsupportedTestFramework, PreflightCode.UnsupportedTestFramework]);
    }

    [Test]
    public async Task MSTestWithoutAnAdapter_IsBlocked()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(App, "<PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>" + References("MSTest.TestFramework:4.0.2"));

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        await Assert.That(Codes(project)).IsEquivalentTo([PreflightCode.MissingRunnerPackage]);
    }

    [Test]
    public async Task SeveralTestFrameworks_AreBlocked()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References($"{MSTest},{NUnit}"));

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        await Assert.That(project.Framework).IsNull();
        await Assert.That(Codes(project)).IsEquivalentTo([PreflightCode.MultipleTestFrameworks]);
    }

    [Test]
    public async Task ProductionProjectWithATestLibraryDependency_IsNotATestApplication()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References("MSTest.TestFramework:3.6.4"));

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.NotATestApplication);
        await Assert.That(project.PackageChanges).IsEmpty();
        await Assert.That(project.Diagnostics).IsEmpty();
    }

    [Test]
    [Arguments("net6.0", MSTest, TestFramework.MSTest)]
    [Arguments("net7.0", MSTest, TestFramework.MSTest)]
    [Arguments("netcoreapp3.1", NUnit, TestFramework.NUnit)]
    [Arguments("net461", MSTest, TestFramework.MSTest)]
    [Arguments("net462", XUnitV3, TestFramework.XUnitV3)]
    [Arguments("netstandard2.0", TUnit, TestFramework.TUnit)]
    public async Task UnsupportedTargetFramework_IsBlocked_NeverRetargeted(string targetFramework, string packages, TestFramework framework)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(App, "<PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>" + References(packages), [targetFramework]);

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Framework).IsEqualTo(framework);
        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        PreflightDiagnostic diagnostic = project.Diagnostics.Single();
        await Assert.That(diagnostic.Code).IsEqualTo(PreflightCode.UnsupportedTargetFramework);
        await Assert.That(diagnostic.Message).Contains(targetFramework);
        await Assert.That(diagnostic.Location!.Line).IsEqualTo(workspace.LineOf(App, "<TargetFramework>"));
    }

    [Test]
    [Arguments("net8.0", MSTest)]
    [Arguments("net10.0", MSTest)]
    [Arguments("net462", MSTest)]
    [Arguments("net48", NUnit)]
    [Arguments("net472", XUnitV3)]
    public async Task SupportedTargetFramework_Passes(string targetFramework, string packages)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References(packages), [targetFramework]);

        await Assert.That(Assess(workspace).Diagnostics).IsEmpty();
    }

    [Test]
    public async Task MultiTargeting_BlocksOnTheUnsupportedTargetFrameworkOnly()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References(MSTest), ["net8.0", "net7.0"]);

        PreflightDiagnostic diagnostic = Assess(workspace).Diagnostics.Single();

        await Assert.That(diagnostic.Code).IsEqualTo(PreflightCode.UnsupportedTargetFramework);
        await Assert.That(diagnostic.Message).Contains("targets net7.0");
        await Assert.That(diagnostic.Location!.Line).IsEqualTo(workspace.LineOf(App, "<TargetFrameworks>"));
    }

    [Test]
    [Arguments("<GenerateProgramFile>false</GenerateProgramFile>", "GenerateProgramFile")]
    [Arguments("<GenerateTestingPlatformEntryPoint>false</GenerateTestingPlatformEntryPoint>", "GenerateTestingPlatformEntryPoint")]
    [Arguments("<StartupObject>Tests.Program</StartupObject>", "StartupObject")]
    public async Task CustomEntryPoint_BlocksAProjectThatStillHasToMove(string property, string name)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, $"<PropertyGroup>{property}</PropertyGroup>" + References(MSTest));

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        PreflightDiagnostic diagnostic = project.Diagnostics.Single();
        await Assert.That(diagnostic.Code).IsEqualTo(PreflightCode.CustomEntryPoint);
        await Assert.That(diagnostic.Message).Contains(name);
        await Assert.That(diagnostic.Location!.Line).IsEqualTo(workspace.LineOf(App, name));
    }

    [Test]
    public async Task CustomEntryPoint_OfAProjectAlreadyOnMtpV2_IsLeftAlone()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App,
            "<PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner><GenerateTestingPlatformEntryPoint>false</GenerateTestingPlatformEntryPoint></PropertyGroup>"
            + References(MSTest));

        await Assert.That(Assess(workspace).Readiness).IsEqualTo(MigrationReadiness.AlreadyMigrated);
    }

    [Test]
    public async Task UnresolvedCondition_OnATestApplication_Blocks()
    {
        // UseTrx is never set, so whether the extension (and its MTP version) is part of the project depends on how the build is invoked.
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References(MSTest) + """
              <ItemGroup Condition="'$(UseTrx)' == 'true'">
                <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="1.9.0" />
              </ItemGroup>
            """);

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        PreflightDiagnostic diagnostic = project.Diagnostics.Single();
        await Assert.That((diagnostic.Code, diagnostic.Severity)).IsEqualTo((PreflightCode.UnresolvedProjectState, BlockerSeverity.Error));
        await Assert.That(diagnostic.Message).Contains(nameof(BlockerCode.ConditionDependsOnUnsetProperty));
        await Assert.That(diagnostic.Location!.Line).IsEqualTo(workspace.LineOf(App, "Microsoft.Testing.Extensions.TrxReport"));
    }

    [Test]
    public async Task TargetFrameworksThatClassifyDifferently_Block()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App,
            "<PropertyGroup Condition=\"'$(TargetFramework)' == 'net8.0'\"><IsTestProject>false</IsTestProject></PropertyGroup>" + References(MSTest),
            ["net10.0", "net8.0"]);

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        await Assert.That(project.Diagnostics.Single().Message).Contains(nameof(BlockerCode.TargetFrameworkClassificationDiffers));
    }

    [Test]
    [Arguments("4.*")]
    [Arguments("(3.0.0,)")]
    public async Task VersionOnlyAFeedCanResolve_Blocks(string version)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, ScenarioWorkspace.PackageReferences(new PackageRef("MSTest", version)));

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        await Assert.That(Codes(project)).IsEquivalentTo([PreflightCode.UnresolvableVersion]);
    }

    [Test]
    [Arguments("coverlet.collector:6.0.4")]
    [Arguments("coverlet.msbuild:6.0.4")]
    public async Task VsTestOnlyPackage_IsReportedWithoutBlocking(string package)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References($"{MSTest},{package}"));

        ProjectPreflight project = Assess(workspace);

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Eligible);
        PreflightDiagnostic diagnostic = project.Diagnostics.Single();
        await Assert.That((diagnostic.Code, diagnostic.Severity)).IsEqualTo((PreflightCode.VsTestOnlyPackage, BlockerSeverity.Warning));
        await Assert.That(diagnostic.Location!.Line).IsEqualTo(workspace.LineOf(App, package.Split(':')[0]));
    }

    [Test]
    public async Task MixedRunConfiguration_IsReported_ButEachProjectKeepsItsOwnVerdict()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("MSTests/MSTests.csproj", References(MSTest))
            .WriteProject("XUnit2Tests/XUnit2Tests.csproj", References(XUnitV2))
            .WriteSlnx("All.slnx", "MSTests/MSTests.csproj", "XUnit2Tests/XUnit2Tests.csproj");

        PreflightResult result = CompatibilityPreflight.Assess(workspace.Inventory("All.slnx"));

        await Assert.That(result.Projects.Select(p => (p.Path, p.Readiness))).IsEquivalentTo(
            [("MSTests/MSTests.csproj", MigrationReadiness.Eligible), ("XUnit2Tests/XUnit2Tests.csproj", MigrationReadiness.Blocked)]);
        PreflightDiagnostic mixed = result.Diagnostics.Single(d => d.Code == PreflightCode.MixedTestRunners);
        await Assert.That((mixed.Project, mixed.Severity, mixed.Location)).IsEqualTo(((string?)null, BlockerSeverity.Error, new SourceLocation("global.json", 0, 0)));
        await Assert.That(mixed.RelatedLocations).IsEquivalentTo([new SourceLocation("XUnit2Tests/XUnit2Tests.csproj", 0, 0)]);
        await Assert.That(result.CanMigrate).IsFalse();
    }

    [Test]
    public async Task RunConfigurationThatStaysOnVSTest_IsNotMixed()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("Lib/Lib.csproj")
            .WriteProject("XUnit2Tests/XUnit2Tests.csproj", References(XUnitV2))
            .WriteSlnx("All.slnx", "Lib/Lib.csproj", "XUnit2Tests/XUnit2Tests.csproj");

        PreflightResult result = CompatibilityPreflight.Assess(workspace.Inventory("All.slnx"));

        await Assert.That(result.Diagnostics.Select(d => d.Code)).IsEquivalentTo([PreflightCode.XUnitV2NotSupported]);
    }

    [Test]
    public async Task UnresolvedSelection_Blocks()
    {
        using ScenarioWorkspace workspace = new();

        PreflightResult result = CompatibilityPreflight.Assess(workspace.Inventory("Missing.slnx"));

        await Assert.That(result.Projects).IsEmpty();
        await Assert.That(result.Diagnostics.Select(d => d.Code)).IsEquivalentTo([PreflightCode.UnresolvedSelection]);
        await Assert.That(result.CanMigrate).IsFalse();
    }

    /// <summary>Package references from <c>Name:Version,Name:Version</c>.</summary>
    internal static string References(string packages) => ScenarioWorkspace.PackageReferences(
        [.. packages.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':')).Select(p => new PackageRef(p[0], p[1]))]);

    private static string VersionIn(string packages, string package) =>
        packages.Split(',').Select(p => p.Split(':')).Single(p => p[0] == package)[1];

    private static ProjectPreflight Assess(ScenarioWorkspace workspace) => CompatibilityPreflight.Assess(workspace.Inventory(App)).Projects.Single();

    private static IEnumerable<PreflightCode> Codes(ProjectPreflight project) => project.Diagnostics.Select(d => d.Code);
}
