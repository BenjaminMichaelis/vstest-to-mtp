namespace VsTestToMtp.Inventory;

/// <summary>A <c>ProjectReference</c> found in the evaluated project.</summary>
/// <param name="Path">Referenced project path (same formatting as <see cref="SourceLocation.File"/>).</param>
/// <param name="IsInSelection">Whether the referenced project is part of the inventoried selection.</param>
public sealed record ProjectReferenceState(string Path, bool IsInSelection, Provenance Definition);
