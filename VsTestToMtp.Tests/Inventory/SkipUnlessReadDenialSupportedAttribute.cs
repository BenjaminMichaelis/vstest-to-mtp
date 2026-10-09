namespace VsTestToMtp.Tests.Inventory;

using TUnit.Core;

/// <summary>
/// Skips tests that deny read access to a file or directory (ACL on Windows, mode bits on Linux/macOS) when that cannot
/// work: on other platforms, or for a privileged non-Windows process, which can read any directory regardless of mode.
/// </summary>
public sealed class SkipUnlessReadDenialSupportedAttribute()
    : SkipAttribute("Denying read access is unsupported here (unsupported platform, or a privileged process).")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context)
    {
        bool supported = OperatingSystem.IsWindows()
            || ((OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && !Environment.IsPrivilegedProcess);

        return Task.FromResult(!supported);
    }
}
