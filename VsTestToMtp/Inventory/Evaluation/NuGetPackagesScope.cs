namespace VsTestToMtp.Inventory;

/// <summary>
/// Points NuGet's global packages folder at a location outside the inventoried repository while MSBuild evaluates, but only when
/// that is needed. A versioned <c>Sdk="Name/1.2.3"</c> (for example MSTest.Sdk) is resolved by MSBuild's NuGet SDK resolver, which
/// extracts a missing package into the global packages folder, and a <c>NuGet.config</c> in the repository may put that folder inside
/// the repository. <c>NUGET_PACKAGES</c> takes precedence over <c>NuGet.config</c>, so setting it keeps evaluation from writing there.
/// </summary>
/// <remarks>
/// <para>
/// The environment is process-wide, so it is left alone unless there is a reason: an existing <c>NUGET_PACKAGES</c> is the user's choice;
/// without a <c>NuGet.config</c> that sets <c>globalPackagesFolder</c> NuGet already uses its per-user default, which is outside the
/// repository; only an explicit directory from the caller, or such a <c>NuGet.config</c>, makes the scope set the variable.
/// </para>
/// <para>
/// When it does set it, evaluations are already serialized and the previous value (unset) is restored on dispose.
/// </para>
/// </remarks>
internal sealed class NuGetPackagesScope : IDisposable
{
    private const string Variable = "NUGET_PACKAGES";

    private readonly bool _changed;

    private NuGetPackagesScope(string? directory, IEnumerable<string> projectPaths)
    {
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 })
        {
            return;
        }

        if (directory is null && !RepositoryRedirectsPackagesFolder(projectPaths))
        {
            return;
        }

        Environment.SetEnvironmentVariable(
            Variable,
            directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
        _changed = true;
    }

    /// <summary>
    /// Redirects to <paramref name="directory"/> when given, or to NuGet's per-user default when a <c>NuGet.config</c> that applies to one of
    /// <paramref name="projectPaths"/> sets <c>globalPackagesFolder</c>; otherwise changes nothing.
    /// </summary>
    public static NuGetPackagesScope Redirect(string? directory, IEnumerable<string> projectPaths) => new(directory, projectPaths);

    /// <summary>
    /// Whether any <c>NuGet.config</c> in a project's directory or above it mentions <c>globalPackagesFolder</c>. NuGet reads settings
    /// from the project directory upwards, so these are the files that can move the folder. A match is by text, so a commented-out entry
    /// also counts: redirecting needlessly is harmless, missing a real one is not. A file that cannot be read counts too.
    /// </summary>
    private static bool RepositoryRedirectsPackagesFolder(IEnumerable<string> projectPaths)
    {
        HashSet<string> visited = new(PathComparison.Comparer);
        foreach (string projectPath in projectPaths)
        {
            // Stop at a directory that was already visited: everything above it was visited with it.
            for (DirectoryInfo? directory = new FileInfo(projectPath).Directory; directory is not null && visited.Add(directory.FullName); directory = directory.Parent)
            {
                try
                {
                    foreach (FileInfo file in directory.EnumerateFiles().Where(f => string.Equals(f.Name, "nuget.config", StringComparison.OrdinalIgnoreCase)))
                    {
                        if (File.ReadAllText(file.FullName).Contains("globalPackagesFolder", StringComparison.OrdinalIgnoreCase))
                        {
                            return true;
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return true;
                }
            }
        }

        return false;
    }

    public void Dispose()
    {
        if (_changed)
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }
    }
}
