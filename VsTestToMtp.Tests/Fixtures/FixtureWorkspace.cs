namespace VsTestToMtp.Tests.Fixtures;

using System.Security.Cryptography;
using System.Text;

/// <summary>
/// An isolated, disposable copy of a committed fixture. Conversion tests mutate the copy so the
/// committed originals stay untouched.
/// </summary>
public sealed class FixtureWorkspace : IDisposable
{
    private static readonly string[] ExcludedDirectories = ["bin", "obj", "TestResults"];

    private FixtureWorkspace(FixtureInfo fixture, string rootPath)
    {
        Fixture = fixture;
        RootPath = rootPath;
    }

    public FixtureInfo Fixture { get; }

    /// <summary>Root of the copy; contains the fixture's own global.json and Directory.*.props.</summary>
    public string RootPath { get; }

    public static FixtureWorkspace Create(FixtureInfo fixture)
    {
        // Outside the repository on purpose: nothing from the repo root can leak into the copy.
        string rootPath = Path.Combine(Path.GetTempPath(), "vstest-to-mtp-fixtures", Guid.NewGuid().ToString("N"));
        foreach (string file in EnumerateFiles(fixture.RootPath))
        {
            string target = Path.Combine(rootPath, Path.GetRelativePath(fixture.RootPath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return new FixtureWorkspace(fixture, rootPath);
    }

    /// <summary>
    /// Content hash of a fixture directory (relative paths and bytes), ignoring build output.
    /// Used to prove committed originals were not modified.
    /// </summary>
    public static string ComputeHash(string directory)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (string file in EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            string relativePath = Path.GetRelativePath(directory, file).Replace('\\', '/');
            hash.AppendData(Encoding.UTF8.GetBytes(relativePath + "\n"));
            hash.AppendData(File.ReadAllBytes(file));
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    public void Dispose()
    {
        if (Directory.Exists(RootPath))
        {
            Directory.Delete(RootPath, recursive: true);
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory) =>
        Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(directory, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => ExcludedDirectories.Contains(segment, StringComparer.OrdinalIgnoreCase)));
}
