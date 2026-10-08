namespace VsTestToMtp.Tests.Inventory;

using System.Collections.Concurrent;
using System.Text.Json;

using TUnit.Assertions.Enums;

using VsTestToMtp.Inventory;
using VsTestToMtp.Tests.Fixtures;

// MSBuild evaluation blocks threads; running many evaluations in parallel starves the thread pool and is far slower than serial.
[NotInParallel("MSBuildEvaluation")]
public class InventoryFixtureTests
{
    private const string TestProjectPath = "tests/Calculator.Tests/Calculator.Tests.csproj";
    private const string ProductionProjectPath = "src/Calculator/Calculator.csproj";

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Inventory_ListsSolutionMembersInStableOrderWithoutBlockers(FixtureInfo fixture)
    {
        InventoryResult result = Inventory(fixture);

        await Assert.That(result.Projects.Select(p => p.Path)).IsEquivalentTo([ProductionProjectPath, TestProjectPath], CollectionOrdering.Matching);
        await Assert.That(result.Selection.Kind).IsEqualTo(SelectionKind.Slnx);
        await Assert.That(result.Blockers).IsEmpty();
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Inventory_ClassifiesTestApplicationAndProductionProject(FixtureInfo fixture)
    {
        InventoryResult result = Inventory(fixture);

        ProjectInventory production = result.Projects.Single(p => p.Path == ProductionProjectPath);
        ProjectInventory tests = result.Projects.Single(p => p.Path == TestProjectPath);

        await Assert.That(production.Classification).IsEqualTo(ProjectClassification.Production);
        await Assert.That(production.HasTestLibraryDependency).IsFalse();
        await Assert.That(tests.Classification).IsEqualTo(ProjectClassification.TestApplication);
        await Assert.That(result.TestApplications.Select(p => p.Path)).IsEquivalentTo([TestProjectPath]);
        await Assert.That(tests.TargetFrameworks.Select(tf => tf.TargetFramework)).IsEquivalentTo(["net10.0"]);
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Inventory_DetectsTestFrameworkFromPackageEvidence(FixtureInfo fixture)
    {
        ProjectInventory tests = Inventory(fixture).Projects.Single(p => p.Path == TestProjectPath);

        string expected = fixture.Framework switch
        {
            "mstest" => "MSTest",
            "nunit" => "NUnit",
            "xunit-v3" => "xUnit v3",
            "xunit-v2" => "xUnit v2",
            _ => throw new InvalidOperationException($"Unknown framework '{fixture.Framework}'."),
        };

        await Assert.That(tests.Frameworks).IsEquivalentTo([expected]);
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Inventory_RecordsWhereEachPackageVersionIsDeclared(FixtureInfo fixture)
    {
        TargetFrameworkState state = Inventory(fixture).Projects.Single(p => p.Path == TestProjectPath).TargetFrameworks.Single();
        PackageReferenceState[] packages = [.. state.Packages];

        await Assert.That(packages).IsNotEmpty();
        foreach (PackageReferenceState package in packages)
        {
            await Assert.That(package.Definition.Location.File).IsEqualTo(TestProjectPath);
            await Assert.That(LineAt(fixture, package.Definition.Location)).Contains($"Include=\"{package.Name}\"");
            await Assert.That(package.Version).IsNotNull();

            if (fixture.UsesCentralPackageManagement)
            {
                await Assert.That(package.VersionSource).IsEqualTo(PackageVersionSource.Central);
                await Assert.That(package.VersionDefinition!.Location.File).IsEqualTo("Directory.Packages.props");
                string centralLine = LineAt(fixture, package.VersionDefinition.Location);
                await Assert.That(centralLine).Contains($"Include=\"{package.Name}\"");
                await Assert.That(centralLine).Contains($"Version=\"{package.Version}\"");
            }
            else
            {
                await Assert.That(package.VersionSource).IsEqualTo(PackageVersionSource.Inline);
                await Assert.That(package.VersionDefinition!.Location.File).IsEqualTo(TestProjectPath);
            }
        }
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Inventory_RecordsCentralPackageManagementProperty(FixtureInfo fixture)
    {
        TargetFrameworkState state = Inventory(fixture).Projects.Single(p => p.Path == TestProjectPath).TargetFrameworks.Single();
        PropertyState? cpm = state.GetProperty("ManagePackageVersionsCentrally");

        await Assert.That(cpm).IsNotNull();
        await Assert.That(cpm!.Value).IsEqualTo(fixture.UsesCentralPackageManagement ? "true" : "false");
        await Assert.That(cpm.Definition!.Location.File).IsEqualTo("Directory.Packages.props");
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Inventory_ResolvesProjectReferenceAndRepositoryImports(FixtureInfo fixture)
    {
        InventoryResult result = Inventory(fixture);
        ProjectInventory tests = result.Projects.Single(p => p.Path == TestProjectPath);

        ProjectReferenceState reference = tests.TargetFrameworks.Single().ProjectReferences.Single();
        await Assert.That(reference.Path).IsEqualTo(ProductionProjectPath);
        await Assert.That(reference.IsInSelection).IsTrue();

        // The fixture sits inside this repository, so MSBuild also finds the repository's own Directory.Build.targets
        // above it; that is genuine evaluated state and is reported with an absolute path.
        await Assert.That(tests.Imports.Select(i => (i.File, i.Kind))).Contains(("Directory.Build.props", ImportKind.DirectoryBuildProps));
        await Assert.That(tests.Imports.Select(i => (i.File, i.Kind))).Contains(("Directory.Packages.props", ImportKind.DirectoryPackagesProps));
        await Assert.That(tests.Imports.Any(i => i.File.Contains("/obj/", StringComparison.Ordinal))).IsFalse();
        await Assert.That(tests.Imports.All(i => i.IsImplicit && i.IsRepositoryFile)).IsTrue();
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Inventory_ReadsGlobalJsonInVsTestMode(FixtureInfo fixture)
    {
        GlobalJsonInfo? globalJson = Inventory(fixture).GlobalJson;

        await Assert.That(globalJson).IsNotNull();
        await Assert.That(globalJson!.Path).IsEqualTo("global.json");
        using JsonDocument committed = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.RootPath, "global.json")));
        JsonElement sdk = committed.RootElement.GetProperty("sdk");
        await Assert.That(globalJson.SdkVersion).IsEqualTo(sdk.GetProperty("version").GetString());
        await Assert.That(globalJson.RollForward).IsEqualTo(sdk.GetProperty("rollForward").GetString());
        await Assert.That(globalJson.TestRunner).IsNull();
    }

    [Test]
    [MethodDataSource(typeof(FixtureDataSources), nameof(FixtureDataSources.All))]
    public async Task Inventory_DoesNotMutateCommittedFixtureAndIsDeterministic(FixtureInfo fixture)
    {
        string before = FixtureWorkspace.ComputeHash(fixture.RootPath);

        string first = JsonSerializer.Serialize(Build(fixture));
        string second = JsonSerializer.Serialize(Build(fixture));

        await Assert.That(FixtureWorkspace.ComputeHash(fixture.RootPath)).IsEqualTo(before);
        await Assert.That(second).IsEqualTo(first);
    }

    [Test]
    public async Task Inventory_AcceptsASingleProjectFile()
    {
        FixtureInfo fixture = FixtureCatalog.All[0];

        InventoryResult result = InventoryBuilder.Build(fixture.TestProjectPath, new InventoryOptions(fixture.RootPath));

        await Assert.That(result.Selection.Kind).IsEqualTo(SelectionKind.Project);
        await Assert.That(result.Projects.Select(p => p.Path)).IsEquivalentTo([TestProjectPath]);
        await Assert.That(result.Projects.Single().Classification).IsEqualTo(ProjectClassification.TestApplication);
    }

    // The text of the line a location points at, read from the committed fixture.
    private static string LineAt(FixtureInfo fixture, SourceLocation location) =>
        File.ReadAllLines(Path.Combine(fixture.RootPath, location.File.Replace('/', Path.DirectorySeparatorChar)))[location.Line - 1];

    // MSBuild evaluation is slow, so evaluate each fixture once and share the (immutable) result between tests.
    private static readonly ConcurrentDictionary<string, Lazy<InventoryResult>> Cache = new();

    private static InventoryResult Inventory(FixtureInfo fixture) =>
        Cache.GetOrAdd(fixture.Name, _ => new Lazy<InventoryResult>(() => Build(fixture))).Value;

    private static InventoryResult Build(FixtureInfo fixture) =>
        InventoryBuilder.Build(Path.Combine(fixture.RootPath, "Fixture.slnx"), new InventoryOptions(fixture.RootPath));
}
