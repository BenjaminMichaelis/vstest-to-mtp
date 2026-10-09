namespace VsTestToMtp.Inventory;

/// <summary>
/// Where an evaluated value or item was declared, so later edits target the right file.
/// </summary>
/// <param name="Location">The declaring element.</param>
/// <param name="Conditions">Conditions guarding the element, outermost first (element and its ancestors).</param>
/// <param name="ImportChain">Imports from the project file down to the declaring file (empty when declared in the project file).</param>
/// <param name="IsFromProjectFile">Whether the element lives in the evaluated project file itself.</param>
/// <param name="IsRepositoryFile">Whether the declaring file is user-owned (not part of the .NET SDK or a NuGet package).</param>
public sealed record Provenance(
    SourceLocation Location,
    IReadOnlyList<string> Conditions,
    IReadOnlyList<ImportStep> ImportChain,
    bool IsFromProjectFile,
    bool IsRepositoryFile);
