using VsTestToMtp.Inventory;

namespace VsTestToMtp.Migration;

/// <summary>The preflight verdict for one inventoried project.</summary>
/// <param name="Path">The project path, as in <see cref="ProjectInventory.Path"/>.</param>
/// <param name="Framework">The test framework the rules matched; <see langword="null"/> when there is none or more than one.</param>
/// <param name="UsesMSTestSdk">Whether the project uses <c>MSTest.Sdk</c>, whose opt-out from Microsoft.Testing.Platform is <c>UseVSTest</c>.</param>
/// <param name="PackageChanges">
/// In-family package edits the migration needs, ordered by package and location. A blocked project lists the ones it would also need,
/// so they can be seen together with what blocks it; only an <see cref="MigrationReadiness.Eligible"/> project is migrated.
/// </param>
/// <param name="Diagnostics">Everything found about the project, ordered; any error makes it <see cref="MigrationReadiness.Blocked"/>.</param>
/// <remarks>
/// Members that are lists are compared by reference by the generated record equality, so do not rely on <c>==</c> or
/// <c>Equals</c> for this type; compare the serialized output or specific fields.
/// </remarks>
public sealed record ProjectPreflight(
    string Path,
    MigrationReadiness Readiness,
    TestFramework? Framework,
    bool UsesMSTestSdk,
    IReadOnlyList<PackageChange> PackageChanges,
    IReadOnlyList<PreflightDiagnostic> Diagnostics);
