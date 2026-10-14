namespace VsTestToMtp.Inventory;

/// <summary>Well-known blocker codes.</summary>
public enum BlockerCode
{
    SelectionNotFound,
    UnsupportedSelection,
    AmbiguousSelection,
    MalformedSolution,
    MissingProject,
    UnsupportedProjectLanguage,
    MsBuildNotFound,
    MsBuildSdkMismatch,
    MsBuildSdkUnsupported,
    MsBuildSdkNewerThanRuntime,
    UnreadableDirectory,
    EvaluationFailed,
    MissingImport,
    NoTargetFramework,
    MalformedGlobalJson,
    UnreadableGlobalJson,
    InvalidIsTestProject,
    ConditionDependsOnUnsetProperty,
    ConditionDependsOnEnvironment,
    IsTestProjectEarlyCondition,
    UnresolvedPackageVersion,
    DuplicatePackageVersion,
    IneffectiveVersionOverride,
    InlineVersionUnderCentralManagement,
    TestEvidenceOnlyFromImports,
    TargetFrameworkClassificationDiffers,
}
