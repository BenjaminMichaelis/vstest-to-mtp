using VsTestToMtp.Inventory;

namespace VsTestToMtp.Migration;

/// <summary>The compatibility preflight of an inventory against one rule set. Nothing is changed to produce it.</summary>
/// <param name="RuleSet">The <see cref="MigrationRuleSet.Id"/> the verdicts were made with.</param>
/// <param name="RuleSetVersion">The <see cref="MigrationRuleSet.Version"/> the verdicts were made with.</param>
/// <param name="Projects">One entry per inventoried project, in inventory order.</param>
/// <param name="Diagnostics">Every diagnostic (selection-level and per project), ordered.</param>
/// <remarks>
/// Members that are lists are compared by reference by the generated record equality, so do not rely on <c>==</c> or
/// <c>Equals</c> for this type; compare the serialized output or specific fields.
/// </remarks>
public sealed record PreflightResult(
    string RuleSet,
    int RuleSetVersion,
    IReadOnlyList<ProjectPreflight> Projects,
    IReadOnlyList<PreflightDiagnostic> Diagnostics)
{
    /// <summary>Whether nothing blocks the selection: no selection-level error and no blocked project.</summary>
    public bool CanMigrate => !Diagnostics.Any(d => d.Severity == BlockerSeverity.Error);
}
