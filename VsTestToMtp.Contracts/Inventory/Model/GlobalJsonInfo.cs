namespace VsTestToMtp.Inventory;

/// <summary>The effective <c>global.json</c> for the selection.</summary>
/// <param name="TestRunner"><c>test.runner</c>, e.g. <c>Microsoft.Testing.Platform</c>; <see langword="null"/> means VSTest.</param>
public sealed record GlobalJsonInfo(
    string Path,
    string? SdkVersion,
    string? RollForward,
    string? TestRunner,
    IReadOnlyList<string> MsBuildSdks);
