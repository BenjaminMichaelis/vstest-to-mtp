using Microsoft.VisualStudio.SolutionPersistence;
using Microsoft.VisualStudio.SolutionPersistence.Model;
using Microsoft.VisualStudio.SolutionPersistence.Serializer;

namespace VsTestToMtp.Inventory;

/// <summary>
/// Resolves what the caller selected into the exact set of C# project files, without touching them.
/// </summary>
internal static class SolutionReader
{
    public sealed record Selection(
        string Kind,
        string Path,
        IReadOnlyList<string> ProjectPaths,
        IReadOnlyList<InventoryBlocker> Blockers);

    public static Selection Resolve(string path, PathFormatter formatter)
    {
        string fullPath = System.IO.Path.GetFullPath(path);
        if (Directory.Exists(fullPath))
        {
            return ResolveDirectory(fullPath, formatter);
        }

        string extension = System.IO.Path.GetExtension(fullPath).ToLowerInvariant();
        return !File.Exists(fullPath)
            ? Failed("unknown", fullPath, BlockerCodes.SelectionNotFound,
                $"'{formatter.Format(fullPath)}' does not exist.",
                "Pass an existing .csproj, .sln or .slnx file, or a directory containing exactly one solution.", formatter)
            : ResolveFile(fullPath, extension, formatter);
    }

    private static Selection ResolveFile(string fullPath, string extension, PathFormatter formatter)
    {
        return extension switch
        {
            ".csproj" => new Selection("project", fullPath, [fullPath], []),
            ".sln" or ".slnx" => ReadSolution(fullPath, formatter),
            _ => Failed("unknown", fullPath, BlockerCodes.UnsupportedSelection,
                $"'{formatter.Format(fullPath)}' is not a C# project or solution file.",
                "Pass a .csproj, .sln or .slnx file.", formatter),
        };
    }

    private static Selection ResolveDirectory(string directory, PathFormatter formatter)
    {
        string[] solutions =
        [
            .. Directory.EnumerateFiles(directory, "*.sln"),
            .. Directory.EnumerateFiles(directory, "*.slnx"),
        ];
        solutions = [.. solutions.Order(StringComparer.OrdinalIgnoreCase)];
        if (solutions.Length == 1)
        {
            return ReadSolution(solutions[0], formatter);
        }

        if (solutions.Length > 1)
        {
            return Failed("directory", directory, BlockerCodes.AmbiguousSelection,
                $"'{formatter.Format(directory)}' contains several solutions: {string.Join(", ", solutions.Select(s => System.IO.Path.GetFileName(s)))}.",
                "Pass the solution file you want to inventory.", formatter);
        }

        string[] projects = [.. Directory.EnumerateFiles(directory, "*.csproj").Order(StringComparer.OrdinalIgnoreCase)];
        return projects.Length == 1
            ? new Selection("project", projects[0], [projects[0]], [])
            : Failed("directory", directory, BlockerCodes.AmbiguousSelection,
                $"'{formatter.Format(directory)}' contains no single solution or project.",
                "Pass a .csproj, .sln or .slnx file.", formatter);
    }

    private static Selection ReadSolution(string solutionPath, PathFormatter formatter)
    {
        List<InventoryBlocker> blockers = [];
        List<string> projects = [];
        string solutionDirectory = System.IO.Path.GetDirectoryName(solutionPath)!;

        try
        {
            ISolutionSerializer serializer = SolutionSerializers.GetSerializerByMoniker(solutionPath)
                ?? throw new SolutionException($"No serializer for '{solutionPath}'.");
            SolutionModel model = serializer.OpenAsync(solutionPath, CancellationToken.None).GetAwaiter().GetResult();

            foreach (SolutionProjectModel project in model.SolutionProjects)
            {
                string projectPath = System.IO.Path.GetFullPath(
                    System.IO.Path.Combine(solutionDirectory, project.FilePath.Replace('\\', System.IO.Path.DirectorySeparatorChar)));
                string extension = System.IO.Path.GetExtension(projectPath);

                if (!string.Equals(extension, ".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    blockers.Add(new InventoryBlocker(
                        BlockerCodes.UnsupportedProjectLanguage, BlockerSeverity.Info,
                        $"'{formatter.Format(projectPath)}' is not a C# project and is skipped.",
                        "Only SDK-style C# (.csproj) projects are inventoried.",
                        formatter.Format(projectPath), formatter.Location(solutionPath)));
                }
                else if (!File.Exists(projectPath))
                {
                    blockers.Add(new InventoryBlocker(
                        BlockerCodes.MissingProject, BlockerSeverity.Error,
                        $"The solution lists '{formatter.Format(projectPath)}', which does not exist.",
                        "Restore the project file or remove it from the solution, then rerun.",
                        formatter.Format(projectPath), formatter.Location(solutionPath)));
                }
                else
                {
                    projects.Add(projectPath);
                }
            }
        }
        catch (Exception ex) when (ex is SolutionException or IOException or UnauthorizedAccessException)
        {
            blockers.Add(new InventoryBlocker(
                BlockerCodes.MalformedSolution, BlockerSeverity.Error,
                $"'{formatter.Format(solutionPath)}' could not be read: {ex.Message}",
                "Fix or regenerate the solution file, then rerun.",
                null, formatter.Location(solutionPath)));
        }

        string kind = string.Equals(System.IO.Path.GetExtension(solutionPath), ".slnx", StringComparison.OrdinalIgnoreCase) ? "slnx" : "sln";
        return new Selection(kind, solutionPath, [.. projects.Distinct(PathComparison.Comparer)], blockers);
    }

    private static Selection Failed(string kind, string path, string code, string message, string remediation, PathFormatter formatter) =>
        new(kind, path, [], [new InventoryBlocker(code, BlockerSeverity.Error, message, remediation, null, formatter.Location(path))]);
}
