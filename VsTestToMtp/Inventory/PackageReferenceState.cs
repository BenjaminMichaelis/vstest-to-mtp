namespace VsTestToMtp.Inventory;

/// <summary>An evaluated <c>PackageReference</c> (or <c>GlobalPackageReference</c>) for one target framework.</summary>
/// <param name="Definition">Where the reference is declared.</param>
/// <param name="Modifiers"><c>Update</c>/<c>Remove</c> operations that also touched the reference.</param>
/// <param name="VersionDefinition">Where the effective version is declared (the reference itself, or the central <c>PackageVersion</c>).</param>
public sealed record PackageReferenceState(
    string Name,
    string? Version,
    PackageVersionSource VersionSource,
    bool IsGlobal,
    Provenance Definition,
    IReadOnlyList<Provenance> Modifiers,
    Provenance? VersionDefinition);
