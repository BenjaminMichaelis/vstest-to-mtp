namespace VsTestToMtp.Inventory;

/// <summary>
/// Path identity follows the platform: case-insensitive on Windows and macOS, case-sensitive elsewhere.
/// (Output ordering stays case-insensitive-then-ordinal so it is stable everywhere; this is only for identity.)
/// </summary>
internal static class PathComparison
{
    public static bool IsCaseInsensitive { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    public static StringComparison Comparison { get; } = IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer { get; } = IsCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static bool Equal(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), Comparison);
}
