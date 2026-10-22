namespace VsTestToMtp.Tests.Inventory;

using System.IO.Compression;

using VsTestToMtp.Inventory;
using VsTestToMtp.Tests.Fixtures;

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

        // MSBuild is registered once per process, so every workspace must resolve to the SDK the fixtures pin; without
        // this, unpinned workspaces resolve the newest installed SDK and tests would depend on which one ran first.
        // Tests that exercise global.json handling overwrite this file.
        Write("global.json", File.ReadAllText(Path.Combine(FixtureCatalog.FixturesRoot, "mstest", "plain", "global.json")));
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

    /// <summary>
    /// Writes an SDK-style project; <paramref name="body"/> is placed after the target framework property group.
    /// <paramref name="targetFrameworks"/> defaults to net10.0; one name sets TargetFramework, several set TargetFrameworks,
    /// and an empty array sets neither.
    /// </summary>
    public ScenarioWorkspace WriteProject(string relativePath, string body = "", string[]? targetFrameworks = null) =>
        Write(relativePath, $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                {TargetFrameworkElement(targetFrameworks)}
              </PropertyGroup>
            {body}
            </Project>
            """);

    private static string TargetFrameworkElement(string[]? targetFrameworks) => targetFrameworks switch
    {
        null => "<TargetFramework>net10.0</TargetFramework>",
        [] => string.Empty,
        [var single] => $"<TargetFramework>{single}</TargetFramework>",
        _ => $"<TargetFrameworks>{string.Join(';', targetFrameworks)}</TargetFrameworks>",
    };

    public ScenarioWorkspace WriteSlnx(string relativePath, params string[] projects)
    {
        string entries = string.Join("\n", projects.Select(p => $"""    <Project Path="{p}" />"""));
        return Write(relativePath, $"""
            <Solution>
              <Folder Name="/all/">
            {entries}
              </Folder>
            </Solution>
            """);
    }

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

    public static string PackageReferences(params PackageRef[] packages)
    {
        string references = string.Join("\n", packages.Select(p => p.Version is null
            ? $"""    <PackageReference Include="{p.Name}" />"""
            : $"""    <PackageReference Include="{p.Name}" Version="{p.Version}" />"""));
        return $"""
              <ItemGroup>
            {references}
              </ItemGroup>
            """;
    }

    /// <summary>
    /// Writes a minimal MSBuild SDK package (<c>Sdk/Sdk.props</c> and <c>Sdk/Sdk.targets</c>) to <paramref name="feed"/>, so a
    /// versioned <c>Sdk="Id/Version"</c> resolves through MSBuild's NuGet SDK resolver from a local feed.
    /// </summary>
    public static void WriteSdkPackage(string feed, string id, string version, string props, string targets = "<Project />")
    {
        Directory.CreateDirectory(feed);
        using ZipArchive package = ZipFile.Open(Path.Combine(feed, $"{id.ToLowerInvariant()}.{version}.nupkg"), ZipArchiveMode.Create);
        Add($"{id}.nuspec", $"""
            <?xml version="1.0"?>
            <package xmlns="http://schemas.microsoft.com/packaging/2013/05/nuspec.xsd">
              <metadata>
                <id>{id}</id>
                <version>{version}</version>
                <authors>tests</authors>
                <description>Test SDK</description>
                <packageTypes>
                  <packageType name="MSBuildSdk" />
                </packageTypes>
              </metadata>
            </package>
            """);
        Add("Sdk/Sdk.props", props);
        Add("Sdk/Sdk.targets", targets);

        void Add(string entryName, string content)
        {
            using StreamWriter writer = new(package.CreateEntry(entryName).Open());
            writer.Write(content);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }
}

/// <summary>A <c>PackageReference</c> to write into a scenario project; <paramref name="Version"/> is omitted when it comes from elsewhere (central management).</summary>
public readonly record struct PackageRef(string Name, string? Version = null)
{
    public static implicit operator PackageRef(string name) => new(name);
}
