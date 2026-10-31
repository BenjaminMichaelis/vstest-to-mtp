namespace VsTestToMtp.Tests.Fixtures;

using System.Collections.Concurrent;

using VsTestToMtp.Inventory;

/// <summary>The inventory of a committed fixture, shared by every test that reads one, and helpers to check locations against it.</summary>
public static class FixtureInventory
{
    /// <summary>The test application of every fixture, relative to the fixture root as the inventory reports it.</summary>
    public const string TestProject = "tests/Calculator.Tests/Calculator.Tests.csproj";

    /// <summary>The production project of every fixture, relative to the fixture root as the inventory reports it.</summary>
    public const string ProductionProject = "src/Calculator/Calculator.csproj";

    // MSBuild evaluation is slow, so each fixture is evaluated once and the (immutable) result is shared between tests.
    private static readonly ConcurrentDictionary<string, Lazy<InventoryResult>> Cache = new();

    /// <summary>The fixture's inventory, evaluated once per test run.</summary>
    public static InventoryResult Of(FixtureInfo fixture) =>
        Cache.GetOrAdd(fixture.Name, _ => new Lazy<InventoryResult>(() => Build(fixture))).Value;

    /// <summary>A fresh inventory of the fixture's solution (for tests about determinism or mutation).</summary>
    public static InventoryResult Build(FixtureInfo fixture) =>
        InventoryBuilder.Build(Path.Combine(fixture.RootPath, "Fixture.slnx"), new InventoryOptions(fixture.RootPath));

    /// <summary>The text of the committed line <paramref name="location"/> points at.</summary>
    public static string LineAt(FixtureInfo fixture, SourceLocation location) =>
        File.ReadAllLines(Path.Combine(fixture.RootPath, location.File.Replace('/', Path.DirectorySeparatorChar)))[location.Line - 1];
}
