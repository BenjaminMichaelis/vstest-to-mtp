namespace VsTestToMtp.Inventory;

/// <summary>
/// One <c>&lt;Import&gt;</c> hop between the evaluated project file and the file that holds an element.
/// </summary>
/// <param name="ImportedBy">Location of the importing element (or the <c>Sdk</c> attribute for implicit SDK imports).</param>
/// <param name="Condition">The import element's own <c>Condition</c>, if any.</param>
public sealed record ImportStep(SourceLocation ImportedBy, string? Condition);
