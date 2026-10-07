namespace VsTestToMtp.Tests.Fixtures;

using System.Text.Json;
using System.Xml.Linq;

public class FixtureStructureTests
{
    private static readonly string[] MtpOptInProperties =
    [
        "UseMicrosoftTestingPlatformRunner",
        "TestingPlatformDotnetTestSupport",
        "EnableMSTestRunner",
        "EnableNUnitRunner",
        "EnableTUnitRunner",
    ];

    [Test]
    public async Task Catalog_ContainsEveryFrameworkInBothVariants()
    {
        await Assert.That(FixtureCatalog.All).Count().IsEqualTo(8);
        await Assert.That(FixtureCatalog.All.Select(f => f.Framework).Distinct().Order())
            .IsEquivalentTo(["mstest", "nunit", "xunit-v2", "xunit-v3"]);
        await Assert.That(FixtureCatalog.All.Count(f => f.UsesCentralPackageManagement)).IsEqualTo(4);
    }

    [Test]
    [MethodDataSource(typeof(FixtureCatalog), nameof(FixtureCatalog.AllAsDataSource))]
    public async Task Fixture_HasItsOwnGlobalJsonInVsTestMode(FixtureInfo fixture)
    {
        string globalJson = Path.Combine(fixture.RootPath, "global.json");
        await Assert.That(File.Exists(globalJson)).IsTrue();

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(globalJson));
        await Assert.That(document.RootElement.GetProperty("sdk").GetProperty("version").GetString()).StartsWith("10.");

        // No "test": { "runner": "Microsoft.Testing.Platform" } => `dotnet test` runs in VSTest mode.
        await Assert.That(document.RootElement.TryGetProperty("test", out _)).IsFalse();
    }

    [Test]
    [MethodDataSource(typeof(FixtureCatalog), nameof(FixtureCatalog.AllAsDataSource))]
    public async Task Fixture_IsolatesItselfFromRepositoryRootBuildFiles(FixtureInfo fixture)
    {
        await Assert.That(File.Exists(Path.Combine(fixture.RootPath, "Directory.Build.props"))).IsTrue();
        await Assert.That(File.Exists(Path.Combine(fixture.RootPath, "Directory.Packages.props"))).IsTrue();
        await Assert.That(File.ReadAllText(Path.Combine(fixture.RootPath, ".editorconfig"))).Contains("root = true");
    }

    [Test]
    [MethodDataSource(typeof(FixtureCatalog), nameof(FixtureCatalog.AllAsDataSource))]
    public async Task Fixture_CentralPackageManagementMatchesVariant(FixtureInfo fixture)
    {
        XDocument packages = XDocument.Load(Path.Combine(fixture.RootPath, "Directory.Packages.props"));
        string? cpm = packages.Descendants("ManagePackageVersionsCentrally").SingleOrDefault()?.Value;
        IEnumerable<XElement> packageVersions = packages.Descendants("PackageVersion");
        IEnumerable<XElement> packageReferences = LoadTestProject(fixture).Descendants("PackageReference");

        if (fixture.UsesCentralPackageManagement)
        {
            await Assert.That(cpm).IsEqualTo("true");
            await Assert.That(packageVersions.Any()).IsTrue();
            await Assert.That(packageReferences.Any(r => r.Attribute("Version") is not null)).IsFalse();
        }
        else
        {
            await Assert.That(cpm).IsEqualTo("false");
            await Assert.That(packageVersions.Any()).IsFalse();
            await Assert.That(packageReferences.All(r => r.Attribute("Version") is not null)).IsTrue();
        }
    }

    [Test]
    [MethodDataSource(typeof(FixtureCatalog), nameof(FixtureCatalog.AllAsDataSource))]
    public async Task Fixture_TestProjectReferencesProductionProject(FixtureInfo fixture)
    {
        await Assert.That(File.Exists(fixture.ProductionProjectPath)).IsTrue();

        string? reference = LoadTestProject(fixture).Descendants("ProjectReference")
            .Select(r => r.Attribute("Include")?.Value)
            .SingleOrDefault();

        await Assert.That(reference).IsNotNull();
        // Project references use Windows separators; normalize so the check also passes on Linux/macOS.
        string referencePath = reference!.Replace('\\', Path.DirectorySeparatorChar);
        await Assert.That(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(fixture.TestProjectPath)!, referencePath)))
            .IsEqualTo(fixture.ProductionProjectPath);
    }

    [Test]
    [MethodDataSource(typeof(FixtureCatalog), nameof(FixtureCatalog.AllAsDataSource))]
    public async Task Fixture_UsesVsTestPackagesAndNoMtpOptIn(FixtureInfo fixture)
    {
        XElement project = LoadTestProject(fixture);
        string[] packages = [.. project.Descendants("PackageReference").Select(r => r.Attribute("Include")!.Value)];

        // MSTest ships the VSTest SDK/adapter inside the "MSTest" metapackage; the others reference them explicitly.
        if (fixture.Framework != "mstest")
        {
            await Assert.That(packages).Contains("Microsoft.NET.Test.Sdk");
        }

        string[] expected = fixture.Framework switch
        {
            "mstest" => ["MSTest"],
            "nunit" => ["NUnit", "NUnit3TestAdapter"],
            "xunit-v3" => ["xunit.v3.mtp-off", "xunit.runner.visualstudio"],
            "xunit-v2" => ["xunit", "xunit.runner.visualstudio"],
            _ => throw new InvalidOperationException($"Unknown framework '{fixture.Framework}'."),
        };
        foreach (string package in expected)
        {
            await Assert.That(packages).Contains(package);
        }

        foreach (string property in MtpOptInProperties)
        {
            await Assert.That(project.Descendants(property).Any()).IsFalse();
        }
    }

    [Test]
    [MethodDataSource(typeof(FixtureCatalog), nameof(FixtureCatalog.AllAsDataSource))]
    public async Task Fixture_XunitMajorVersionMatchesFramework(FixtureInfo fixture)
    {
        if (!fixture.Framework.StartsWith("xunit", StringComparison.Ordinal))
        {
            return;
        }

        string packageName = fixture.Framework == "xunit-v3" ? "xunit.v3.mtp-off" : "xunit";
        string version = GetPackageVersion(fixture, packageName);

        // xUnit v2 is the intentional migration blocker; there is no automatic v2 -> v3 upgrade.
        await Assert.That(version).StartsWith(fixture.Framework == "xunit-v3" ? "4." : "2.");
    }

    private static XElement LoadTestProject(FixtureInfo fixture) => XDocument.Load(fixture.TestProjectPath).Root!;

    private static string GetPackageVersion(FixtureInfo fixture, string packageName)
    {
        XElement reference = LoadTestProject(fixture).Descendants("PackageReference")
            .Single(r => r.Attribute("Include")?.Value == packageName);

        return reference.Attribute("Version")?.Value
            ?? XDocument.Load(Path.Combine(fixture.RootPath, "Directory.Packages.props"))
                .Descendants("PackageVersion")
                .Single(v => v.Attribute("Include")?.Value == packageName)
                .Attribute("Version")!.Value;
    }
}
