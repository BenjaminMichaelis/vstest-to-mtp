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

/// <summary>Known test packages. Package evidence is only one input to classification.</summary>
internal static class TestPackageCatalog
{
    public const string TestSdkPackage = "Microsoft.NET.Test.Sdk";

    private sealed record Entry(TestPackageRole Role, string Framework);

    private static readonly Dictionary<string, Entry> Exact = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MSTest"] = new(TestPackageRole.FrameworkAndRunner, "MSTest"),
        ["MSTest.TestFramework"] = new(TestPackageRole.Framework, "MSTest"),
        ["MSTest.TestAdapter"] = new(TestPackageRole.Runner, "MSTest"),
        ["NUnit"] = new(TestPackageRole.Framework, "NUnit"),
        ["NUnit3TestAdapter"] = new(TestPackageRole.Runner, "NUnit"),
        ["NUnit.Console"] = new(TestPackageRole.Runner, "NUnit"),
        ["xunit"] = new(TestPackageRole.Framework, "xUnit v2"),
        ["xunit.core"] = new(TestPackageRole.Framework, "xUnit v2"),
        ["xunit.runner.visualstudio"] = new(TestPackageRole.Runner, "xUnit"),
        ["xunit.v3"] = new(TestPackageRole.FrameworkAndRunner, "xUnit v3"),
        ["xunit.v3.core"] = new(TestPackageRole.Framework, "xUnit v3"),
        ["xunit.v3.mtp-off"] = new(TestPackageRole.FrameworkAndRunner, "xUnit v3"),
        ["xunit.v3.mtp-v1"] = new(TestPackageRole.FrameworkAndRunner, "xUnit v3"),
        ["xunit.v3.mtp-v2"] = new(TestPackageRole.FrameworkAndRunner, "xUnit v3"),
        ["TUnit"] = new(TestPackageRole.FrameworkAndRunner, "TUnit"),
        ["TUnit.Core"] = new(TestPackageRole.Framework, "TUnit"),
        [TestSdkPackage] = new(TestPackageRole.Runner, "VSTest"),
        ["Microsoft.Testing.Platform"] = new(TestPackageRole.Runner, "Microsoft.Testing.Platform"),
        ["Microsoft.Testing.Platform.MSBuild"] = new(TestPackageRole.Runner, "Microsoft.Testing.Platform"),
    };

    public static bool TryGet(string packageName, out TestPackageRole role, out string framework)
    {
        if (Exact.TryGetValue(packageName, out Entry? entry))
        {
            role = entry.Role;
            framework = entry.Framework;
            return true;
        }

        role = default;
        framework = string.Empty;
        return false;
    }

    public static bool IsTestSdk(string packageName) =>
        string.Equals(packageName, TestSdkPackage, StringComparison.OrdinalIgnoreCase);
}
