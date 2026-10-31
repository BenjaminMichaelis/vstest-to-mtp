namespace VsTestToMtp.Tests.Inventory;

using System.Reflection;

using VsTestToMtp.Inventory;

/// <summary>
/// The MSBuild host captures <see cref="MsBuildPropertyNames.Captured"/> and readers name properties through the constants. Every constant
/// must be captured, or a reader would see a property it asked for as unset (and decide as if the project did not set it).
/// </summary>
public class MsBuildPropertyNamesTests
{
    private static string[] Constants =>
    [
        .. typeof(MsBuildPropertyNames)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (string)f.GetRawConstantValue()!),
    ];

    [Test]
    public async Task EveryNamedProperty_IsCaptured_AndNothingElse()
    {
        await Assert.That(MsBuildPropertyNames.Captured).IsEquivalentTo(Constants);
        await Assert.That(MsBuildPropertyNames.Captured).HasDistinctItems();
    }

    [Test]
    public async Task EveryConstant_IsTheNameItStandsFor()
    {
        foreach (FieldInfo field in typeof(MsBuildPropertyNames).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral))
        {
            await Assert.That((string)field.GetRawConstantValue()!).IsEqualTo(field.Name);
        }
    }
}
