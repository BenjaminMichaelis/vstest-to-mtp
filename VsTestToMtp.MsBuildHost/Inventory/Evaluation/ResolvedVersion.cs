namespace VsTestToMtp.Inventory;

/// <summary>The outcome of resolving a package reference's version.</summary>
/// <param name="Version">The effective version; <see langword="null"/> when none is effective.</param>
/// <param name="Source">Where <paramref name="Version"/> comes from.</param>
/// <param name="Definition">Where that value is declared; <see langword="null"/> when no version is effective.</param>
internal readonly record struct ResolvedVersion(string? Version, PackageVersionSource Source, Provenance? Definition)
{
    /// <summary>No version is effective.</summary>
    public static ResolvedVersion None { get; } = new(null, PackageVersionSource.None, null);
}
