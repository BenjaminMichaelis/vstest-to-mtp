namespace VsTestToMtp.Inventory;

/// <summary>
/// The MSBuild properties the MSBuild host captures for every target framework (<see cref="TargetFrameworkState.Properties"/>).
/// Code that reads an inventory names a property through these constants, so it can only ask for one the host captured: a property that is
/// not captured would read as unset, which is indistinguishable from a project that does not set it.
/// </summary>
public static class MsBuildPropertyNames
{
    public const string CentralPackageVersionOverrideEnabled = "CentralPackageVersionOverrideEnabled";
    public const string EnableMSTestRunner = "EnableMSTestRunner";
    public const string EnableNUnitRunner = "EnableNUnitRunner";
    public const string EnableTUnitRunner = "EnableTUnitRunner";
    public const string GenerateProgramFile = "GenerateProgramFile";
    public const string GenerateTestingPlatformEntryPoint = "GenerateTestingPlatformEntryPoint";
    public const string IsPackable = "IsPackable";
    public const string IsTestProject = "IsTestProject";
    public const string ManagePackageVersionsCentrally = "ManagePackageVersionsCentrally";
    public const string MicrosoftTestingExtensionsCodeCoverageVersion = "MicrosoftTestingExtensionsCodeCoverageVersion";
    public const string MicrosoftTestingExtensionsFakesVersion = "MicrosoftTestingExtensionsFakesVersion";
    public const string MicrosoftTestingPlatformVersion = "MicrosoftTestingPlatformVersion";
    public const string MSTestVersion = "MSTestVersion";
    public const string NETCoreSdkVersion = "NETCoreSdkVersion";
    public const string OutputType = "OutputType";
    public const string StartupObject = "StartupObject";
    public const string TargetFramework = "TargetFramework";
    public const string TargetFrameworkIdentifier = "TargetFrameworkIdentifier";
    public const string TargetFrameworks = "TargetFrameworks";
    public const string TargetFrameworkVersion = "TargetFrameworkVersion";
    public const string TestingPlatformDotnetTestSupport = "TestingPlatformDotnetTestSupport";
    public const string UseMicrosoftTestingPlatformRunner = "UseMicrosoftTestingPlatformRunner";
    public const string UseVSTest = "UseVSTest";
    public const string UsingMSTestSdk = "UsingMSTestSdk";

    /// <summary>
    /// Every property above, in ordinal order: what the host captures. Classification reads <see cref="IsTestProject"/>; the migration
    /// preflight reads the runtime target, the SDK version, MSTest.Sdk's state and versions, and the entry-point properties.
    /// </summary>
    public static IReadOnlyList<string> Captured { get; } =
    [
        CentralPackageVersionOverrideEnabled,
        EnableMSTestRunner,
        EnableNUnitRunner,
        EnableTUnitRunner,
        GenerateProgramFile,
        GenerateTestingPlatformEntryPoint,
        IsPackable,
        IsTestProject,
        ManagePackageVersionsCentrally,
        MicrosoftTestingExtensionsCodeCoverageVersion,
        MicrosoftTestingExtensionsFakesVersion,
        MicrosoftTestingPlatformVersion,
        MSTestVersion,
        NETCoreSdkVersion,
        OutputType,
        StartupObject,
        TargetFramework,
        TargetFrameworkIdentifier,
        TargetFrameworks,
        TargetFrameworkVersion,
        TestingPlatformDotnetTestSupport,
        UseMicrosoftTestingPlatformRunner,
        UseVSTest,
        UsingMSTestSdk,
    ];
}
