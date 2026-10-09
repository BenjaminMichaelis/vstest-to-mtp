namespace VsTestToMtp.Inventory;

/// <summary>
/// Reads boolean properties the way NuGet's restore does: only the literal <c>true</c> or <c>false</c> (trimmed,
/// case-insensitive) counts. MSBuild's other spellings (<c>yes</c>, <c>off</c>, ...) are neither.
/// </summary>
internal static class NuGetBoolean
{
    // https://github.com/NuGet/NuGet.Client/blob/b337f5b80d3363a61f773f2e1c3757526a474610/src/NuGet.Core/NuGet.Commands/RestoreCommand/Utility/PackageSpecFactory.cs#L941-L962
    public static bool IsTrue(string? value) => string.Equals(value?.Trim(), bool.TrueString, StringComparison.OrdinalIgnoreCase);

    public static bool IsFalse(string? value) => string.Equals(value?.Trim(), bool.FalseString, StringComparison.OrdinalIgnoreCase);
}
