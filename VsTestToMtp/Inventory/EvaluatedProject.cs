namespace VsTestToMtp.Inventory;

/// <summary>Raw evaluated state for one project, before classification.</summary>
internal sealed record EvaluatedProject(
    string Path,
    IReadOnlyList<EvaluatedTargetFramework> TargetFrameworks,
    IReadOnlyList<ImportRecord> Imports,
    IReadOnlyList<InventoryBlocker> Blockers)
{
    // Errors that mean MSBuild's picture of the project is incomplete. Other errors (for example a declaration NuGet would reject)
    // do not stop the project from being evaluated, so they must not stop it from being classified.
    private static readonly HashSet<string> IncompleteEvaluationCodes =
    [
        BlockerCodes.EvaluationFailed,
        BlockerCodes.MissingImport,
        BlockerCodes.NoTargetFramework,
    ];

    /// <summary>Whether evaluation was incomplete in a way that makes classification a guess.</summary>
    public bool HasErrors => Blockers.Any(b => b.Severity == BlockerSeverity.Error && IncompleteEvaluationCodes.Contains(b.Code));
}
