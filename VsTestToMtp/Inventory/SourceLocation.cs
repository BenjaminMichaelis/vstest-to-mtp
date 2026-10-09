namespace VsTestToMtp.Inventory;

/// <summary>
/// A position in a file. <see cref="File"/> is relative to the inventory root with <c>/</c> separators
/// when the file lives under it, otherwise an absolute path with <c>/</c> separators.
/// </summary>
public sealed record SourceLocation(string File, int Line, int Column)
{
    public override string ToString() => $"{File}({Line},{Column})";
}
