namespace VsTestToMtp.Inventory;

public enum PropertySource
{
    /// <summary>Defined by an element in a project or imported file.</summary>
    File,

    /// <summary>Provided as a global property by the inventory (or the caller).</summary>
    Global,

    /// <summary>Read from an environment variable.</summary>
    Environment,

    /// <summary>MSBuild reserved or well-known property.</summary>
    Reserved,
}
