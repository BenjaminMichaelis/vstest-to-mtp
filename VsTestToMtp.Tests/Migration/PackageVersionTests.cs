namespace VsTestToMtp.Tests.Migration;

using VsTestToMtp.Migration;

public class PackageVersionTests
{
    [Test]
    [Arguments("4.0.0", "4.0.0", 0)]
    [Arguments("4.0", "4.0.0.0", 0)]
    [Arguments("4", "4.0.0", 0)]
    [Arguments("3.11.0", "4.0.0", -1)]
    [Arguments("10.0.0", "9.9.9", 1)]
    [Arguments("4.0.0-preview.1", "4.0.0", -1)]
    [Arguments("4.0.0-pre.33", "4.0.0-pre.108", -1)]
    [Arguments("4.0.0-alpha", "4.0.0-alpha.1", -1)]
    [Arguments("4.0.0-1", "4.0.0-alpha", -1)]
    [Arguments("4.0.0-ALPHA", "4.0.0-alpha", 0)]
    [Arguments("4.0.0+build.5", "4.0.0", 0)]
    [Arguments("0.70.7", "0.71.0", -1)]
    public async Task Compare_OrdersVersionsTheWayNuGetDoes(string left, string right, int expected)
    {
        int actual = PackageVersion.Parse(left).CompareTo(PackageVersion.Parse(right));

        await Assert.That(Math.Sign(actual)).IsEqualTo(expected);
    }

    [Test]
    [Arguments("")]
    [Arguments("4.x")]
    [Arguments("4.0.0.0.0")]
    [Arguments("4.0.0-")]
    [Arguments("v4.0.0")]
    [Arguments("4..0")]
    [Arguments("$(MSTestVersion)")]
    public async Task TryParse_RejectsWhatIsNotAVersion(string value)
    {
        await Assert.That(PackageVersion.TryParse(value, out _)).IsFalse();
    }

    [Test]
    [Arguments("4.0.2", "4.0.2")]
    [Arguments(" 4.0.2 ", "4.0.2")]
    [Arguments("[4.0.2]", "4.0.2")]
    [Arguments("[4.0.2,5.0.0)", "4.0.2")]
    [Arguments("[4.0.2, )", "4.0.2")]
    public async Task TryResolveMinimum_TakesTheLowerBoundNuGetResolvesTo(string value, string expected)
    {
        await Assert.That(PackageVersion.TryResolveMinimum(value, out PackageVersion? version)).IsTrue();
        await Assert.That(version!).IsEqualTo(PackageVersion.Parse(expected));
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
    [Arguments("[4.0.2)")]
    public async Task TryResolveMinimum_RefusesWhatOnlyAFeedCanResolve(string? value)
    {
        await Assert.That(PackageVersion.TryResolveMinimum(value, out _)).IsFalse();
    }
}
