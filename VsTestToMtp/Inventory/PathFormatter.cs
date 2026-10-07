namespace VsTestToMtp.Inventory;

/// <summary>
/// Formats absolute paths relative to the inventory root with <c>/</c> separators so results are stable across machines.
/// Paths outside the root stay absolute.
/// </summary>
internal sealed class PathFormatter(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string Format(string path)
    {
        string full = Path.GetFullPath(path);
        string relative = Path.GetRelativePath(Root, full);
        bool isInside = !Path.IsPathRooted(relative)
            && relative != ".."
            && !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            && !relative.StartsWith(".." + Path.AltDirectorySeparatorChar, StringComparison.Ordinal);

        return (isInside ? relative : full).Replace('\\', '/');
    }

    public SourceLocation Location(string path, int line = 0, int column = 0) => new(Format(path), line, column);
}
