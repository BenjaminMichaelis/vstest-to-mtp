namespace VsTestToMtp.Tests.Inventory;

using VsTestToMtp.Inventory;

/// <summary>
/// A small disposable repository written to a temp directory (outside this repository, so none of its
/// MSBuild files leak in). Used to exercise inventory edge cases the committed fixtures do not cover.
/// </summary>
public sealed class ScenarioWorkspace : IDisposable
{
    public ScenarioWorkspace()
    {
        RootPath = Path.Combine(Path.GetTempPath(), "vstest-to-mtp-scenarios", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(RootPath);
    }

    public string RootPath { get; }

    public string PathOf(string relativePath) => Path.Combine(RootPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    public ScenarioWorkspace Write(string relativePath, string content)
    {
        string path = PathOf(relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content.ReplaceLineEndings("\n"));
        return this;
    }

    /// <summary>Writes an SDK-style project; <paramref name="body"/> is placed after the target framework property group.</summary>
    public ScenarioWorkspace WriteProject(string relativePath, string body = "", string targetFramework = "<TargetFramework>net10.0</TargetFramework>") =>
        Write(relativePath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {targetFramework}
              </PropertyGroup>
            {body}
            </Project>
            """);

    public ScenarioWorkspace WriteSlnx(string relativePath, params string[] projects) =>
        Write(relativePath, $"<Solution>\n  <Folder Name=\"/all/\">\n{string.Join("\n", projects.Select(p => $"    <Project Path=\"{p}\" />"))}\n  </Folder>\n</Solution>\n");

    /// <summary>
    /// The 1-based line of the <paramref name="occurrence"/>th line of <paramref name="relativePath"/> containing
    /// <paramref name="text"/>, so provenance assertions follow the file instead of hard-coded line numbers.
    /// </summary>
    public int LineOf(string relativePath, string text, int occurrence = 1)
    {
        string[] lines = File.ReadAllLines(PathOf(relativePath));
        int found = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Contains(text, StringComparison.Ordinal) && ++found == occurrence)
            {
                return i + 1;
            }
        }

        throw new InvalidOperationException($"'{text}' (occurrence {occurrence}) not found in '{relativePath}'.");
    }

    public InventoryResult Inventory(string selection) =>
        InventoryBuilder.Build(PathOf(selection), new InventoryOptions(RootPath));

    public static string PackageReferences(params string[] packages) =>
        "  <ItemGroup>\n" + string.Join("\n", packages.Select(p => p.Contains('|')
            ? $"    <PackageReference Include=\"{p.Split('|')[0]}\" Version=\"{p.Split('|')[1]}\" />"
            : $"    <PackageReference Include=\"{p}\" />")) + "\n  </ItemGroup>";

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}
