namespace VsTestToMtp.Inventory;

/// <summary>Raw evaluated state for one project, before classification.</summary>
internal sealed record EvaluatedProject(
    string Path,
    IReadOnlyList<EvaluatedTargetFramework> TargetFrameworks,
    IReadOnlyList<ProjectReferenceState> ProjectReferences,
    IReadOnlyList<ImportRecord> Imports,
    IReadOnlyList<InventoryBlocker> Blockers)
{
    /// <summary>Whether evaluation was incomplete in a way that makes classification a guess.</summary>
    public bool HasErrors => Blockers.Any(b => b.Severity == BlockerSeverity.Error);
}
