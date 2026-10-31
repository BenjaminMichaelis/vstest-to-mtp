namespace VsTestToMtp.Tests.Migration;

using VsTestToMtp.Inventory;
using VsTestToMtp.Migration;

/// <summary>
/// What the run configuration needs (its .NET SDK, and global.json selecting the MTP mode of <c>dotnet test</c>). An SDK older than the
/// one the suite runs on cannot be evaluated here, so these use an inventory as the MSBuild host reports it instead of a real evaluation.
/// </summary>
public class PreflightRunConfigurationTests
{
    private const string Project = "Tests/Tests.csproj";

    [Test]
    [Arguments("9.0.300", true)]
    [Arguments("8.0.100", true)]
    [Arguments("10.0.100", false)]
    [Arguments("10.0.401", false)]
    [Arguments("11.0.100-preview.6.26357.103", false)]
    public async Task SdkBefore10_BlocksTheSelection(string sdkVersion, bool blocked)
    {
        PreflightResult result = CompatibilityPreflight.Assess(Inventory(sdkVersion));

        PreflightDiagnostic[] sdk = [.. result.Diagnostics.Where(d => d.Code == PreflightCode.UnsupportedSdk)];
        await Assert.That(sdk.Length).IsEqualTo(blocked ? 1 : 0);
        await Assert.That(result.CanMigrate).IsEqualTo(!blocked);
        if (blocked)
        {
            await Assert.That((sdk[0].Project, sdk[0].Location)).IsEqualTo(((string?)null, (SourceLocation?)SourceLocation.OfFile("global.json")));
            await Assert.That(sdk[0].Message).Contains(sdkVersion);
        }

        // The project itself is fine; only the run configuration's SDK is not.
        await Assert.That(result.Projects.Single().Readiness).IsEqualTo(MigrationReadiness.Eligible);
    }

    // What cannot be decided is an error, not a pass.
    [Test]
    public async Task TestApplicationThatDoesNotReportItsSdk_IsBlocked()
    {
        PreflightResult result = CompatibilityPreflight.Assess(Inventory(sdkVersion: null));

        ProjectPreflight project = result.Projects.Single();
        await Assert.That(project.Readiness).IsEqualTo(MigrationReadiness.Blocked);
        await Assert.That(project.Diagnostics.Select(d => d.Code)).IsEquivalentTo([PreflightCode.UnsupportedSdk]);
        await Assert.That(result.CanMigrate).IsFalse();
    }

    // MTP v2 test applications only run in the MTP mode of `dotnet test`; one already on MTP v2 is broken until global.json selects it.
    [Test]
    [Arguments(true, null, true)]
    [Arguments(true, "VSTest", true)]
    [Arguments(false, null, true)]
    [Arguments(true, "Microsoft.Testing.Platform", false)]
    [Arguments(true, "microsoft.testing.platform", false)]
    public async Task GlobalJsonNotSelectingMtp_IsReported_WithoutBlocking(bool hasGlobalJson, string? testRunner, bool reported)
    {
        PreflightResult result = CompatibilityPreflight.Assess(Inventory("10.0.401", hasGlobalJson, testRunner, enableMSTestRunner: true));

        await Assert.That(result.Projects.Single().Readiness).IsEqualTo(MigrationReadiness.AlreadyMigrated);
        PreflightDiagnostic[] notSelected = [.. result.Diagnostics.Where(d => d.Code == PreflightCode.TestRunnerNotSelected)];
        await Assert.That(notSelected.Length).IsEqualTo(reported ? 1 : 0);
        if (reported)
        {
            await Assert.That(notSelected[0].Severity).IsEqualTo(BlockerSeverity.Warning);
            await Assert.That(notSelected[0].Location).IsEqualTo(hasGlobalJson ? SourceLocation.OfFile("global.json") : null);
            await Assert.That(notSelected[0].Message).Contains(Project);
        }

        await Assert.That(result.CanMigrate).IsTrue();
    }

    private static InventoryResult Inventory(string? sdkVersion, bool hasGlobalJson = true, string? testRunner = null, bool enableMSTestRunner = false)
    {
        static Provenance InProject(int line) => new(new SourceLocation(Project, line, 5), [], [], IsFromProjectFile: true, IsRepositoryFile: true);
        static PropertyState Property(string name, string value, int line) => new(name, value, PropertySource.File, InProject(line), []);

        List<PropertyState> properties =
        [
            Property(MsBuildPropertyNames.TargetFramework, "net8.0", 3),
            Property(MsBuildPropertyNames.TargetFrameworkIdentifier, ".NETCoreApp", 3),
            Property(MsBuildPropertyNames.TargetFrameworkVersion, "v8.0", 3),
        ];
        if (sdkVersion is not null)
        {
            properties.Add(new PropertyState(MsBuildPropertyNames.NETCoreSdkVersion, sdkVersion, PropertySource.File,
                new Provenance(new SourceLocation("C:/dotnet/sdk/Microsoft.NETCoreSdk.BundledVersions.props", 20, 5), [], [], false, false), []));
        }

        if (enableMSTestRunner)
        {
            properties.Add(Property(MsBuildPropertyNames.EnableMSTestRunner, "true", 4));
        }

        TargetFrameworkState framework = new(
            "net8.0",
            ProjectClassification.TestApplication,
            HasTestLibraryDependency: true,
            Evidence: [],
            Properties: properties,
            Packages: [new PackageReferenceState("MSTest", "4.0.2", PackageVersionSource.Inline, false, InProject(6), [], InProject(6))],
            ProjectReferences: []);

        return new InventoryResult(
            "C:/repo",
            new SelectionInfo(Project, SelectionKind.Project),
            [new ProjectInventory(Project, "Tests", ProjectClassification.TestApplication, true, [TestFramework.MSTest], [TestRunnerKind.VSTest], [framework], [], [], [])],
            hasGlobalJson ? new GlobalJsonInfo("global.json", sdkVersion, "latestFeature", testRunner, []) : null,
            [],
            []);
    }
}
