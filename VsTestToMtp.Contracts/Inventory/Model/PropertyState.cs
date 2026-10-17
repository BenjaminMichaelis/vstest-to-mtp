namespace VsTestToMtp.Inventory;

/// <summary>An evaluated MSBuild property for one target framework.</summary>
/// <param name="Definition">The winning definition; <see langword="null"/> unless <see cref="Source"/> is <see cref="PropertySource.File"/>.</param>
/// <param name="Overridden">Earlier definitions that the winning one replaced, oldest first.</param>
/// <remarks>
/// <see cref="Definition"/> is non-null only when <see cref="Source"/> is <see cref="PropertySource.File"/>. An empty
/// <see cref="Value"/> means the property is defined but empty; a property that is not defined at all has no
/// <see cref="PropertyState"/>. Members that are lists are compared by reference by the generated record equality, so do not rely on
/// <c>==</c> or <c>Equals</c> for this type; compare the serialized output or specific fields.
/// </remarks>
public sealed record PropertyState(
    string Name,
    string Value,
    PropertySource Source,
    Provenance? Definition,
    IReadOnlyList<Provenance> Overridden)
{
    /// <summary>The property called <paramref name="name"/> (MSBuild property names are case-insensitive), or <see langword="null"/> when it is not defined.</summary>
    public static PropertyState? Find(IReadOnlyList<PropertyState> properties, string name) =>
        properties.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
}
