namespace VsTestToMtp.Inventory;

/// <summary>An evaluated MSBuild property for one target framework.</summary>
/// <param name="Definition">The winning definition; <see langword="null"/> unless <see cref="Source"/> is <see cref="PropertySource.File"/>.</param>
/// <param name="Overridden">Earlier definitions that the winning one replaced, oldest first.</param>
public sealed record PropertyState(
    string Name,
    string Value,
    PropertySource Source,
    Provenance? Definition,
    IReadOnlyList<Provenance> Overridden);
