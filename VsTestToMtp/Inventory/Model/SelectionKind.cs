namespace VsTestToMtp.Inventory;

/// <summary>What kind of path the caller selected.</summary>
public enum SelectionKind
{
    /// <summary>The path did not exist or is not a supported kind.</summary>
    Unknown,

    /// <summary>A single project file.</summary>
    Project,

    /// <summary>A <c>.sln</c> solution.</summary>
    Sln,

    /// <summary>A <c>.slnx</c> solution.</summary>
    Slnx,

    /// <summary>A directory that did not resolve to exactly one solution or project.</summary>
    Directory,
}
