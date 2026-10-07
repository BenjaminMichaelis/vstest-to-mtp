namespace VsTestToMtp.Tests.Inventory;

using TUnit.Core;

/// <summary>
/// Skips tests that need the tool to treat paths case-sensitively. The tool treats paths as case-insensitive on
/// Windows and macOS and case-sensitive elsewhere (see <c>PathComparison</c>), regardless of the actual volume, so this
/// states that same OS rule instead of probing the file system. The rule is deliberately restated here rather than
/// shared: if the tool's rule changes, the test fails (or stops running) and is revisited, instead of silently following it.
/// </summary>
public sealed class SkipOnCaseInsensitivePathsAttribute()
    : SkipAttribute("The tool treats paths case-insensitively on this OS (Windows/macOS).")
{
    public override Task<bool> ShouldSkip(TestRegisteredContext context) =>
        Task.FromResult(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());
}
