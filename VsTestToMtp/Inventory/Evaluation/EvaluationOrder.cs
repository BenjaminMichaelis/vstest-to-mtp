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

        // Implicit SDK imports are not part of the document; MSBuild records where each one goes (Sdk.props at the top, Sdk.targets at the bottom).
        // https://github.com/dotnet/msbuild/blob/74878b50aab1c07a7cdcaa15f28115768388cdcc/src/Build/Construction/ProjectImportElement.cs#L126
        List<ProjectImportElement> implicitImports = [.. _imported.Keys.Where(e => ReferenceEquals(e.ContainingProject, root) && !inTree.Contains(e))];
        foreach (ProjectImportElement import in implicitImports.Where(i => i.ImplicitImportLocation == ImplicitImportLocation.Top))
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

        foreach (ProjectImportElement import in implicitImports.Where(i => i.ImplicitImportLocation != ImplicitImportLocation.Top))
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
}
