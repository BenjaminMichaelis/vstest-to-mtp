namespace VsTestToMtp.Tests.Fixtures;

/// <summary>
/// TUnit <c>[MethodDataSource]</c> sources over <see cref="FixtureCatalog"/>.
/// Each case is wrapped in a <see cref="Func{T}"/> so every test gets its own value.
/// </summary>
public static class FixtureDataSources
{
    /// <summary>Every committed fixture.</summary>
    public static IEnumerable<Func<FixtureInfo>> All() => FixtureCatalog.All.Select(Wrap);

    /// <summary>Only the xUnit fixtures (v2 and v3).</summary>
    public static IEnumerable<Func<FixtureInfo>> Xunit() =>
        FixtureCatalog.All.Where(f => f.Framework.StartsWith("xunit", StringComparison.Ordinal)).Select(Wrap);

    private static Func<FixtureInfo> Wrap(FixtureInfo fixture) => () => fixture;
}
