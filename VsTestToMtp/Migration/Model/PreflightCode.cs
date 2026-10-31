namespace VsTestToMtp.Migration;

/// <summary>Well-known preflight diagnostic codes.</summary>
public enum PreflightCode
{
    /// <summary>The inventory could not determine the selection (not found, ambiguous, MSBuild not locatable, ...).</summary>
    UnresolvedSelection,

    /// <summary>The inventory could not fully determine the project's evaluated state (failed evaluation, missing imports, unresolved conditions, ...).</summary>
    UnresolvedProjectState,

    /// <summary>The project could not be classified, or its target frameworks classify differently.</summary>
    UnclassifiedProject,

    /// <summary>The .NET SDK is older than the rule set requires, or which SDK evaluated a test application could not be determined.</summary>
    UnsupportedSdk,

    /// <summary>
    /// <c>global.json</c> does not select the MTP mode of <c>dotnet test</c> (<c>test.runner</c>) for test applications that run, or will run,
    /// on Microsoft.Testing.Platform v2. A warning: selecting it is part of the migration.
    /// </summary>
    TestRunnerNotSelected,

    /// <summary>The selection would mix VSTest and Microsoft.Testing.Platform test applications in one <c>dotnet test</c> run configuration.</summary>
    MixedTestRunners,

    /// <summary>A test application whose test framework (or framework version) the rules do not migrate.</summary>
    UnsupportedTestFramework,

    /// <summary>xUnit v2 has no Microsoft.Testing.Platform v2 support; moving to xUnit v3 is a framework migration the tool does not perform.</summary>
    XUnitV2NotSupported,

    /// <summary>The project uses more than one test framework.</summary>
    MultipleTestFrameworks,

    /// <summary>A target framework Microsoft.Testing.Platform v2 (or the project's test framework) does not support; retargeting is never automatic.</summary>
    UnsupportedTargetFramework,

    /// <summary>The project supplies its own entry point instead of the generated one.</summary>
    CustomEntryPoint,

    /// <summary>A package version that cannot be resolved offline (missing, floating or an open range), so compatibility cannot be decided.</summary>
    UnresolvableVersion,

    /// <summary>A package or SDK that needs to change is declared outside the repository (by an SDK or another package), so it cannot be edited.</summary>
    PackageNotInRepository,

    /// <summary>
    /// A package MSTest.Sdk declares takes its version from an MSTest.Sdk property (<c>MSTestVersion</c>, ...) that the repository sets
    /// below what Microsoft.Testing.Platform v2 needs; the property is what has to change.
    /// </summary>
    SdkPackageVersionPinned,

    /// <summary>A test framework is present but no runner/adapter package that runs it on Microsoft.Testing.Platform is.</summary>
    MissingRunnerPackage,

    /// <summary>A VSTest-only package (such as a data collector) that does nothing under Microsoft.Testing.Platform.</summary>
    VsTestOnlyPackage,
}
