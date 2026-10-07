using Microsoft.Build.Construction;
using Microsoft.Build.Evaluation;

namespace VsTestToMtp.Inventory;

/// <summary>
/// The order in which MSBuild evaluates a project's elements: the SDK's implicit top imports, then the project body in
/// document order with each import expanded where it appears, then the SDK's implicit bottom imports.
/// A property condition only sees properties defined earlier in this order.
/// </summary>
internal sealed class EvaluationOrder
{
    private readonly Dictionary<ProjectElement, int> _order = [];
    private readonly Dictionary<ProjectImportElement, List<ProjectRootElement>> _imported = [];
    private readonly HashSet<ProjectRootElement> _visited = [];
    private int _next;

    public EvaluationOrder(Project project)
    {
        foreach (ResolvedImport import in project.Imports)
        {
            if (import.ImportingElement is { } importing)
            {
                if (!_imported.TryGetValue(importing, out List<ProjectRootElement>? roots))
                {
                    _imported[importing] = roots = [];
                }

                roots.Add(import.ImportedProject);
            }
        }

        Walk(project.Xml);
    }

    /// <summary>The element's position, or <see langword="null"/> when it was not reached.</summary>
    public int? IndexOf(ProjectElement element) => _order.TryGetValue(element, out int index) ? index : null;

    private void Walk(ProjectRootElement root)
    {
        if (!_visited.Add(root))
        {
            return;
        }

        List<ProjectElement> tree = [.. root.AllChildren];
        HashSet<ProjectElement> inTree = [.. tree];

        // Implicit SDK imports are not part of the document: Sdk.props first, Sdk.targets last.
        List<ProjectImportElement> implicitImports = [.. _imported.Keys.Where(e => ReferenceEquals(e.ContainingProject, root) && !inTree.Contains(e))];
        foreach (ProjectImportElement import in implicitImports.Where(IsPropsImport))
        {
            Visit(import);
        }

        foreach (ProjectElement element in tree)
        {
            _order[element] = _next++;
            if (element is ProjectImportElement import)
            {
                Visit(import);
            }
        }

        foreach (ProjectImportElement import in implicitImports.Where(i => !IsPropsImport(i)))
        {
            Visit(import);
        }
    }

    private void Visit(ProjectImportElement import)
    {
        _order.TryAdd(import, _next++);
        if (_imported.TryGetValue(import, out List<ProjectRootElement>? roots))
        {
            foreach (ProjectRootElement imported in roots)
            {
                Walk(imported);
            }
        }
    }

    private static bool IsPropsImport(ProjectImportElement import) =>
        import.Project.EndsWith(".props", StringComparison.OrdinalIgnoreCase);
}
