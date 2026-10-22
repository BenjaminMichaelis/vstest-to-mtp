namespace VsTestToMtp.Tests.Migration;

using VsTestToMtp.Inventory;
using VsTestToMtp.Migration;
using VsTestToMtp.Tests.Inventory;

/// <summary>
/// MSTest.Sdk projects, evaluated against a stand-in MSTest.Sdk from a local feed that has the parts of the real one (MSTest.Sdk 4.0.0's
/// <c>Sdk/Sdk.props</c> and runner targets) the rules read: <c>UsingMSTestSdk</c>, the <c>UseVSTest</c> opt-out, the MTP and MSTest
/// versions it brings, and the MSTest packages it declares.
/// </summary>
// LocalSdkFeed clears the process-wide NUGET_PACKAGES, so these must not run alongside any other test (see InventoryProvenanceTests).
[NotInParallel]
public class MSTestSdkPreflightTests
{
    private const string App = "Tests/Tests.csproj";

    private static readonly MigrationRuleSet Rules = MigrationRuleSet.Current;

    [Test]
    [Arguments("3.11.0", "1.9.0", "", false, MigrationReadiness.Eligible, true)]
    [Arguments("3.11.0", "1.9.0", "", true, MigrationReadiness.Eligible, true)]
    [Arguments("4.0.0", "2.0.0", "<UseVSTest>true</UseVSTest>", false, MigrationReadiness.Eligible, false)]
    [Arguments("4.0.0", "2.0.0", "", false, MigrationReadiness.AlreadyMigrated, false)]
    public async Task MSTestSdk_IsRecognisedFromItsEvaluatedState(
        string sdkVersion, string platformVersion, string properties, bool versionInGlobalJson, MigrationReadiness readiness, bool upgraded)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write(App, Project(versionInGlobalJson ? "MSTest.Sdk" : $"MSTest.Sdk/{sdkVersion}", properties));
        if (versionInGlobalJson)
        {
            PinInGlobalJson(workspace, sdkVersion);
        }

        using LocalSdkFeed feed = new LocalSdkFeed(workspace).AddSdk("MSTest.Sdk", sdkVersion, Props(sdkVersion, platformVersion), Targets);

        InventoryResult inventory = feed.Inventory(App);
        ProjectInventory evaluated = inventory.Projects.Single();
        ProjectPreflight project = CompatibilityPreflight.Assess(inventory).Projects.Single();

        // The SDK declares the MSTest packages and the project file chose the SDK, so that is the project's own test evidence.
        await Assert.That(evaluated.Classification).IsEqualTo(ProjectClassification.TestApplication);
        await Assert.That(evaluated.Blockers).IsEmpty();
        SdkReferenceState sdk = evaluated.Sdks.Single(s => s.Name == "MSTest.Sdk");
        await Assert.That((sdk.DeclaredVersion, sdk.ResolvedVersion, sdk.Location.File, sdk.Location.Line))
            .IsEqualTo((versionInGlobalJson ? null : sdkVersion, sdkVersion, App, 1));

        await Assert.That((project.UsesMSTestSdk, project.Framework, project.Readiness)).IsEqualTo((true, (TestFramework?)TestFramework.MSTest, readiness));
        await Assert.That(project.Diagnostics).IsEmpty();
        if (!upgraded)
        {
            await Assert.That(project.PackageChanges).IsEmpty();
            return;
        }

        // Only the SDK changes; the MSTest packages it declares follow it. Its version is edited where it is written.
        PackageChange change = project.PackageChanges.Single();
        await Assert.That((change.Kind, change.Package, change.FromVersion, change.ToVersion))
            .IsEqualTo((PackageChangeKind.SdkUpgrade, "MSTest.Sdk", sdkVersion, Rules.FindPackage("MSTest.Sdk")!.UpgradeTo!.ToString()));
        await Assert.That(change.Reference).IsEqualTo(sdk.Location);
        await Assert.That(change.VersionLocation).IsEqualTo(versionInGlobalJson ? new SourceLocation("global.json", 0, 0) : sdk.Location);
    }

    [Test]
    public async Task MSTestVersionPinnedBelowMtpV2_UnderAnMtpV2Sdk_IsBlocked_BecauseTheSdkDeclaresThePackages()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace().Write(App, Project("MSTest.Sdk/4.0.0", "<MSTestVersion>3.11.0</MSTestVersion>"));
        using LocalSdkFeed feed = new LocalSdkFeed(workspace).AddSdk("MSTest.Sdk", "4.0.0", Props("4.0.0", "2.0.0"), Targets);

        ProjectPreflight project = CompatibilityPreflight.Assess(feed.Inventory(App)).Projects.Single();

        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        await Assert.That(project.PackageChanges).IsEmpty();
        await Assert.That(project.Diagnostics.Select(d => d.Code)).IsEquivalentTo([PreflightCode.PackageNotInRepository, PreflightCode.PackageNotInRepository]);
        await Assert.That(project.Diagnostics.All(d => d.Location!.File.EndsWith("Sdk.targets", StringComparison.Ordinal))).IsTrue();
    }

    private static string Project(string sdk, string properties) => $"""
        <Project Sdk="{sdk}">
          <PropertyGroup>
            <TargetFramework>net10.0</TargetFramework>
            {properties}
          </PropertyGroup>
        </Project>
        """;

    private static void PinInGlobalJson(ScenarioWorkspace workspace, string sdkVersion)
    {
        string globalJson = File.ReadAllText(workspace.PathOf("global.json")).TrimEnd().TrimEnd('}').TrimEnd();
        workspace.Write("global.json", globalJson + $$"""
            ,
              "msbuild-sdks": { "MSTest.Sdk": "{{sdkVersion}}" }
            }
            """);
    }

    private static string Props(string mstestVersion, string platformVersion) => $"""
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

    private const string Targets = """
        <Project>
          <Import Project="Sdk.targets" Sdk="Microsoft.NET.Sdk" />
          <ItemGroup>
            <PackageReference Include="MSTest.TestFramework" Version="$(MSTestVersion)" />
            <PackageReference Include="MSTest.TestAdapter" Version="$(MSTestVersion)" />
          </ItemGroup>
        </Project>
        """;
}
