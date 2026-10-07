namespace VsTestToMtp.Tests.Fixtures;

using IntelliTect.Multitool;

/// <summary>
/// One committed VSTest migration fixture: a self-contained mini repository under <c>fixtures/</c>.
/// </summary>
/// <param name="Framework">Fixture folder name: mstest, nunit, xunit-v3 or xunit-v2.</param>
/// <param name="UsesCentralPackageManagement">Whether this is the CPM variant (<c>cpm</c>) rather than <c>plain</c>.</param>
/// <param name="ExpectedTotalTests">Baseline VSTest total recorded in fixtures/README.md.</param>
public sealed record FixtureInfo(string Framework, bool UsesCentralPackageManagement, int ExpectedTotalTests)
{
    public string Variant => UsesCentralPackageManagement ? "cpm" : "plain";

    public string Name => $"{Framework}/{Variant}";

    public string RootPath => Path.Combine(FixtureCatalog.FixturesRoot, Framework, Variant);

    public string TestProjectPath => Path.Combine(RootPath, "tests", "Calculator.Tests", "Calculator.Tests.csproj");

    public string ProductionProjectPath => Path.Combine(RootPath, "src", "Calculator", "Calculator.csproj");

    public override string ToString() => Name;
}

/// <summary>
/// Enumerates the committed fixtures. Fixtures are generated once by <c>fixtures/generate.ps1</c>
/// and checked in; nothing here (or in any test) regenerates templates.
/// </summary>
public static class FixtureCatalog
{
    // 3 single tests + 1 data-driven test with 3 rows in every fixture.
    private const int BaselineTotalTests = 6;

    public static string FixturesRoot { get; } = Path.Combine(RepositoryPaths.GetDefaultRepoRoot(), "fixtures");

    public static IReadOnlyList<FixtureInfo> All { get; } =
    [
        new("mstest", UsesCentralPackageManagement: false, BaselineTotalTests),
        new("mstest", UsesCentralPackageManagement: true, BaselineTotalTests),
        new("nunit", UsesCentralPackageManagement: false, BaselineTotalTests),
        new("nunit", UsesCentralPackageManagement: true, BaselineTotalTests),
        new("xunit-v3", UsesCentralPackageManagement: false, BaselineTotalTests),
        new("xunit-v3", UsesCentralPackageManagement: true, BaselineTotalTests),
        new("xunit-v2", UsesCentralPackageManagement: false, BaselineTotalTests),
        new("xunit-v2", UsesCentralPackageManagement: true, BaselineTotalTests),
    ];
}
