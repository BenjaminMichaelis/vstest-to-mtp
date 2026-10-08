namespace VsTestToMtp.Tests.Inventory;

using TUnit.Core;

using VsTestToMtp.Inventory;

/// <summary>
/// Skips tests that need the tool to treat paths case-sensitively. The tool decides this by OS
/// (<see cref="PathComparison.IsCaseInsensitive"/>: Windows and macOS are case-insensitive), not by probing the volume,
/// so this reads that same rule and the test pins the tool's documented behavior.
/// </summary>
public sealed class SkipOnCaseInsensitivePathsAttribute()
    : SkipAttribute("The tool treats paths case-insensitively on this OS (Windows/macOS).")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(PathComparison.IsCaseInsensitive);
}
