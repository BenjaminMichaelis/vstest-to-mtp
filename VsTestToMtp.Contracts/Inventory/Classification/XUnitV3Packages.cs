using System.Diagnostics.CodeAnalysis;

namespace VsTestToMtp.Inventory;

/// <summary>
/// xUnit v3's packages that choose how it runs (https://xunit.net/docs/getting-started/v3/microsoft-testing-platform): <c>xunit.v3</c>
/// and <c>xunit.v3.core</c>, each with no suffix (the default), <c>.mtp-v1</c>, <c>.mtp-v2</c> or <c>.mtp-off</c>. Shared by the MSBuild
/// host (which recognises them as xUnit v3) and the migration rules (which move them between variants), so both agree on the set.
/// </summary>
public static class XUnitV3Packages
{
    public const string MtpOff = ".mtp-off";
    public const string MtpV1 = ".mtp-v1";
    public const string MtpV2 = ".mtp-v2";

    /// <summary>The packages a variant suffix goes on; the more specific one first, so splitting finds the longest match.</summary>
    public static IReadOnlyList<string> Bases { get; } = ["xunit.v3.core", "xunit.v3"];

    /// <summary>The suffixes, starting with the default (none).</summary>
    public static IReadOnlyList<string> Variants { get; } = [string.Empty, MtpOff, MtpV1, MtpV2];

    /// <summary>Splits a package id into its base and variant (<c>xunit.v3.core.mtp-off</c> into <c>xunit.v3.core</c> and <c>.mtp-off</c>).</summary>
    public static bool TrySplit(string package, [NotNullWhen(true)] out string? baseName, [NotNullWhen(true)] out string? variant)
    {
        foreach (string candidate in Bases)
        {
            if (package.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)
                && Variants.FirstOrDefault(v => string.Equals(package[candidate.Length..], v, StringComparison.OrdinalIgnoreCase)) is { } suffix)
            {
                baseName = candidate;
                variant = suffix;
                return true;
            }
        }

        baseName = null;
        variant = null;
        return false;
    }
}
