namespace VsTestToMtp.Tests.Migration;

using VsTestToMtp.Inventory;
using VsTestToMtp.Migration;

/// <summary>
/// The SDK rule needs an SDK older than the one the suite runs on, so it is checked against an inventory as the MSBuild host reports it
/// (the evaluated <c>NETCoreSdkVersion</c>) instead of a real evaluation.
/// </summary>
public class PreflightSdkTests
{
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
            await Assert.That(sdk[0].Project).IsNull();
            await Assert.That(sdk[0].Location).IsEqualTo(new SourceLocation("global.json", 0, 0));
            await Assert.That(sdk[0].Message).Contains(sdkVersion);
        }

        // The project itself is fine; only the run configuration's SDK is not.
        await Assert.That(result.Projects.Single().Readiness).IsEqualTo(MigrationReadiness.Eligible);
    }

    private static InventoryResult Inventory(string sdkVersion)
    {
        const string project = "Tests/Tests.csproj";
        Provenance InProject(int line) => new(new SourceLocation(project, line, 5), [], [], IsFromProjectFile: true, IsRepositoryFile: true);
        PropertyState Property(string name, string value, int line) => new(name, value, PropertySource.File, InProject(line), []);
        PropertyState Sdk(string value) => new("NETCoreSdkVersion", value, PropertySource.File,
            new Provenance(new SourceLocation("C:/dotnet/sdk/Microsoft.NETCoreSdk.BundledVersions.props", 20, 5), [], [], false, false), []);

        TargetFrameworkState framework = new(
            "net8.0",
            ProjectClassification.TestApplication,
            HasTestLibraryDependency: true,
            Evidence: [],
            Properties: [Property("TargetFramework", "net8.0", 3), Property("TargetFrameworkIdentifier", ".NETCoreApp", 3), Property("TargetFrameworkVersion", "v8.0", 3), Sdk(sdkVersion)],
            Packages: [new PackageReferenceState("MSTest", "4.0.2", PackageVersionSource.Inline, false, InProject(6), [], InProject(6))],
            ProjectReferences: []);

        return new InventoryResult(
            "C:/repo",
            new SelectionInfo(project, SelectionKind.Project),
            [new ProjectInventory(project, "Tests", ProjectClassification.TestApplication, true, [TestFramework.MSTest], [TestRunnerKind.VSTest], [framework], [], [], [])],
            new GlobalJsonInfo("global.json", sdkVersion, "latestFeature", null, []),
            [],
            []);
    }
}
