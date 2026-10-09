namespace VsTestToMtp.Inventory;

/// <summary>
/// Parses booleans the way MSBuild conditions do. Use this only for properties MSBuild itself interprets (such as
/// <c>IsTestProject</c>); NuGet reads its own properties differently (see <see cref="NuGetBoolean"/>).
/// </summary>
internal static class MsBuildBoolean
{
    // MSBuild accepts exactly these spellings, case-insensitively, with no trimming:
    // https://github.com/dotnet/msbuild/blob/74878b50aab1c07a7cdcaa15f28115768388cdcc/src/Framework/Utilities/ConversionUtilities.cs#L101-L123
    private static readonly HashSet<string> TrueSpellings = new(StringComparer.OrdinalIgnoreCase) { "true", "on", "yes", "!false", "!off", "!no" };
    private static readonly HashSet<string> FalseSpellings = new(StringComparer.OrdinalIgnoreCase) { "false", "off", "no", "!true", "!on", "!yes" };

    public static bool TryParse(string? value, out bool result)
    {
        result = value is not null && TrueSpellings.Contains(value);
        return result || (value is not null && FalseSpellings.Contains(value));
    }
}
