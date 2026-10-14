namespace VsTestToMtp.Inventory;

public enum BlockerSeverity
{
    /// <summary>Informational; nothing prevents migration.</summary>
    Info,

    /// <summary>The result may be wrong or environment-dependent.</summary>
    Warning,

    /// <summary>State could not be determined; do not guess.</summary>
    Error,
}
