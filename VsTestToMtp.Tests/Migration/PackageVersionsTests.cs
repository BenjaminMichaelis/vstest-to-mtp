namespace VsTestToMtp.Tests.Migration;

using NuGet.Versioning;

using VsTestToMtp.Migration;

/// <summary>
/// Compatibility is decided offline from the version NuGet would restore. Version comparison itself is NuGet.Versioning's; these pin the
/// one decision that is ours: only a version NuGet resolves to without a feed counts.
/// </summary>
public class PackageVersionsTests
{
    [Test]
    [Arguments("4.0.2", "4.0.2")]
    [Arguments(" 4.0.2 ", "4.0.2")]
    [Arguments("4.0", "4.0.0")]
    [Arguments("[4.0.2]", "4.0.2")]
    [Arguments("[4.0.2,5.0.0)", "4.0.2")]
    [Arguments("[4.0.2, )", "4.0.2")]
    [Arguments("4.0.0-preview.1", "4.0.0-preview.1")]
    public async Task TryResolveMinimum_TakesTheLowerBoundNuGetResolvesTo(string value, string expected)
    {
        await Assert.That(PackageVersions.TryResolveMinimum(value, out NuGetVersion? version)).IsTrue();
        await Assert.That(version!).IsEqualTo(NuGetVersion.Parse(expected));
    }

    [Test]
    [Arguments(null)]
    [Arguments("")]
    [Arguments("4.*")]
    [Arguments("*")]
    [Arguments("4.0.*-*")]
    [Arguments("(4.0.2,)")]
    [Arguments("(,5.0.0)")]
    [Arguments("[,5.0.0]")]
    [Arguments("$(MSTestVersion)")]
    public async Task TryResolveMinimum_RefusesWhatOnlyAFeedCanResolve(string? value)
    {
        await Assert.That(PackageVersions.TryResolveMinimum(value, out _)).IsFalse();
    }
}
