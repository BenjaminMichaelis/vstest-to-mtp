namespace VsTestToMtp.Inventory;

/// <summary>
/// Points NuGet's global packages folder at a location outside the inventoried repository while MSBuild evaluates.
/// A versioned <c>Sdk="Name/1.2.3"</c> (for example MSTest.Sdk) is resolved by MSBuild's NuGet SDK resolver, which extracts a
/// missing package into the global packages folder, and a <c>NuGet.config</c> in the repository may put that folder inside the
/// repository. <c>NUGET_PACKAGES</c> takes precedence over <c>NuGet.config</c>, so setting it keeps evaluation from writing there.
/// An existing <c>NUGET_PACKAGES</c> is the user's choice and is left alone.
/// </summary>
/// <remarks>
/// The environment is process-wide; evaluations are already serialized, and the previous value (unset) is restored on dispose.
/// </remarks>
internal sealed class NuGetPackagesScope : IDisposable
{
    private const string Variable = "NUGET_PACKAGES";

    private readonly bool _changed;

    private NuGetPackagesScope(string? directory)
    {
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 })
        {
            return;
        }

        Environment.SetEnvironmentVariable(
            Variable,
            directory ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget", "packages"));
        _changed = true;
    }

    /// <summary>Redirects to <paramref name="directory"/>, or to NuGet's per-user default when it is <see langword="null"/>.</summary>
    public static NuGetPackagesScope Redirect(string? directory) => new(directory);

    public void Dispose()
    {
        if (_changed)
        {
            Environment.SetEnvironmentVariable(Variable, null);
        }
    }
}
