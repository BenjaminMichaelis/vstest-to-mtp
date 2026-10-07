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
            if (skipped.ImportedFile is null || !File.Exists(skipped.ImportedFile))
            {
                string target = skipped.ImportedFile is null ? skipped.UnexpandedProject : formatter.Format(skipped.ImportedFile);
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
        && EvaluationContext.PathsEqual(element.ContainingProject.FullPath, context.ProjectPath);

    /// <summary>
    /// Flags conditions on elements that matter for migration (relevant properties, package/project references, imports)
    /// that compare an unset or environment-provided property with a literal. Unevaluated elements are scanned too,
    /// because a false condition hides the very element whose state would change the outcome.
    /// </summary>
    public static void AddConditionBlockers(Project project, EvaluationContext context, string displayPath, List<InventoryBlocker> blockers)
    {
        IEnumerable<ProjectRootElement> roots = [project.Xml, .. project.Imports.Select(i => i.ImportedProject)];
        foreach (ProjectRootElement root in roots.DistinctBy(r => r.FullPath, StringComparer.OrdinalIgnoreCase))
        {
            if (!context.IsRepositoryFile(root.FullPath))
            {
                continue;
            }

            foreach (ProjectElement element in root.AllChildren.Where(IsRelevantElement))
            {
                SourceLocation location = context.LocationOf(element);
                foreach (string condition in EvaluationContext.Conditions(element))
                {
                    AddConditionBlockers(project, condition, location, displayPath, blockers);
                }
            }
        }
    }

    private static void AddConditionBlockers(Project project, string condition, SourceLocation location, string displayPath, List<InventoryBlocker> blockers)
    {
        foreach (Match match in LiteralComparison().Matches(condition))
        {
            foreach (string side in new[] { match.Groups["l"].Value, match.Groups["r"].Value })
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

                ProjectProperty? value = project.GetProperty(name);
                if (value is null)
                {
                    blockers.Add(new InventoryBlocker(
                        BlockerCodes.ConditionDependsOnUnsetProperty, BlockerSeverity.Warning,
                        $"Condition '{condition}' at {location} depends on '{name}', which is not set, so the guarded element is treated as absent/false.",
                        $"Set '{name}' (for example in Directory.Build.props) or pass it as a global property if the guarded state matters.",
                        displayPath, location));
                }
                else if (value.IsEnvironmentProperty)
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

    private static bool IsRelevantElement(ProjectElement element) => element switch
    {
        ProjectPropertyElement property => ProjectEvaluator.RelevantProperties.Contains(property.Name, StringComparer.OrdinalIgnoreCase),
        ProjectItemElement item => item.ItemType is "PackageReference" or "PackageVersion" or "GlobalPackageReference" or "ProjectReference",
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
