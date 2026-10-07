namespace VsTestToMtp.Tests.Inventory;

using TUnit.Core;

using VsTestToMtp.Inventory;

/// <summary>
/// Skips tests that need the tool to treat paths case-sensitively. The tool decides this by OS
/// (<see cref="PathComparison.IsCaseInsensitive"/>: Windows and macOS are case-insensitive), so this reads that same
/// rule instead of probing the file system: the test pins the tool's documented behavior, not whatever volume the
/// runner happens to use.
/// </summary>
public sealed class SkipOnCaseInsensitivePathsAttribute()
    : SkipAttribute("The tool treats paths case-insensitively on this OS (Windows/macOS).")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(PathComparison.IsCaseInsensitive);
}
