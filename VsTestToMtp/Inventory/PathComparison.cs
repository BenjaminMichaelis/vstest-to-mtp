namespace VsTestToMtp.Inventory;

/// <summary>
/// Path identity follows the platform: case-insensitive on Windows and macOS, case-sensitive elsewhere, regardless of
/// the actual volume. Callers comparing paths from an <see cref="InventoryResult"/> with their own should apply the same rule.
/// (Output ordering stays case-insensitive-then-ordinal so it is stable everywhere; this is only for identity.)
/// </summary>
public static class PathComparison
{
    /// <summary>Whether the tool treats two paths differing only by case as the same path on this OS.</summary>
    public static bool IsCaseInsensitive { get; } = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    internal static StringComparison Comparison { get; } = IsCaseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    internal static StringComparer Comparer { get; } = IsCaseInsensitive ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    internal static bool Equal(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), Comparison);
}
