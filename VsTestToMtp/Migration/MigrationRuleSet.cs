using NuGet.Versioning;

using VsTestToMtp.Inventory;

namespace VsTestToMtp.Migration;

/// <summary>A runtime a test application may target: <c>TargetFrameworkIdentifier</c> at <c>TargetFrameworkVersion</c> or later.</summary>
/// <param name="Moniker">The lowest target framework moniker that qualifies, for messages (<c>net8.0</c>).</param>
public sealed record RuntimeTarget(string Identifier, NuGetVersion MinimumVersion, string Moniker);

/// <summary>What kind of package a <see cref="PackageRule"/> is about.</summary>
public enum PackageRuleKind
{
    /// <summary>A test framework, its adapter or its SDK.</summary>
    Framework,

    /// <summary>An MTP extension or the platform itself, which work with any test framework.</summary>
    Extension,
}

/// <summary>One row of the package matrix: below <see cref="MinimumVersion"/> the package does not run on MTP v2.</summary>
/// <param name="Package">The package id (case-insensitive), or the SDK name for <see cref="MigrationRuleSet.MSTestSdk"/>.</param>
/// <param name="Framework">The test framework the package belongs to; <see langword="null"/> for extensions, which work with any.</param>
/// <param name="MinimumVersion">The lowest version that runs on MTP v2.</param>
/// <param name="UpgradeTo">
/// The tested in-family version a lower version is upgraded to; <see langword="null"/> when a lower version cannot be upgraded without
/// migrating the test framework (NUnit 2), which the tool never does.
/// </param>
/// <param name="Evidence">Why the threshold is what it is (the package's own MTP dependency, its documentation).</param>
public sealed record PackageRule(
    string Package,
    PackageRuleKind Kind,
    TestFramework? Framework,
    NuGetVersion MinimumVersion,
    NuGetVersion? UpgradeTo,
    string Evidence)
{
    /// <summary>
    /// The MSTest.Sdk property that sets this package's version when MSTest.Sdk declares the package (directly, or as the default of a
    /// more specific property); <see langword="null"/> when MSTest.Sdk does not declare it.
    /// </summary>
    public string? MSTestSdkVersionProperty { get; init; }
}

/// <summary>A package that only works with VSTest (a data collector) and does nothing under MTP, at any version.</summary>
public sealed record VsTestOnlyPackage(string Package, string Evidence);

/// <summary>
/// The versioned, offline compatibility matrix for migrating VSTest test applications to Microsoft.Testing.Platform (MTP) v2 under the
/// .NET 10 SDK's MTP mode of <c>dotnet test</c>. Every threshold here was read from the packages' own dependencies on nuget.org and
/// every upgrade target was run under MTP v2 (see docs/migration-rules.md).
/// </summary>
public sealed class MigrationRuleSet
{
    private readonly Dictionary<string, PackageRule> _packages;
    private readonly Dictionary<string, VsTestOnlyPackage> _vsTestOnly;

    private MigrationRuleSet(IEnumerable<PackageRule> packages, IEnumerable<VsTestOnlyPackage> vsTestOnly)
    {
        _packages = packages.ToDictionary(p => p.Package, StringComparer.OrdinalIgnoreCase);
        _vsTestOnly = vsTestOnly.ToDictionary(p => p.Package, StringComparer.OrdinalIgnoreCase);
        Packages = [.. _packages.Values.OrderBy(p => p.Package, StringComparer.OrdinalIgnoreCase)];
        VsTestOnlyPackages = [.. _vsTestOnly.Values.OrderBy(p => p.Package, StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>The rule set in effect.</summary>
    public static MigrationRuleSet Current { get; } = new(CurrentPackages(), CurrentVsTestOnlyPackages());

    /// <summary>What the rules target: MTP v2 on the .NET 10 SDK's MTP mode of <c>dotnet test</c>.</summary>
    public string Id { get; } = "mtp-v2-dotnet10";

    /// <summary>
    /// Bumped whenever a verdict the rules produce can change: a threshold, an upgrade target or a runtime target here, and equally a rule
    /// in <see cref="CompatibilityPreflight"/> or one of the framework rules it applies. A result names the version it was made with.
    /// </summary>
    public int Version { get; } = 1;

    /// <summary>
    /// The .NET SDK whose <c>dotnet test</c> has an MTP mode (selected with <c>global.json</c> <c>test.runner</c>). MTP v2 can no longer run
    /// under the VSTest mode of <c>dotnet test</c>, which <c>TestingPlatformDotnetTestSupport</c> enabled for MTP v1.
    /// https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-migration-from-v1-to-v2
    /// </summary>
    public NuGetVersion MinimumSdkVersion { get; } = NuGetVersion.Parse("10.0.100");

    /// <summary>The <c>global.json</c> <c>test.runner</c> value that selects the MTP mode of <c>dotnet test</c>.</summary>
    public string TestRunner { get; } = "Microsoft.Testing.Platform";

    /// <summary>
    /// The runtimes MTP v2 supports: .NET 8 or later and .NET Framework 4.6.2 or later (.NET Core 3.1 to .NET 7 were dropped).
    /// https://learn.microsoft.com/dotnet/core/testing/microsoft-testing-platform-migration-from-v1-to-v2#dropped-unsupported-target-frameworks
    /// </summary>
    public IReadOnlyList<RuntimeTarget> RuntimeTargets { get; } =
    [
        new(".NETCoreApp", NuGetVersion.Parse("8.0"), "net8.0"),
        new(".NETFramework", NuGetVersion.Parse("4.6.2"), "net462"),
    ];

    /// <summary>The package matrix, ordered by package id.</summary>
    public IReadOnlyList<PackageRule> Packages { get; }

    /// <summary>Packages that only work with VSTest, ordered by package id.</summary>
    public IReadOnlyList<VsTestOnlyPackage> VsTestOnlyPackages { get; }

    /// <summary>
    /// MSTest.Sdk, the project SDK whose own <c>UseVSTest</c> property opts out of MTP. It is versioned where the project references it
    /// (<c>Sdk="MSTest.Sdk/x.y.z"</c> or <c>global.json</c> <c>msbuild-sdks</c>), not with a <c>PackageReference</c>.
    /// </summary>
    public PackageRule MSTestSdk { get; } = new("MSTest.Sdk", PackageRuleKind.Framework, TestFramework.MSTest, NuGetVersion.Parse("4.0.0"), NuGetVersion.Parse("4.0.2"),
        "MSTest.Sdk 4.0.0 sets MicrosoftTestingPlatformVersion 2.0.0 (3.11 sets 1.9.0).");

    /// <summary>
    /// xUnit v3's MTP variants (<see cref="XUnitV3Packages"/>): the default is MTP v1 in 3.x and MTP v2 from <see cref="XUnitV3DefaultsToMtpV2"/>;
    /// <c>.mtp-v2</c> exists from <see cref="XUnitV3MtpV2Variant"/>; anything older moves to <see cref="XUnitV3UpgradeTo"/>. All of them are xUnit v3.
    /// https://xunit.net/docs/getting-started/v3/microsoft-testing-platform
    /// </summary>
    public NuGetVersion XUnitV3DefaultsToMtpV2 { get; } = NuGetVersion.Parse("4.0.0");

    public NuGetVersion XUnitV3MtpV2Variant { get; } = NuGetVersion.Parse("3.2.0");

    public NuGetVersion XUnitV3UpgradeTo { get; } = NuGetVersion.Parse("3.2.2");

    /// <summary>xUnit v3 does not run on .NET Framework before 4.7.2 (its packages' lowest .NET Framework target).</summary>
    public RuntimeTarget XUnitV3NetFramework { get; } = new(".NETFramework", NuGetVersion.Parse("4.7.2"), "net472");

    /// <summary>The runtimes a test application on <paramref name="framework"/> may target.</summary>
    public IReadOnlyList<RuntimeTarget> RuntimeTargetsFor(TestFramework framework) => framework == TestFramework.XUnitV3
        ? [.. RuntimeTargets.Select(t => t.Identifier == XUnitV3NetFramework.Identifier ? XUnitV3NetFramework : t)]
        : RuntimeTargets;

    public PackageRule? FindPackage(string package) => _packages.GetValueOrDefault(package);

    public VsTestOnlyPackage? FindVsTestOnlyPackage(string package) => _vsTestOnly.GetValueOrDefault(package);

    private static IEnumerable<PackageRule> CurrentPackages()
    {
        // MSTest 4 is the first to depend on MTP 2 (MSTest.TestAdapter 4.0.0 -> Microsoft.Testing.Platform.MSBuild 2.0.0; 3.11 -> 1.9.0).
        // The framework and analyzers have no MTP dependency but must stay on the adapter's version.
        foreach (string package in new[] { "MSTest", "MSTest.TestAdapter", "MSTest.TestFramework", "MSTest.Analyzers" })
        {
            yield return Framework(package, TestFramework.MSTest, "4.0.0", "4.0.2", "MSTest 4.0.0 is the first MSTest on MTP v2 (MSTest.TestAdapter 4.0.0 depends on Microsoft.Testing.Platform.MSBuild 2.0.0); the MSTest packages are versioned together.", MsBuildPropertyNames.MSTestVersion);
        }

        // NUnit3TestAdapter 5.x runs on MTP 1 (5.2.0 -> Microsoft.Testing.Platform.MSBuild 1.9.0), 6.0.0 on MTP 2 (2.0.2).
        yield return Framework("NUnit3TestAdapter", TestFramework.NUnit, "6.0.0", "6.3.1", "NUnit3TestAdapter 6.0.0 is the first on MTP v2 (it depends on Microsoft.Testing.Platform.MSBuild 2.0.2; 5.2.0 on 1.9.0).");

        // The adapter runs NUnit 3 and later; NUnit 2 needs the NUnit 2 adapter, which has no MTP support. Moving to NUnit 3 is a framework migration.
        yield return new PackageRule("NUnit", PackageRuleKind.Framework, TestFramework.NUnit, NuGetVersion.Parse("3.0.0"), null, "NUnit3TestAdapter only runs NUnit 3 and later.");

        // TUnit.Engine 0.71.0 is the first on MTP 2 (0.70.7 -> Microsoft.Testing.Platform 1.9.0). The TUnit packages are versioned together.
        foreach (string package in new[] { "TUnit", "TUnit.Engine", "TUnit.Core", "TUnit.Assertions" })
        {
            yield return Framework(package, TestFramework.TUnit, "0.71.0", "1.45.0", "TUnit.Engine 0.71.0 is the first TUnit on MTP v2 (0.70.7 depends on Microsoft.Testing.Platform 1.9.0); the TUnit packages are versioned together.");
        }

        // The platform and the extensions shipped with it share its version: 1.x is MTP v1, 2.x is MTP v2. MSTest.Sdk defaults each of
        // their versions to MicrosoftTestingPlatformVersion.
        foreach (string package in new[]
        {
            "Microsoft.Testing.Platform",
            "Microsoft.Testing.Platform.MSBuild",
            "Microsoft.Testing.Extensions.AzureDevOpsReport",
            "Microsoft.Testing.Extensions.CrashDump",
            "Microsoft.Testing.Extensions.HangDump",
            "Microsoft.Testing.Extensions.HotReload",
            "Microsoft.Testing.Extensions.Retry",
            "Microsoft.Testing.Extensions.Telemetry",
            "Microsoft.Testing.Extensions.TrxReport",
            "Microsoft.Testing.Extensions.TrxReport.Abstractions",
            "Microsoft.Testing.Extensions.VSTestBridge",
        })
        {
            yield return Extension(package, "2.0.0", "2.5.1", "Versioned with Microsoft.Testing.Platform: 1.x is MTP v1, 2.x is MTP v2.", MsBuildPropertyNames.MicrosoftTestingPlatformVersion);
        }

        yield return Extension("Microsoft.Testing.Extensions.CodeCoverage", "18.1.0", "18.8.0", "18.1.0 is the first on MTP v2 (it depends on Microsoft.Testing.Platform 2.0.0; 18.0.6 on 1.8.4).", MsBuildPropertyNames.MicrosoftTestingExtensionsCodeCoverageVersion);
        yield return Extension("Microsoft.Testing.Extensions.Fakes", "18.1.1", "18.1.1", "MSTest.Sdk 4.0.0 (MTP v2) uses 18.1.1; MSTest.Sdk 3.11 (MTP v1) uses 17.14.1.", MsBuildPropertyNames.MicrosoftTestingExtensionsFakesVersion);
        yield return Extension("coverlet.MTP", "8.0.0", "8.0.0", "Every coverlet.MTP release (8.0.0 and later) is built on MTP v2.");
    }

    // Data collectors run inside VSTest's data collection process; MTP never loads them, so coverage would silently disappear.
    private static IEnumerable<VsTestOnlyPackage> CurrentVsTestOnlyPackages() =>
    [
        new("coverlet.collector", "A VSTest data collector; MTP does not load data collectors. Use coverlet.MTP or Microsoft.Testing.Extensions.CodeCoverage."),
        new("coverlet.msbuild", "Hooks the VSTest target of dotnet test, which the MTP mode of dotnet test does not run. Use coverlet.MTP or Microsoft.Testing.Extensions.CodeCoverage."),
    ];

    private static PackageRule Framework(string package, TestFramework framework, string minimum, string upgradeTo, string evidence, string? sdkVersionProperty = null) =>
        new(package, PackageRuleKind.Framework, framework, NuGetVersion.Parse(minimum), NuGetVersion.Parse(upgradeTo), evidence) { MSTestSdkVersionProperty = sdkVersionProperty };

    private static PackageRule Extension(string package, string minimum, string upgradeTo, string evidence, string? sdkVersionProperty = null) =>
        new(package, PackageRuleKind.Extension, null, NuGetVersion.Parse(minimum), NuGetVersion.Parse(upgradeTo), evidence) { MSTestSdkVersionProperty = sdkVersionProperty };
}
