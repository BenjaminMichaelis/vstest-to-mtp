namespace VsTestToMtp.Inventory;

/// <summary>A file imported while evaluating a project.</summary>
/// <param name="IsImplicit">Whether the import comes from the SDK rather than an explicit <c>&lt;Import&gt;</c> in a repository file.</param>
/// <param name="IsRepositoryFile">Whether the imported file is user-owned (not part of the .NET SDK or a NuGet package).</param>
public sealed record ImportRecord(
    string File,
    ImportKind Kind,
    bool IsImplicit,
    bool IsRepositoryFile,
    SourceLocation ImportedBy,
    string? Condition);
