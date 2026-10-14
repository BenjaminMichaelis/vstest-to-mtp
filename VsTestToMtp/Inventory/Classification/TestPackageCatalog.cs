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
        ["NUnit.Console"] = new(TestPackageRole.Runner, TestFramework.NUnit, null),
        ["xunit"] = new(TestPackageRole.Framework, TestFramework.XUnitV2, null),
        ["xunit.core"] = new(TestPackageRole.Framework, TestFramework.XUnitV2, null),

        // The VSTest adapter serves both xUnit generations, so it is not tied to one framework.
        ["xunit.runner.visualstudio"] = new(TestPackageRole.Runner, null, TestRunnerKind.VSTest),
        ["xunit.v3"] = new(TestPackageRole.FrameworkAndRunner, TestFramework.XUnitV3, null),
        ["xunit.v3.core"] = new(TestPackageRole.Framework, TestFramework.XUnitV3, null),
        ["xunit.v3.mtp-off"] = new(TestPackageRole.FrameworkAndRunner, TestFramework.XUnitV3, TestRunnerKind.VSTest),
        ["xunit.v3.mtp-v1"] = new(TestPackageRole.FrameworkAndRunner, TestFramework.XUnitV3, TestRunnerKind.MicrosoftTestingPlatform),
        ["xunit.v3.mtp-v2"] = new(TestPackageRole.FrameworkAndRunner, TestFramework.XUnitV3, TestRunnerKind.MicrosoftTestingPlatform),
        ["TUnit"] = new(TestPackageRole.FrameworkAndRunner, TestFramework.TUnit, TestRunnerKind.MicrosoftTestingPlatform),
        ["TUnit.Core"] = new(TestPackageRole.Framework, TestFramework.TUnit, null),
        [TestSdkPackage] = new(TestPackageRole.Runner, null, TestRunnerKind.VSTest),
        ["Microsoft.Testing.Platform"] = new(TestPackageRole.Runner, null, TestRunnerKind.MicrosoftTestingPlatform),
        ["Microsoft.Testing.Platform.MSBuild"] = new(TestPackageRole.Runner, null, TestRunnerKind.MicrosoftTestingPlatform),
    };

    public static bool TryGet(string packageName, [NotNullWhen(true)] out TestPackageInfo? info) =>
        Exact.TryGetValue(packageName, out info);

    public static bool IsTestSdk(string packageName) =>
        string.Equals(packageName, TestSdkPackage, StringComparison.OrdinalIgnoreCase);
}
