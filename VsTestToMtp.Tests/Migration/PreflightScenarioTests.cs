namespace VsTestToMtp.Tests.Migration;

using VsTestToMtp.Inventory;
using VsTestToMtp.Migration;
using VsTestToMtp.Tests.Inventory;

/// <summary>
/// Representative passing and blocked cases of the rule matrix, each evaluated with MSBuild from a small repository, so the verdicts
/// come from evaluated properties and package references, not from names.
/// </summary>
// MSBuild evaluation blocks threads; running many evaluations in parallel starves the thread pool and is far slower than serial.
[NotInParallel("MSBuildEvaluation")]
public class PreflightScenarioTests
{
    private const string App = "Tests/Tests.csproj";

    [Test]
    [Arguments("MSTest 4 with its runner", """<PropertyGroup><EnableMSTestRunner>true</EnableMSTestRunner><OutputType>Exe</OutputType></PropertyGroup>""", "MSTest:4.0.2", TestFramework.MSTest)]
    [Arguments("NUnit with its runner", """<PropertyGroup><EnableNUnitRunner>true</EnableNUnitRunner><OutputType>Exe</OutputType></PropertyGroup>""", "NUnit:4.3.2,NUnit3TestAdapter:6.3.1", TestFramework.NUnit)]
    [Arguments("xunit.v3 4.x (MTP v2 by default)", "", "xunit.v3:4.0.1", TestFramework.XUnitV3)]
    [Arguments("xunit.v3.mtp-v2 3.2", "", "xunit.v3.mtp-v2:3.2.2", TestFramework.XUnitV3)]
    [Arguments("TUnit on MTP v2", "", "TUnit:1.45.0", TestFramework.TUnit)]
    public async Task ProjectAlreadyOnMtpV2_IsAlreadyMigrated(string scenario, string body, string packages, TestFramework framework)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, body + References(packages));

        ProjectPreflight project = Preflight(workspace, App).Projects.Single();

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.AlreadyMigrated).Because(scenario);
        await Assert.That(project.Framework).IsEqualTo(framework);
        await Assert.That(project.PackageChanges).IsEmpty();
        await Assert.That(project.Diagnostics).IsEmpty();
    }

    [Test]
    [Arguments("MSTest 3 on MTP v1", "MSTest:3.11.0", "Upgrade MSTest 3.11.0 -> MSTest 4.0.2")]
    [Arguments("MSTest packages pinned separately", "MSTest.TestAdapter:3.6.4,MSTest.TestFramework:3.6.4", "Upgrade MSTest.TestAdapter 3.6.4 -> MSTest.TestAdapter 4.0.2; Upgrade MSTest.TestFramework 3.6.4 -> MSTest.TestFramework 4.0.2")]
    [Arguments("NUnit3TestAdapter 5 (MTP v1)", "NUnit:4.3.2,NUnit3TestAdapter:5.2.0", "Upgrade NUnit3TestAdapter 5.2.0 -> NUnit3TestAdapter 6.3.1")]
    [Arguments("xunit.v3 3.2 defaults to MTP v1", "xunit.v3:3.2.1", "Replace xunit.v3 3.2.1 -> xunit.v3.mtp-v2 3.2.1")]
    [Arguments("xunit.v3 before the mtp-v2 variant existed", "xunit.v3:3.1.0", "Replace xunit.v3 3.1.0 -> xunit.v3.mtp-v2 3.2.2")]
    [Arguments("xunit.v3.mtp-v1", "xunit.v3.mtp-v1:3.2.2", "Replace xunit.v3.mtp-v1 3.2.2 -> xunit.v3.mtp-v2 3.2.2")]
    [Arguments("xunit.v3.core.mtp-off", "xunit.v3.core.mtp-off:4.0.1", "Replace xunit.v3.core.mtp-off 4.0.1 -> xunit.v3.core.mtp-v2 4.0.1")]
    [Arguments("TUnit on MTP v1", "TUnit:0.70.7", "Upgrade TUnit 0.70.7 -> TUnit 1.45.0")]
    [Arguments("an MTP v1 extension", "MSTest:4.0.2,Microsoft.Testing.Extensions.TrxReport:1.9.0", "Upgrade Microsoft.Testing.Extensions.TrxReport 1.9.0 -> Microsoft.Testing.Extensions.TrxReport 2.5.1")]
    [Arguments("an MTP v1 code coverage extension", "MSTest:4.0.2,Microsoft.Testing.Extensions.CodeCoverage:17.14.2", "Upgrade Microsoft.Testing.Extensions.CodeCoverage 17.14.2 -> Microsoft.Testing.Extensions.CodeCoverage 18.8.0")]
    public async Task InFamilyPackageChanges_ArePlannedVisibly(string scenario, string packages, string expected)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References(packages));

        ProjectPreflight project = Preflight(workspace, App).Projects.Single();

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Eligible).Because(scenario);
        await Assert.That(string.Join("; ", project.PackageChanges.Select(c => $"{c.Kind} {c.Package} {c.FromVersion} -> {c.ToPackage} {c.ToVersion}")))
            .IsEqualTo(expected);
        await Assert.That(project.PackageChanges.All(c => c.Reason.Length > 0 && c.VersionLocation is not null)).IsTrue();
        await Assert.That(project.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task XUnitV2_IsBlocked_AndNeverPlannedOntoV3()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(App, References("Microsoft.NET.Test.Sdk:17.14.1,xunit:2.9.3,xunit.runner.visualstudio:3.1.4"));

        ProjectPreflight project = Preflight(workspace, App).Projects.Single();

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        await Assert.That(project.Diagnostics.Select(d => d.Code)).IsEquivalentTo([PreflightCode.XUnitV2NotSupported]);
        await Assert.That(project.PackageChanges).IsEmpty();
    }

    [Test]
    [Arguments("net6.0", TestFramework.MSTest, "MSTest:4.0.2")]
    [Arguments("net7.0", TestFramework.MSTest, "MSTest:4.0.2")]
    [Arguments("netcoreapp3.1", TestFramework.NUnit, "NUnit:4.3.2,NUnit3TestAdapter:6.3.1")]
    [Arguments("net461", TestFramework.MSTest, "MSTest:4.0.2")]
    [Arguments("net462", TestFramework.XUnitV3, "xunit.v3:4.0.1")]
    [Arguments("netstandard2.0", TestFramework.TUnit, "TUnit:1.45.0")]
    public async Task UnsupportedTargetFramework_IsBlocked_NeverRetargeted(string targetFramework, TestFramework framework, string packages)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(App, "<PropertyGroup><IsTestProject>true</IsTestProject></PropertyGroup>" + References(packages), [targetFramework]);

        ProjectPreflight project = Preflight(workspace, App).Projects.Single();

        await Assert.That(project.Framework).IsEqualTo(framework);
        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        PreflightDiagnostic diagnostic = project.Diagnostics.Single(d => d.Severity == BlockerSeverity.Error);
        await Assert.That(diagnostic.Code).IsEqualTo(PreflightCode.UnsupportedTargetFramework);
        await Assert.That(diagnostic.Message).Contains(targetFramework);
        await Assert.That(diagnostic.Location).IsEqualTo(new SourceLocation(App, workspace.LineOf(App, "<TargetFramework>"), 5));
    }

    [Test]
    [Arguments("net8.0", "MSTest:4.0.2")]
    [Arguments("net10.0", "MSTest:4.0.2")]
    [Arguments("net462", "MSTest:4.0.2")]
    [Arguments("net48", "NUnit:4.3.2,NUnit3TestAdapter:6.3.1")]
    [Arguments("net472", "xunit.v3:4.0.1")]
    public async Task SupportedTargetFramework_Passes(string targetFramework, string packages)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References(packages), [targetFramework]);

        ProjectPreflight project = Preflight(workspace, App).Projects.Single();

        await Assert.That(project.Diagnostics.Where(d => d.Severity == BlockerSeverity.Error)).IsEmpty();
    }

    [Test]
    public async Task MultiTargeting_BlocksOnTheUnsupportedTargetFrameworkOnly()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References("MSTest:4.0.2"), ["net8.0", "net7.0"]);

        ProjectPreflight project = Preflight(workspace, App).Projects.Single();

        PreflightDiagnostic diagnostic = project.Diagnostics.Single();
        await Assert.That(diagnostic.Code).IsEqualTo(PreflightCode.UnsupportedTargetFramework);
        await Assert.That(diagnostic.Message).Contains("net7.0");
        await Assert.That(diagnostic.Message).DoesNotContain("targets net8.0");
        await Assert.That(diagnostic.Location!.Line).IsEqualTo(workspace.LineOf(App, "<TargetFrameworks>"));
    }

    [Test]
    [Arguments("<GenerateProgramFile>false</GenerateProgramFile>", "GenerateProgramFile")]
    [Arguments("<GenerateTestingPlatformEntryPoint>false</GenerateTestingPlatformEntryPoint>", "GenerateTestingPlatformEntryPoint")]
    [Arguments("<StartupObject>Tests.Program</StartupObject>", "StartupObject")]
    public async Task CustomEntryPoint_BlocksAProjectThatStillHasToMove(string property, string name)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(App, $"<PropertyGroup>{property}</PropertyGroup>" + References("MSTest:4.0.2"));

        ProjectPreflight project = Preflight(workspace, App).Projects.Single();

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
            + References("MSTest:4.0.2"));

        ProjectPreflight project = Preflight(workspace, App).Projects.Single();

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.AlreadyMigrated);
    }

    [Test]
    public async Task UnresolvedCondition_OnATestApplication_Blocks()
    {
        // UseTrx is never set, so whether the extension (and its MTP version) is part of the project depends on how the build is invoked.
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References("MSTest:4.0.2") + """
              <ItemGroup Condition="'$(UseTrx)' == 'true'">
                <PackageReference Include="Microsoft.Testing.Extensions.TrxReport" Version="1.9.0" />
              </ItemGroup>
            """);

        ProjectPreflight project = Preflight(workspace, App).Projects.Single();

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        PreflightDiagnostic diagnostic = project.Diagnostics.Single();
        await Assert.That(diagnostic.Code).IsEqualTo(PreflightCode.UnresolvedProjectState);
        await Assert.That(diagnostic.Severity).IsEqualTo(BlockerSeverity.Error);
        await Assert.That(diagnostic.Message).Contains(nameof(BlockerCode.ConditionDependsOnUnsetProperty));
        await Assert.That(diagnostic.Location!.Line).IsEqualTo(workspace.LineOf(App, "Microsoft.Testing.Extensions.TrxReport"));
    }

    [Test]
    [Arguments("4.*")]
    [Arguments("(3.0.0,)")]
    public async Task VersionOnlyAFeedCanResolve_Blocks(string version)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, ScenarioWorkspace.PackageReferences(new PackageRef("MSTest", version)));

        ProjectPreflight project = Preflight(workspace, App).Projects.Single();

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        await Assert.That(project.Diagnostics.Select(d => d.Code)).IsEquivalentTo([PreflightCode.UnresolvableVersion]);
    }

    [Test]
    public async Task NUnit2_IsBlocked_NotMigratedToNUnit3()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References("NUnit:2.6.4,NUnitTestAdapter:2.3.0"));

        ProjectPreflight project = Preflight(workspace, App).Projects.Single();

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        await Assert.That(project.PackageChanges).IsEmpty();
        await Assert.That(project.Diagnostics.Select(d => d.Code)).IsEquivalentTo(
            [PreflightCode.MissingRunnerPackage, PreflightCode.UnsupportedTestFramework, PreflightCode.UnsupportedTestFramework]);
    }

    [Test]
    public async Task SeveralTestFrameworks_AreBlocked()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject(App, References("MSTest:4.0.2,NUnit:4.3.2,NUnit3TestAdapter:6.3.1"));

        ProjectPreflight project = Preflight(workspace, App).Projects.Single();

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        await Assert.That(project.Framework).IsNull();
        await Assert.That(project.Diagnostics.Select(d => d.Code)).IsEquivalentTo([PreflightCode.MultipleTestFrameworks]);
    }

    [Test]
    public async Task ProductionProjectWithATestLibraryDependency_IsNotATestApplication()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject("Helpers/Helpers.csproj", References("MSTest.TestFramework:3.6.4"));

        ProjectPreflight project = Preflight(workspace, "Helpers/Helpers.csproj").Projects.Single();

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.NotATestApplication);
        await Assert.That(project.PackageChanges).IsEmpty();
        await Assert.That(project.Diagnostics).IsEmpty();
    }

    [Test]
    public async Task MixedRunConfiguration_IsReported_ButEachProjectKeepsItsOwnVerdict()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("MSTests/MSTests.csproj", References("MSTest:4.0.2"))
            .WriteProject("XUnit2Tests/XUnit2Tests.csproj", References("Microsoft.NET.Test.Sdk:17.14.1,xunit:2.9.3,xunit.runner.visualstudio:3.1.4"))
            .WriteSlnx("All.slnx", "MSTests/MSTests.csproj", "XUnit2Tests/XUnit2Tests.csproj");

        PreflightResult result = Preflight(workspace, "All.slnx");

        await Assert.That(result.Projects.Single(p => p.Path == "MSTests/MSTests.csproj").Readiness).IsEqualTo(MigrationReadiness.Eligible);
        await Assert.That(result.Projects.Single(p => p.Path == "XUnit2Tests/XUnit2Tests.csproj").Readiness).IsEqualTo(MigrationReadiness.Blocked);
        PreflightDiagnostic mixed = result.Diagnostics.Single(d => d.Code == PreflightCode.MixedTestRunners);
        await Assert.That(mixed.Project).IsNull();
        await Assert.That(mixed.Severity).IsEqualTo(BlockerSeverity.Error);
        await Assert.That(mixed.Location).IsEqualTo(new SourceLocation("global.json", 0, 0));
        await Assert.That(mixed.RelatedLocations).IsEquivalentTo([new SourceLocation("XUnit2Tests/XUnit2Tests.csproj", 0, 0)]);
        await Assert.That(result.CanMigrate).IsFalse();
    }

    [Test]
    public async Task RunConfigurationThatStaysOnVSTest_IsNotMixed()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("Lib/Lib.csproj")
            .WriteProject("XUnit2Tests/XUnit2Tests.csproj", References("Microsoft.NET.Test.Sdk:17.14.1,xunit:2.9.3,xunit.runner.visualstudio:3.1.4"))
            .WriteSlnx("All.slnx", "Lib/Lib.csproj", "XUnit2Tests/XUnit2Tests.csproj");

        PreflightResult result = Preflight(workspace, "All.slnx");

        await Assert.That(result.Diagnostics.Select(d => d.Code)).IsEquivalentTo([PreflightCode.XUnitV2NotSupported]);
    }

    [Test]
    public async Task UnresolvedSelection_Blocks()
    {
        using ScenarioWorkspace workspace = new();

        PreflightResult result = Preflight(workspace, "Missing.slnx");

        await Assert.That(result.Projects).IsEmpty();
        await Assert.That(result.Diagnostics.Select(d => d.Code)).IsEquivalentTo([PreflightCode.UnresolvedSelection]);
        await Assert.That(result.CanMigrate).IsFalse();
    }

    [Test]
    [Arguments("3.11.0", "1.9.0", "", false, MigrationReadiness.Eligible, "SdkUpgrade MSTest.Sdk 3.11.0 -> MSTest.Sdk 4.0.2")]
    [Arguments("3.11.0", "1.9.0", "", true, MigrationReadiness.Eligible, "SdkUpgrade MSTest.Sdk 3.11.0 -> MSTest.Sdk 4.0.2")]
    [Arguments("4.0.2", "2.0.0", "<UseVSTest>true</UseVSTest>", false, MigrationReadiness.Eligible, "")]
    [Arguments("4.0.2", "2.0.0", "", false, MigrationReadiness.AlreadyMigrated, "")]
    public async Task MSTestSdk_IsRecognisedFromItsEvaluatedState(
        string sdkVersion, string platformVersion, string properties, bool versionInGlobalJson, MigrationReadiness readiness, string expected)
    {
        if (Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 })
        {
            Skip.Test("NUGET_PACKAGES is set, so the SDK package location is not under this test's control.");
            return;
        }

        string cache = Path.Combine(Path.GetTempPath(), "vstest-to-mtp-sdk-cache", Guid.NewGuid().ToString("N"));
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("NuGet.config", LocalFeedNuGetConfig)
            .Write(App, $"""
                <Project Sdk="MSTest.Sdk{(versionInGlobalJson ? string.Empty : "/" + sdkVersion)}">
                  <PropertyGroup>
                    <TargetFramework>net10.0</TargetFramework>
                    {properties}
                  </PropertyGroup>
                </Project>
                """);
        ScenarioWorkspace.WriteSdkPackage(workspace.PathOf("feed"), "MSTest.Sdk", sdkVersion, FakeMSTestSdkProps(sdkVersion, platformVersion), FakeMSTestSdkTargets);
        if (versionInGlobalJson)
        {
            string globalJson = File.ReadAllText(workspace.PathOf("global.json")).TrimEnd().TrimEnd('}').TrimEnd();
            workspace.Write("global.json", globalJson + $$"""
                ,
                  "msbuild-sdks": { "MSTest.Sdk": "{{sdkVersion}}" }
                }
                """);
        }

        try
        {
            InventoryResult inventory = InventoryBuilder.Build(workspace.PathOf(App), new InventoryOptions(workspace.RootPath, SdkPackagesDirectory: cache));
            ProjectInventory evaluated = inventory.Projects.Single();
            ProjectPreflight project = CompatibilityPreflight.Assess(inventory).Projects.Single();

            // The SDK declares the MSTest packages, and the project file chose the SDK, so that is test evidence of the project itself.
            await Assert.That(evaluated.Classification).IsEqualTo(ProjectClassification.TestApplication);
            await Assert.That(evaluated.Blockers).IsEmpty();
            SdkReferenceState sdk = evaluated.Sdks.Single(s => s.Name == "MSTest.Sdk");
            await Assert.That((sdk.DeclaredVersion, sdk.ResolvedVersion, sdk.Location.File, sdk.Location.Line))
                .IsEqualTo((versionInGlobalJson ? null : sdkVersion, sdkVersion, App, 1));

            await Assert.That(project.UsesMSTestSdk).IsTrue();
            await Assert.That(project.Framework).IsEqualTo(TestFramework.MSTest);
            await Assert.That(project.Readiness).IsEqualTo(readiness);
            await Assert.That(project.Diagnostics).IsEmpty();
            await Assert.That(string.Join("; ", project.PackageChanges.Select(c => $"{c.Kind} {c.Package} {c.FromVersion} -> {c.ToPackage} {c.ToVersion}")))
                .IsEqualTo(expected);
            foreach (PackageChange change in project.PackageChanges)
            {
                await Assert.That(change.Reference).IsEqualTo(sdk.Location);
                await Assert.That(change.VersionLocation).IsEqualTo(versionInGlobalJson ? new SourceLocation("global.json", 0, 0) : sdk.Location);
            }
        }
        finally
        {
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
    }

    private const string LocalFeedNuGetConfig = """
        <configuration>
          <packageSources>
            <clear />
            <add key="local" value="feed" />
          </packageSources>
        </configuration>
        """;

    // The parts of MSTest.Sdk's Sdk.props and runner targets the preflight reads (compare MSTest.Sdk 4.0.0's Sdk/Sdk.props).
    private static string FakeMSTestSdkProps(string mstestVersion, string platformVersion) => $"""
        <Project>
          <PropertyGroup>
            <UsingMSTestSdk>true</UsingMSTestSdk>
          </PropertyGroup>
          <Import Project="Sdk.props" Sdk="Microsoft.NET.Sdk" />
          <PropertyGroup>
            <UseVSTest Condition=" '$(UseVSTest)' == '' ">false</UseVSTest>
            <MicrosoftTestingPlatformVersion Condition=" '$(MicrosoftTestingPlatformVersion)' == '' ">{platformVersion}</MicrosoftTestingPlatformVersion>
            <MSTestVersion Condition=" '$(MSTestVersion)' == '' ">{mstestVersion}</MSTestVersion>
          </PropertyGroup>
        </Project>
        """;

    private const string FakeMSTestSdkTargets = """
        <Project>
          <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
          <ItemGroup>
            <PackageReference Include="MSTest.TestFramework" Version="$(MSTestVersion)" />
            <PackageReference Include="MSTest.TestAdapter" Version="$(MSTestVersion)" />
          </ItemGroup>
        </Project>
        """;

    /// <summary>Package references from <c>Name:Version,Name:Version</c>.</summary>
    private static string References(string packages) => ScenarioWorkspace.PackageReferences(
        [.. packages.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split(':')).Select(p => new PackageRef(p[0], p[1]))]);

    private static PreflightResult Preflight(ScenarioWorkspace workspace, string selection) =>
        CompatibilityPreflight.Assess(workspace.Inventory(selection));
}
