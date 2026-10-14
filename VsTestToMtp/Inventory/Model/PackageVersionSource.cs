namespace VsTestToMtp.Inventory;

public enum PackageVersionSource
{
    /// <summary>No version could be resolved.</summary>
    None,

    /// <summary><c>Version</c> metadata on the <c>PackageReference</c>.</summary>
    Inline,

    /// <summary><c>VersionOverride</c> metadata on the <c>PackageReference</c> (Central Package Management).</summary>
    VersionOverride,

    /// <summary>A <c>PackageVersion</c> item (Central Package Management).</summary>
    Central,
}
