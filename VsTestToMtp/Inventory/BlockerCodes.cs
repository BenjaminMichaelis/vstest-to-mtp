namespace VsTestToMtp.Inventory;

/// <summary>Well-known blocker codes.</summary>
public static class BlockerCodes
{
    public const string SelectionNotFound = "SelectionNotFound";
    public const string UnsupportedSelection = "UnsupportedSelection";
    public const string AmbiguousSelection = "AmbiguousSelection";
    public const string MalformedSolution = "MalformedSolution";
    public const string MissingProject = "MissingProject";
    public const string UnsupportedProjectLanguage = "UnsupportedProjectLanguage";
    public const string MsBuildNotFound = "MsBuildNotFound";
    public const string MsBuildSdkMismatch = "MsBuildSdkMismatch";
    public const string MsBuildSdkUnsupported = "MsBuildSdkUnsupported";
    public const string UnreadableDirectory = "UnreadableDirectory";
    public const string EvaluationFailed = "EvaluationFailed";
    public const string MissingImport = "MissingImport";
    public const string NoTargetFramework = "NoTargetFramework";
    public const string MalformedGlobalJson = "MalformedGlobalJson";
    public const string UnreadableGlobalJson = "UnreadableGlobalJson";
    public const string InvalidIsTestProject = "InvalidIsTestProject";
    public const string ConditionDependsOnUnsetProperty = "ConditionDependsOnUnsetProperty";
    public const string ConditionDependsOnEnvironment = "ConditionDependsOnEnvironment";
    public const string IsTestProjectEarlyCondition = "IsTestProjectEarlyCondition";
    public const string UnresolvedPackageVersion = "UnresolvedPackageVersion";
    public const string DuplicatePackageVersion = "DuplicatePackageVersion";
    public const string IneffectiveVersionOverride = "IneffectiveVersionOverride";
    public const string InlineVersionUnderCentralManagement = "InlineVersionUnderCentralManagement";
    public const string TestEvidenceOnlyFromImports = "TestEvidenceOnlyFromImports";
    public const string TargetFrameworkClassificationDiffers = "TargetFrameworkClassificationDiffers";
}
