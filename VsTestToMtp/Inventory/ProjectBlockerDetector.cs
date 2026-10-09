using System.Text.RegularExpressions;

using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace VsTestToMtp.Inventory;

/// <summary>
/// Turns suspicious MSBuild state (skipped imports, early <c>$(IsTestProject)</c> conditions, conditions on unset or
/// environment-provided properties) into actionable <see cref="InventoryBlocker"/>s.
/// </summary>
internal sealed partial class ProjectBlockerDetector(PathFormatter formatter)
{
    // Environment-provided properties that are present on every machine and not worth flagging. MSBuildExtensionsPath and
    // MSBuildSDKsPath are environment variables that MSBuildLocator sets when it registers the SDK, so they hold the same
    // meaning for everyone using that SDK.
    private static readonly HashSet<string> WellKnownEnvironmentProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "OS",
        "MSBuildExtensionsPath",
        "MSBuildSDKsPath",
    };

    // MSBuild item types are case-insensitive.
    private static readonly HashSet<string> RelevantItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PackageReference", "PackageVersion", "GlobalPackageReference", "ProjectReference",
    };

    public void AddImportBlockers(IEnumerable<SkippedImport> skippedImports, ProvenanceResolver resolver, string displayPath, List<InventoryBlocker> blockers)
    {
        foreach (SkippedImport skipped in skippedImports)
        {
            string importingFile = skipped.ImportingFile.Length == 0 ? resolver.ProjectPath : skipped.ImportingFile;
            if (!resolver.IsRepositoryFile(importingFile))
            {
                continue;
            }

            SourceLocation location = formatter.Location(importingFile, skipped.Line, skipped.Column);
            if (string.IsNullOrEmpty(skipped.ImportedFile))
            {
                // MSBuild does not log imports skipped for a false condition, so an ignored import with no file
                // is one whose condition held but whose path expanded to nothing.
                blockers.Add(new InventoryBlocker(
                    BlockerCodes.MissingImport, BlockerSeverity.Error,
                    $"Import '{skipped.UnexpandedProject}' at {location} resolves to an empty path, so the evaluated state is incomplete.",
                    "Make sure the imported file exists (for GetPathOfFileAbove, that a parent file exists), or guard the import with Condition=\"Exists('...')\" if it is intentionally optional.",
                    displayPath, location));
            }
            else if (!File.Exists(skipped.ImportedFile))
            {
                string target = formatter.Format(skipped.ImportedFile);
                blockers.Add(new InventoryBlocker(
                    BlockerCodes.MissingImport, BlockerSeverity.Error,
                    $"Import '{target}' at {location} does not resolve to an existing file, so the evaluated state is incomplete.",
                    "Create the file, correct the import path, or guard the import with Condition=\"Exists('...')\" if it is intentionally optional.",
                    displayPath, location));
            }
            else
            {
                blockers.Add(new InventoryBlocker(
                    BlockerCodes.EvaluationFailed, BlockerSeverity.Error,
                    $"Import '{formatter.Format(skipped.ImportedFile)}' at {location} is empty or not a valid MSBuild file.",
                    "Fix the imported file, then rerun.",
                    displayPath, location));
            }
        }
    }

    /// <summary>
    /// Conditions on <c>$(IsTestProject)</c> inside early-evaluated <c>.props</c> files are unreliable: they run
    /// before the project body and before package-contributed props can set the property.
    /// </summary>
    public void AddEarlyIsTestProjectBlockers(Project project, ProvenanceResolver resolver, string displayPath, List<InventoryBlocker> blockers)
    {
        EvaluationOrder order = resolver.Order;
        int? firstDefinition = EarliestRepositoryDefinition(project.GetProperty("IsTestProject"), resolver, order);

        foreach (ResolvedImport import in project.Imports)
        {
            string file = import.ImportedProject.FullPath;
            if (!resolver.IsRepositoryFile(file) || !file.EndsWith(".props", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (ProjectElement element in import.ImportedProject.AllChildren)
            {
                // Only a definition in a repository file that is evaluated first makes the value reliable here; an
                // import that comes before the project assigns IsTestProject (or when nothing in the repository assigns it,
                // leaving it to restore-time package props) sees it unset.
                if (element.Condition.Length > 0
                    && IsTestProjectReference().IsMatch(element.Condition)
                    && !(firstDefinition is { } defined && order.IndexOf(element) is { } at && defined < at))
                {
                    blockers.Add(new InventoryBlocker(
                        BlockerCodes.IsTestProjectEarlyCondition, BlockerSeverity.Warning,
                        $"'{formatter.Format(file)}' conditions on $(IsTestProject) before anything in the repository has reliably set it, so the condition may not see the final value.",
                        "Move this logic to Directory.Build.targets, import the file after IsTestProject is assigned, or detect test projects from package references instead of IsTestProject.",
                        displayPath, resolver.LocationOf(element)));
                }
            }
        }
    }

    private static int? EarliestRepositoryDefinition(ProjectProperty? property, ProvenanceResolver resolver, EvaluationOrder order)
    {
        int? earliest = null;
        for (ProjectProperty? definition = property; definition is not null; definition = definition.Predecessor)
        {
            if (definition.Xml is { } xml && resolver.IsRepositoryFile(xml.Location.File) && order.IndexOf(xml) is { } index)
            {
                earliest = Math.Min(earliest ?? index, index);
            }
        }

        return earliest;
    }

    /// <summary>
    /// Flags conditions on elements that matter for migration (relevant properties, package/project references, imports)
    /// that compare an unset or environment-provided property with a literal. Unevaluated elements are scanned too,
    /// because a false condition hides the very element whose state would change the outcome.
    /// MSBuild evaluates all properties before any item, so item conditions see final property state, but property and
    /// import conditions only see properties defined earlier in evaluation order.
    /// </summary>
    public static void AddConditionBlockers(Project project, ProvenanceResolver resolver, string displayPath, List<InventoryBlocker> blockers)
    {
        EvaluationOrder order = resolver.Order;
        IEnumerable<ProjectRootElement> roots = [project.Xml, .. project.Imports.Select(i => i.ImportedProject)];
        foreach (ProjectRootElement root in roots.DistinctBy(r => r.FullPath, PathComparison.Comparer))
        {
            if (!resolver.IsRepositoryFile(root.FullPath))
            {
                continue;
            }

            foreach (ProjectElement element in root.AllChildren.Where(IsRelevantElement))
            {
                SourceLocation location = resolver.LocationOf(element);
                int? evaluatedAt = element is ProjectItemElement ? null : order.IndexOf(element);
                foreach (string condition in ProvenanceResolver.Conditions(element))
                {
                    AddConditionBlockers(project, resolver, condition, location, evaluatedAt, order, displayPath, blockers);
                }
            }
        }
    }

    private static void AddConditionBlockers(
        Project project,
        ProvenanceResolver resolver,
        string condition,
        SourceLocation location,
        int? evaluatedAt,
        EvaluationOrder order,
        string displayPath,
        List<InventoryBlocker> blockers)
    {
        // Every simple property reference counts, whatever the surrounding syntax (either quote style, a bare boolean
        // such as Condition="$(CI)", negation, and/or, Exists(...)). Property functions are not simple references.
        foreach (string name in PropertyReferences().Matches(condition).Select(m => m.Groups["n"].Value).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            // MSBuild's reserved properties are in the property table and recognised by SourceAt. The exception is the per-file
            // MSBuildThisFile* family, which MSBuild resolves while evaluating each file and never stores. A custom property that
            // merely starts with "MSBuild" (MSBuildEnableWorkloadResolver, ...) is an ordinary property and is analysed as one.
            if (string.Equals(name, "IsTestProject", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("MSBuildThisFile", StringComparison.OrdinalIgnoreCase)
                || WellKnownEnvironmentProperties.Contains(name))
            {
                continue;
            }

            // Comparing with '' is a deliberate "is it unset?" probe, so an absent (or later-defined) property is intended.
            // An environment-provided one still varies between machines, so that is reported either way.
            bool isUnsetProbe = IsUnsetProbe(condition, name);
            ProjectProperty? value = project.GetProperty(name);
            PropertySourceAtEvaluation source = SourceAt(value, evaluatedAt, order);
            if (!isUnsetProbe && source == PropertySourceAtEvaluation.Unset)
            {
                string state = value is null ? "which is not set" : "which is only set later in evaluation";
                blockers.Add(new InventoryBlocker(
                    BlockerCodes.ConditionDependsOnUnsetProperty, BlockerSeverity.Warning,
                    $"Condition '{condition}' at {location} depends on '{name}', {state}, so the guarded element is treated as absent/false.",
                    $"Set '{name}' before this element (for example in Directory.Build.props) or pass it as a global property if the guarded state matters.",
                    displayPath, location)
                {
                    // Where the property is (only later) defined, in evaluation order; empty when it is never set.
                    RelatedLocations = [.. Definitions(value).Select(resolver.LocationOf)],
                });
            }
            else if (source == PropertySourceAtEvaluation.Environment)
            {
                blockers.Add(new InventoryBlocker(
                    BlockerCodes.ConditionDependsOnEnvironment, BlockerSeverity.Warning,
                    $"Condition '{condition}' at {location} depends on the environment variable '{name}', so the result can differ between machines and CI.",
                    $"Define '{name}' explicitly in MSBuild if the guarded state matters.",
                    displayPath, location));
            }
        }
    }

    // '$(Name)' == '' / != '' in either order and with either quote style.
    private static bool IsUnsetProbe(string condition, string name)
    {
        string reference = @"\$\(\s*" + Regex.Escape(name) + @"\s*\)";
        return Regex.IsMatch(
            condition,
            $@"(['""]){reference}\1\s*[!=]=\s*(['""])\2|(['""])\3\s*[!=]=\s*(['""]){reference}\4",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    /// <summary>
    /// Where the property's value comes from when the element at <paramref name="evaluatedAt"/> is evaluated, not where its
    /// final value comes from: a later assignment can override an environment value, and a property defined only later is still
    /// unset at that point. Items (<paramref name="evaluatedAt"/> is null) are evaluated after every property, so they see the
    /// final value.
    /// </summary>
    private static PropertySourceAtEvaluation SourceAt(ProjectProperty? value, int? evaluatedAt, EvaluationOrder order)
    {
        for (ProjectProperty? definition = value; definition is not null; definition = definition.Predecessor)
        {
            if (definition.IsEnvironmentProperty)
            {
                return PropertySourceAtEvaluation.Environment;
            }

            if (definition.Xml is null || definition.IsGlobalProperty || definition.IsReservedProperty)
            {
                return PropertySourceAtEvaluation.Other;
            }

            // A file definition counts when it is evaluated before the element (or when evaluation order does not apply).
            if (evaluatedAt is not { } at || order.IndexOf(definition.Xml) is not { } index || index < at)
            {
                return PropertySourceAtEvaluation.Other;
            }
        }

        return PropertySourceAtEvaluation.Unset;
    }

    private static List<ProjectPropertyElement> Definitions(ProjectProperty? property)
    {
        List<ProjectPropertyElement> definitions = [];
        for (ProjectProperty? definition = property; definition is not null; definition = definition.Predecessor)
        {
            if (definition.Xml is { } xml)
            {
                definitions.Insert(0, xml);
            }
        }

        return definitions;
    }

    private enum PropertySourceAtEvaluation
    {
        Unset,
        Environment,
        Other,
    }

    private static bool IsRelevantElement(ProjectElement element) => element switch
    {
        ProjectPropertyElement property => ProjectEvaluator.RelevantProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase),
        ProjectItemElement item => RelevantItemTypes.Contains(item.ItemType),
        ProjectImportElement => true,
        _ => false,
    };

    [GeneratedRegex(@"\$\(\s*IsTestProject\s*\)", RegexOptions.IgnoreCase)]
    private static partial Regex IsTestProjectReference();

    [GeneratedRegex(@"\$\(\s*(?<n>[A-Za-z_][A-Za-z0-9_]*)\s*\)")]
    private static partial Regex PropertyReferences();
}
