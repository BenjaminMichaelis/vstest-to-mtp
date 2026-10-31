namespace VsTestToMtp.Inventory;

/// <summary>
/// A position in a file. <see cref="File"/> is relative to the inventory root with <c>/</c> separators
/// when the file lives under it, otherwise an absolute path with <c>/</c> separators.
/// </summary>
public sealed record SourceLocation(string File, int Line, int Column)
{
    /// <summary>A whole file (no line or column), for a finding about the file as such, such as a project or global.json.</summary>
    public static SourceLocation OfFile(string file) => new(file, 0, 0);

    public override string ToString() => $"{File}({Line},{Column})";
}
