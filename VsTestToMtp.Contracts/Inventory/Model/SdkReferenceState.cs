namespace VsTestToMtp.Inventory;

/// <summary>An MSBuild project SDK that a repository file references (<c>Sdk="..."</c>, <c>&lt;Sdk&gt;</c> or <c>&lt;Import Sdk="..."&gt;</c>).</summary>
/// <param name="Name">The SDK name, for example <c>MSTest.Sdk</c>.</param>
/// <param name="DeclaredVersion">
/// The version written next to the name (<c>MSTest.Sdk/4.0.2</c>); <see langword="null"/> when the reference has none, in which case a
/// versioned SDK takes its version from <c>global.json</c> <c>msbuild-sdks</c>.
/// </param>
/// <param name="ResolvedVersion">The version MSBuild's SDK resolver resolved, when it reports one.</param>
/// <param name="Location">The referencing element (the <c>Project</c> element for its <c>Sdk</c> attribute).</param>
public sealed record SdkReferenceState(string Name, string? DeclaredVersion, string? ResolvedVersion, SourceLocation Location);
