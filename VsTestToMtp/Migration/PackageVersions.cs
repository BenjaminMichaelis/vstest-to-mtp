using System.Diagnostics.CodeAnalysis;

using NuGet.Versioning;

namespace VsTestToMtp.Migration;

/// <summary>Reads package versions the way NuGet does (<see cref="VersionRange"/>), without a feed.</summary>
public static class PackageVersions
{
    /// <summary>
    /// The version NuGet resolves a <c>PackageReference</c>/<c>PackageVersion</c> <paramref name="value"/> to when that version exists:
    /// the lower bound, because NuGet picks the lowest version that satisfies the range. A bare version (<c>1.2.3</c>) means
    /// "1.2.3 or higher" and an inclusive range (<c>[1.2.3]</c>, <c>[1.2.3,2.0)</c>, <c>[1.2.3,)</c>) starts at its lower bound.
    /// A floating version (<c>1.*</c>), an exclusive lower bound or a range without one depends on what the feed contains, so it is
    /// not resolved: compatibility must not be guessed from it.
    /// </summary>
    public static bool TryResolveMinimum(string? value, [NotNullWhen(true)] out NuGetVersion? version)
    {
        version = VersionRange.TryParse(value?.Trim() ?? string.Empty, allowFloating: true, out VersionRange? range)
            && range is { IsFloating: false, HasLowerBound: true, IsMinInclusive: true }
            ? range.MinVersion
            : null;
        return version is not null;
    }
}
