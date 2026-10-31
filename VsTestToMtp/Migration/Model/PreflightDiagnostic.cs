using VsTestToMtp.Inventory;

namespace VsTestToMtp.Migration;

/// <summary>An explicit finding of the compatibility preflight. Errors block; warnings are reported but do not.</summary>
/// <param name="Project">The project it is about; <see langword="null"/> for the whole selection.</param>
public sealed record PreflightDiagnostic(
    PreflightCode Code,
    BlockerSeverity Severity,
    string Message,
    string Remediation,
    string? Project,
    SourceLocation? Location)
{
    /// <summary>Other places the diagnostic is about (for example each project of a mixed run configuration). Empty when there are none.</summary>
    public IReadOnlyList<SourceLocation> RelatedLocations { get; init; } = [];

    internal static PreflightDiagnostic Error(PreflightCode code, string message, string remediation, string? project = null, SourceLocation? location = null) =>
        new(code, BlockerSeverity.Error, message, remediation, project, location);

    internal static PreflightDiagnostic Warning(PreflightCode code, string message, string remediation, string? project = null, SourceLocation? location = null) =>
        new(code, BlockerSeverity.Warning, message, remediation, project, location);

    /// <summary>Removes duplicates and orders by project, code, location and message, so output never depends on evaluation order.</summary>
    internal static List<PreflightDiagnostic> Normalize(IEnumerable<PreflightDiagnostic> diagnostics) =>
        [.. diagnostics
            .DistinctBy(d => (d.Code, d.Project, d.Location, d.Message))
            .OrderBy(d => d.Project, StringComparer.Ordinal)
            .ThenBy(d => d.Code.ToString(), StringComparer.Ordinal)
            .ThenBy(d => d.Location?.File, StringComparer.Ordinal)
            .ThenBy(d => d.Location?.Line)
            .ThenBy(d => d.Message, StringComparer.Ordinal)];
}
