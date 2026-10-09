using Microsoft.Build.Evaluation;

namespace VsTestToMtp.Inventory;

/// <summary>
/// Applies NuGet's rules for which version a package reference effectively gets (inline Version, VersionOverride or a central
/// PackageVersion) in one evaluated project, and reports declarations NuGet would reject or ignore.
/// </summary>
internal sealed class PackageVersionResolver
{
    private readonly Dictionary<string, ProjectItem> _central = new(StringComparer.OrdinalIgnoreCase);
    private readonly bool _centralManagement;
    private readonly bool _overrideAllowed;
    private readonly ProvenanceResolver _resolver;
    private readonly string _displayPath;
    private readonly List<InventoryBlocker> _blockers;

    public PackageVersionResolver(Project project, ProvenanceResolver resolver, string displayPath, List<InventoryBlocker> blockers)
    {
        _resolver = resolver;
        _displayPath = displayPath;
        _blockers = blockers;

        // A PackageVersion only supplies versions when Central Package Management is on; NuGet ignores it otherwise.
        // Matches NuGet's restore outside Visual Studio: it reads _CentralPackageVersionsEnabled, which NuGet.targets sets only when
        // ManagePackageVersionsCentrally == 'true' and a Directory.Packages.props was imported, and disables overrides only for a
        // literal "false" (overrides are on by default).
        // https://github.com/NuGet/NuGet.Client/blob/b337f5b80d3363a61f773f2e1c3757526a474610/src/NuGet.Core/NuGet.Commands/RestoreCommand/Utility/PackageSpecFactory.cs#L513-L517
        _centralManagement = NuGetBoolean.IsTrue(project.GetPropertyValue("_CentralPackageVersionsEnabled"));
        _overrideAllowed = _centralManagement && !NuGetBoolean.IsFalse(project.GetPropertyValue("CentralPackageVersionOverrideEnabled"));
        if (!_centralManagement)
        {
            return;
        }

        foreach (IGrouping<string, ProjectItem> group in project.GetItems("PackageVersion").GroupBy(v => v.EvaluatedInclude, StringComparer.OrdinalIgnoreCase))
        {
            if (group.Count() == 1)
            {
                _central[group.Key] = group.Single();
                continue;
            }

            // NuGet reports NU1506 here because restore is inconsistent; do not pick a winner.
            Provenance[] definitions = [.. group.Select(v => resolver.Provenance(v.Xml))];
            blockers.Add(new InventoryBlocker(
                BlockerCodes.DuplicatePackageVersion, BlockerSeverity.Warning,
                $"PackageVersion '{group.Key}' is declared {definitions.Length} times ({string.Join(", ", definitions.Select(d => d.Location))}), so its central version is ambiguous.",
                "Keep one PackageVersion per package (use Update to change a version declared elsewhere).",
                displayPath, definitions[^1].Location));
        }
    }

    /// <summary>The effective version of <paramref name="item"/>, where it came from, and where that value was declared.</summary>
    /// <param name="definition">Where the reference itself is declared; blockers point there.</param>
    public (string? Version, PackageVersionSource Source, Provenance? Definition) Resolve(ProjectItem item, bool isGlobal, Provenance definition)
    {
        string name = item.EvaluatedInclude;
        string inline = item.GetMetadataValue("Version");
        string versionOverride = item.GetMetadataValue("VersionOverride");
        bool userOwned = definition.IsRepositoryFile;

        if (versionOverride.Length > 0 && !_overrideAllowed && userOwned)
        {
            // With central management on but overrides disabled NuGet fails restore (NU1013); with it off the metadata is
            // simply not a version source, which leaves the reference unversioned.
            Add(_centralManagement
                ? new InventoryBlocker(
                    BlockerCodes.IneffectiveVersionOverride, BlockerSeverity.Error,
                    $"PackageReference '{name}' sets VersionOverride while CentralPackageVersionOverrideEnabled is false, which NuGet rejects (NU1013).",
                    "Remove VersionOverride, or enable version overrides (CentralPackageVersionOverrideEnabled).",
                    _displayPath, definition.Location)
                : new InventoryBlocker(
                    BlockerCodes.IneffectiveVersionOverride, BlockerSeverity.Warning,
                    $"PackageReference '{name}' sets VersionOverride, which has no effect unless Central Package Management is on.",
                    "Enable Central Package Management, or use Version instead of VersionOverride.",
                    _displayPath, definition.Location));
        }

        if (inline.Length > 0 && _centralManagement && !isGlobal)
        {
            // NU1008: with Central Package Management a PackageReference must not carry Version (VersionOverride is the way to
            // override one). Restore fails, so this is not an effective version.
            if (userOwned)
            {
                Add(new InventoryBlocker(
                    BlockerCodes.InlineVersionUnderCentralManagement, BlockerSeverity.Error,
                    $"PackageReference '{name}' sets Version, which NuGet rejects (NU1008) when Central Package Management is on.",
                    "Move the version to a PackageVersion in Directory.Packages.props, or use VersionOverride to override the central version for this project.",
                    _displayPath, definition.Location));
            }

            return (null, PackageVersionSource.None, null);
        }

        if (inline.Length > 0)
        {
            return (inline, PackageVersionSource.Inline, MetadataProvenance(item, "Version", definition));
        }

        if (versionOverride.Length > 0 && _centralManagement && !_overrideAllowed)
        {
            // NU1013: restore fails; it does not fall back to the central version, so no version is effective.
            return (null, PackageVersionSource.None, null);
        }

        if (versionOverride.Length > 0 && _overrideAllowed)
        {
            return (versionOverride, PackageVersionSource.VersionOverride, MetadataProvenance(item, "VersionOverride", definition));
        }

        if (_central.TryGetValue(name, out ProjectItem? centralItem) && centralItem.GetMetadataValue("Version") is { Length: > 0 } centralVersion)
        {
            return (centralVersion, PackageVersionSource.Central, MetadataProvenance(centralItem, "Version", _resolver.Provenance(centralItem.Xml)));
        }

        if (userOwned)
        {
            Add(new InventoryBlocker(
                BlockerCodes.UnresolvedPackageVersion, BlockerSeverity.Warning,
                $"PackageReference '{name}' has no Version, VersionOverride or central PackageVersion.",
                "Add a Version, or a PackageVersion entry in the nearest Directory.Packages.props.",
                _displayPath, definition.Location));
        }

        return (null, PackageVersionSource.None, null);
    }

    private void Add(InventoryBlocker blocker) => _blockers.Add(blocker);

    /// <summary>
    /// Where the winning value of an item's metadata was declared: the Include, a later Update, or an item definition.
    /// Falls back to <paramref name="fallback"/> when MSBuild does not expose the declaring element.
    /// </summary>
    private Provenance MetadataProvenance(ProjectItem item, string metadataName, Provenance fallback) =>
        item.GetMetadata(metadataName)?.Xml is { } xml ? _resolver.Provenance(xml) : fallback;
}
