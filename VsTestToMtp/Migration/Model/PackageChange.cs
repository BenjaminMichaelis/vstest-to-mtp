using VsTestToMtp.Inventory;

namespace VsTestToMtp.Migration;

/// <summary>
/// A package edit the migration needs, planned before anything is changed. Changes stay within the package's own family: a test
/// framework is never swapped for another one (xUnit v2 is never moved to v3) and no project is retargeted.
/// </summary>
/// <param name="Package">The package (or SDK) as it is referenced today.</param>
/// <param name="FromVersion">Its current version as written (it can be a range).</param>
/// <param name="ToPackage">The package after the change; the same as <paramref name="Package"/> unless <paramref name="Kind"/> is <see cref="PackageChangeKind.Replace"/>.</param>
/// <param name="Reference">Where the package (or SDK) is referenced: the <c>PackageReference</c>, or the element with the <c>Sdk</c> reference.</param>
/// <param name="VersionLocation">
/// Where its version is written: the reference itself (inline <c>Version</c> or <c>VersionOverride</c>), the central <c>PackageVersion</c>,
/// or <c>global.json</c> (<c>msbuild-sdks</c>) for an SDK without a version of its own.
/// </param>
public sealed record PackageChange(
    PackageChangeKind Kind,
    string Package,
    string FromVersion,
    string ToPackage,
    string ToVersion,
    string Reason,
    SourceLocation Reference,
    SourceLocation? VersionLocation);
