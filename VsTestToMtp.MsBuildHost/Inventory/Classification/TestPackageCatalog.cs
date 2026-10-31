using System.Diagnostics.CodeAnalysis;

namespace VsTestToMtp.Inventory;

internal enum TestPackageRole
{
    /// <summary>A test framework (provides the attributes/assertions tests are written with).</summary>
    Framework,

    /// <summary>A runner or adapter that lets a runner discover and execute the framework's tests.</summary>
    Runner,

    /// <summary>A package that pulls in both a framework and a runner.</summary>
    FrameworkAndRunner,
}

/// <summary>What a known test package is.</summary>
/// <param name="Framework">The framework the package provides or adapts; <see langword="null"/> for a runner that is not tied to one framework.</param>
/// <param name="Runner">The platform the package runs tests on; <see langword="null"/> when it does not say (a bare framework package).</param>
internal sealed record TestPackageInfo(TestPackageRole Role, TestFramework? Framework, TestRunnerKind? Runner);

/// <summary>Known test packages. Package evidence is only one input to classification.</summary>
internal static class TestPackageCatalog
{
    public const string TestSdkPackage = "Microsoft.NET.Test.Sdk";

    private static readonly Dictionary<string, TestPackageInfo> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MSTest"] = new(TestPackageRole.FrameworkAndRunner, TestFramework.MSTest, TestRunnerKind.VSTest),
        ["MSTest.TestFramework"] = new(TestPackageRole.Framework, TestFramework.MSTest, null),
        ["MSTest.TestAdapter"] = new(TestPackageRole.Runner, TestFramework.MSTest, TestRunnerKind.VSTest),
        ["NUnit"] = new(TestPackageRole.Framework, TestFramework.NUnit, null),
        ["NUnit3TestAdapter"] = new(TestPackageRole.Runner, TestFramework.NUnit, TestRunnerKind.VSTest),

        // The NUnit 2 adapter; the migration preflight blocks it (NUnit3TestAdapter, the only adapter with MTP support, does not run NUnit 2).
        ["NUnitTestAdapter"] = new(TestPackageRole.Runner, TestFramework.NUnit, TestRunnerKind.VSTest),
        ["NUnit.Console"] = new(TestPackageRole.Runner, TestFramework.NUnit, null),
        ["xunit"] = new(TestPackageRole.Framework, TestFramework.XUnitV2, null),
        ["xunit.core"] = new(TestPackageRole.Framework, TestFramework.XUnitV2, null),

        // The VSTest adapter serves both xUnit generations, so it is not tied to one framework.
        ["xunit.runner.visualstudio"] = new(TestPackageRole.Runner, null, TestRunnerKind.VSTest),
        ["TUnit"] = new(TestPackageRole.FrameworkAndRunner, TestFramework.TUnit, TestRunnerKind.MicrosoftTestingPlatform),
        ["TUnit.Core"] = new(TestPackageRole.Framework, TestFramework.TUnit, null),
        ["TUnit.Engine"] = new(TestPackageRole.Runner, TestFramework.TUnit, TestRunnerKind.MicrosoftTestingPlatform),
        [TestSdkPackage] = new(TestPackageRole.Runner, null, TestRunnerKind.VSTest),
        ["Microsoft.Testing.Platform"] = new(TestPackageRole.Runner, null, TestRunnerKind.MicrosoftTestingPlatform),
        ["Microsoft.Testing.Platform.MSBuild"] = new(TestPackageRole.Runner, null, TestRunnerKind.MicrosoftTestingPlatform),
    };

    // xUnit v3's packages: every variant runs on MTP except .mtp-off, and the default says nothing about which. xunit.v3.core without
    // a suffix carries no runner of its own in this catalog (it is what the other variants build on); every other one brings one.
    private static readonly Dictionary<string, TestPackageInfo> XUnitV3 = XUnitV3Packages.Bases
        .SelectMany(baseName => XUnitV3Packages.Variants.Select(variant => (Id: baseName + variant, BaseName: baseName, Variant: variant)))
        .ToDictionary(
            p => p.Id,
            p => new TestPackageInfo(
                p is { BaseName: "xunit.v3.core", Variant: "" } ? TestPackageRole.Framework : TestPackageRole.FrameworkAndRunner,
                TestFramework.XUnitV3,
                p.Variant switch
                {
                    "" => null,
                    XUnitV3Packages.MtpOff => TestRunnerKind.VSTest,
                    _ => TestRunnerKind.MicrosoftTestingPlatform,
                }),
            StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string packageName, [NotNullWhen(true)] out TestPackageInfo? info) =>
        Exact.TryGetValue(packageName, out info) || XUnitV3.TryGetValue(packageName, out info);

    public static bool IsTestSdk(string packageName) =>
        string.Equals(packageName, TestSdkPackage, StringComparison.OrdinalIgnoreCase);
}
