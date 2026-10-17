namespace VsTestToMtp.Inventory;

/// <summary>Which .NET runtime the MSBuild host process runs on.</summary>
public enum MsBuildHostRuntime
{
    /// <summary>
    /// The machine's installed runtime when it is at least as new as the one bundled with the tool (or nothing is bundled), otherwise the
    /// bundled runtime. A runtime that cannot start falls back to the other one when both are available.
    /// </summary>
    Auto,

    /// <summary>Only the runtime installed on the machine; never the bundled one.</summary>
    Machine,

    /// <summary>Only the runtime bundled with the tool; never the one installed on the machine.</summary>
    Bundled,
}
