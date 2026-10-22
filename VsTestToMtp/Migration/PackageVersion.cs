using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace VsTestToMtp.Migration;

/// <summary>
/// A NuGet package version (<c>major[.minor[.patch[.revision]]][-prerelease][+metadata]</c>), compared the way NuGet compares them:
/// numerically by part, a pre-release before its release, pre-release labels by dot-separated identifier, metadata ignored.
/// </summary>
public sealed class PackageVersion : IComparable<PackageVersion>, IEquatable<PackageVersion>
{
    private readonly int[] _parts;

    private PackageVersion(int[] parts, string release, string original)
    {
        _parts = parts;
        Release = release;
        Original = original;
    }

    public int Major => _parts[0];

    /// <summary>The pre-release label without the leading <c>-</c>; empty for a release.</summary>
    public string Release { get; }

    public bool IsPrerelease => Release.Length > 0;

    /// <summary>The text the version was parsed from.</summary>
    public string Original { get; }

    public static PackageVersion Parse(string value) =>
        TryParse(value, out PackageVersion? version) ? version : throw new FormatException($"'{value}' is not a NuGet version.");

    public static bool TryParse(string? value, [NotNullWhen(true)] out PackageVersion? version)
    {
        version = null;
        string text = value?.Trim() ?? string.Empty;
        int plus = text.IndexOf('+', StringComparison.Ordinal);
        string withoutMetadata = plus < 0 ? text : text[..plus];
        int dash = withoutMetadata.IndexOf('-', StringComparison.Ordinal);
        string numbers = dash < 0 ? withoutMetadata : withoutMetadata[..dash];
        string release = dash < 0 ? string.Empty : withoutMetadata[(dash + 1)..];

        string[] parts = numbers.Split('.');
        if (parts.Length is < 1 or > 4 || (dash >= 0 && release.Length == 0))
        {
            return false;
        }

        int[] parsed = new int[4];
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length == 0 || !parts[i].All(char.IsAsciiDigit)
                || !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out parsed[i]))
            {
                return false;
            }
        }

        version = new PackageVersion(parsed, release, text);
        return true;
    }

    public int CompareTo(PackageVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        for (int i = 0; i < _parts.Length; i++)
        {
            int byPart = _parts[i].CompareTo(other._parts[i]);
            if (byPart != 0)
            {
                return byPart;
            }
        }

        return (IsPrerelease, other.IsPrerelease) switch
        {
            (false, false) => 0,
            (true, false) => -1,
            (false, true) => 1,
            _ => CompareRelease(Release, other.Release),
        };
    }

    // SemVer 2.0: identifiers compare numerically when both are numeric, otherwise ordinally (case-insensitively, as NuGet does),
    // a numeric identifier sorts before an alphanumeric one, and a shorter list of otherwise equal identifiers sorts first.
    private static int CompareRelease(string left, string right)
    {
        string[] a = left.Split('.');
        string[] b = right.Split('.');
        for (int i = 0; i < Math.Min(a.Length, b.Length); i++)
        {
            bool aNumeric = int.TryParse(a[i], NumberStyles.None, CultureInfo.InvariantCulture, out int aNumber);
            bool bNumeric = int.TryParse(b[i], NumberStyles.None, CultureInfo.InvariantCulture, out int bNumber);
            int result = (aNumeric, bNumeric) switch
            {
                (true, true) => aNumber.CompareTo(bNumber),
                (true, false) => -1,
                (false, true) => 1,
                _ => StringComparer.OrdinalIgnoreCase.Compare(a[i], b[i]),
            };
            if (result != 0)
            {
                return result;
            }
        }

        return a.Length.CompareTo(b.Length);
    }

    public bool Equals(PackageVersion? other) => CompareTo(other) == 0;

    public override bool Equals(object? obj) => obj is PackageVersion other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(_parts[0], _parts[1], _parts[2], _parts[3], Release.ToUpperInvariant());

    public override string ToString() => Original;

    public static bool operator ==(PackageVersion? left, PackageVersion? right) => left is null ? right is null : left.Equals(right);

    public static bool operator !=(PackageVersion? left, PackageVersion? right) => !(left == right);

    public static bool operator <(PackageVersion? left, PackageVersion? right) => left is null ? right is not null : left.CompareTo(right) < 0;

    public static bool operator >(PackageVersion? left, PackageVersion? right) => right < left;

    public static bool operator <=(PackageVersion? left, PackageVersion? right) => !(left > right);

    public static bool operator >=(PackageVersion? left, PackageVersion? right) => !(left < right);

    /// <summary>
    /// The version NuGet resolves a <c>PackageReference</c>/<c>PackageVersion</c> <paramref name="value"/> to when that version exists:
    /// the lower bound, because NuGet picks the lowest version that satisfies the range. A bare version (<c>1.2.3</c>) means
    /// "1.2.3 or higher" and an inclusive range (<c>[1.2.3]</c>, <c>[1.2.3,2.0)</c>, <c>[1.2.3,)</c>) starts at its lower bound.
    /// A floating version (<c>1.*</c>), an exclusive lower bound or a range without one depends on what the feed contains, so it is
    /// not resolved: compatibility must not be guessed from it.
    /// </summary>
    public static bool TryResolveMinimum(string? value, [NotNullWhen(true)] out PackageVersion? version)
    {
        version = null;
        string text = value?.Trim() ?? string.Empty;
        if (text is "" || text.Contains('*', StringComparison.Ordinal))
        {
            return false;
        }

        if (text is not ['[' or '(', ..])
        {
            return TryParse(text, out version);
        }

        if (text is not ['[', .., ']' or ')'])
        {
            return false;
        }

        string inner = text[1..^1];
        int comma = inner.IndexOf(',', StringComparison.Ordinal);

        // "[1.2.3]" is exact, so it must close with ']'; "[1.2.3,...)" starts at 1.2.3 whatever the upper bound is.
        if (comma < 0 && text is not [.., ']'])
        {
            return false;
        }

        string lower = comma < 0 ? inner : inner[..comma];
        return TryParse(lower, out version);
    }
}
