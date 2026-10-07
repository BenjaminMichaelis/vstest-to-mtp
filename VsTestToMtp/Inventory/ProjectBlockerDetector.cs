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
    // Environment-provided properties that are present on every machine and not worth flagging.
    private static readonly HashSet<string> WellKnownEnvironmentProperties = new(StringComparer.OrdinalIgnoreCase) { "OS" };

    // MSBuild item types are case-insensitive.
    private static readonly HashSet<string> RelevantItemTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "PackageReference", "PackageVersion", "GlobalPackageReference", "ProjectReference",
    };

    public void AddImportBlockers(ImportLogger logger, EvaluationContext context, string displayPath, List<InventoryBlocker> blockers)
    {
        foreach (SkippedImport skipped in logger.Skipped)
        {
            string importingFile = skipped.ImportingFile.Length == 0 ? context.ProjectPath : skipped.ImportingFile;
            if (!context.IsRepositoryFile(importingFile))
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
    public void AddEarlyIsTestProjectBlockers(Project project, EvaluationContext context, string displayPath, List<InventoryBlocker> blockers)
    {
        foreach (ResolvedImport import in project.Imports)
        {
            string file = import.ImportedProject.FullPath;
            if (!context.IsRepositoryFile(file)
                || !file.EndsWith(".props", StringComparison.OrdinalIgnoreCase)
                || IsImportedFromProjectBody(import, context))
            {
                continue;
            }

            foreach (ProjectElement element in import.ImportedProject.AllChildren)
            {
                if (element.Condition.Length > 0 && IsTestProjectReference().IsMatch(element.Condition))
                {
                    blockers.Add(new InventoryBlocker(
                        BlockerCodes.IsTestProjectEarlyCondition, BlockerSeverity.Warning,
                        $"'{formatter.Format(file)}' is evaluated before the project body but conditions on $(IsTestProject), which is not yet reliable there.",
                        "Move this logic to Directory.Build.targets, or detect test projects from package references instead of IsTestProject.",
                        displayPath, context.LocationOf(element)));
                }
            }
        }
    }

    private static bool IsImportedFromProjectBody(ResolvedImport import, EvaluationContext context) =>
        import.ImportingElement is { } element
        && string.IsNullOrEmpty(element.Sdk)
        && PathComparison.Equal(element.ContainingProject.FullPath, context.ProjectPath);

    /// <summary>
    /// Flags conditions on elements that matter for migration (relevant properties, package/project references, imports)
    /// that compare an unset or environment-provided property with a literal. Unevaluated elements are scanned too,
    /// because a false condition hides the very element whose state would change the outcome.
    /// MSBuild evaluates all properties before any item, so item conditions see final property state, but property and
    /// import conditions only see properties defined earlier in evaluation order.
    /// </summary>
    public static void AddConditionBlockers(Project project, EvaluationContext context, string displayPath, List<InventoryBlocker> blockers)
    {
        EvaluationOrder order = new(project);
        IEnumerable<ProjectRootElement> roots = [project.Xml, .. project.Imports.Select(i => i.ImportedProject)];
        foreach (ProjectRootElement root in roots.DistinctBy(r => r.FullPath, PathComparison.Comparer))
        {
            if (!context.IsRepositoryFile(root.FullPath))
            {
                continue;
            }

            foreach (ProjectElement element in root.AllChildren.Where(IsRelevantElement))
            {
                SourceLocation location = context.LocationOf(element);
                int? evaluatedAt = element is ProjectItemElement ? null : order.IndexOf(element);
                foreach (string condition in EvaluationContext.Conditions(element))
                {
                    AddConditionBlockers(project, condition, location, evaluatedAt, order, displayPath, blockers);
                }
            }
        }
    }

    private static void AddConditionBlockers(
        Project project,
        string condition,
        SourceLocation location,
        int? evaluatedAt,
        EvaluationOrder order,
        string displayPath,
        List<InventoryBlocker> blockers)
    {
        foreach (Match match in LiteralComparison().Matches(condition))
        {
            string left = match.Groups["l"].Value;
            string right = match.Groups["r"].Value;
            foreach ((string side, string other) in new[] { (left, right), (right, left) })
            {
                Match property = PropertyReference().Match(side);
                if (!property.Success)
                {
                    continue;
                }

                string name = property.Groups["n"].Value;
                if (string.Equals(name, "IsTestProject", StringComparison.OrdinalIgnoreCase)
                    || WellKnownEnvironmentProperties.Contains(name))
                {
                    continue;
                }

                // Comparing with '' is a deliberate "is it unset?" probe, so an absent (or later-defined) property is intended.
                // An environment-provided one still varies between machines, so that is reported either way.
                bool isUnsetProbe = other.Length == 0;
                ProjectProperty? value = project.GetProperty(name);
                if (!isUnsetProbe && (value is null || IsDefinedOnlyLater(value, evaluatedAt, order)))
                {
                    string state = value is null ? "which is not set" : "which is only set later in evaluation";
                    blockers.Add(new InventoryBlocker(
                        BlockerCodes.ConditionDependsOnUnsetProperty, BlockerSeverity.Warning,
                        $"Condition '{condition}' at {location} depends on '{name}', {state}, so the guarded element is treated as absent/false.",
                        $"Set '{name}' before this element (for example in Directory.Build.props) or pass it as a global property if the guarded state matters.",
                        displayPath, location));
                }
                else if (value is { IsEnvironmentProperty: true })
                {
                    blockers.Add(new InventoryBlocker(
                        BlockerCodes.ConditionDependsOnEnvironment, BlockerSeverity.Warning,
                        $"Condition '{condition}' at {location} depends on the environment variable '{name}', so the result can differ between machines and CI.",
                        $"Define '{name}' explicitly in MSBuild if the guarded state matters.",
                        displayPath, location));
                }
            }
        }
    }

    /// <summary>
    /// Whether every definition of <paramref name="value"/> comes after the element being evaluated. Global, reserved and
    /// environment properties exist from the start, as does anything an earlier definition overrode.
    /// </summary>
    private static bool IsDefinedOnlyLater(ProjectProperty value, int? evaluatedAt, EvaluationOrder order)
    {
        if (evaluatedAt is not { } at)
        {
            return false;
        }

        int? earliest = null;
        for (ProjectProperty? definition = value; definition is not null; definition = definition.Predecessor)
        {
            if (definition.Xml is null || definition.IsGlobalProperty || definition.IsEnvironmentProperty || definition.IsReservedProperty)
            {
                return false;
            }

            if (order.IndexOf(definition.Xml) is { } index)
            {
                earliest = Math.Min(earliest ?? index, index);
            }
        }

        return earliest is { } first && first >= at;
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

    [GeneratedRegex(@"'(?<l>[^']*)'\s*(?:==|!=)\s*'(?<r>[^']*)'")]
    private static partial Regex LiteralComparison();

    [GeneratedRegex(@"^\$\((?<n>[A-Za-z_][A-Za-z0-9_]*)\)$")]
    private static partial Regex PropertyReference();
}
