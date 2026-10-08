using System.Runtime.CompilerServices;

using Microsoft.Build.Locator;

namespace VsTestToMtp.Inventory;

/// <summary>
/// Registers the .NET SDK's MSBuild once per process. This type must not reference <c>Microsoft.Build</c>
/// types so that registration can happen before any of them is loaded.
/// </summary>
internal static class MsBuildEnvironment
{
    /// <summary>
    /// The oldest .NET SDK whose MSBuild we support. The Microsoft.Build package we compile against (17.8.x) must not be
    /// newer than the oldest MSBuild we load, per the MSBuildLocator guidance; 17.8 ships with SDK 8.0.100.
    /// </summary>
    public const int MinimumSdkMajorVersion = 8;

    private static readonly Lock Gate = new();
    private static string? s_registeredPath;
    private static bool s_registeredExternally;

    // Reads the loaded assembly list by name so this type never references Microsoft.Build types.
    private static string? LoadedMsBuildDirectory() =>
        AppDomain.CurrentDomain.GetAssemblies()
            .FirstOrDefault(a => a.GetName().Name == "Microsoft.Build" && !a.IsDynamic && a.Location.Length > 0)?.Location is { } location
            ? Path.GetDirectoryName(location)
            : null;

    /// <summary>
    /// Makes sure the SDK that <c>dotnet</c> would select for <paramref name="workingDirectory"/> (honoring
    /// <c>global.json</c>) is the registered MSBuild. MSBuild registration is process-wide, so once an SDK has been
    /// registered a selection that resolves to a different SDK cannot be evaluated faithfully and is refused.
    /// Failures are not remembered, so a bad selection never poisons later ones.
    /// </summary>
    /// <param name="errorCode">A <see cref="BlockerCodes"/> value when registration is not possible.</param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryRegister(string workingDirectory, out string? errorCode, out string? error)
    {
        lock (Gate)
        {
            errorCode = null;
            error = null;

            VisualStudioInstance? instance;
            try
            {
                // With a working directory the Locator lists every installed SDK but puts the one that hostfxr resolves
                // for that directory (honoring global.json) first, and throws when global.json cannot be satisfied.
                instance = MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
                {
                    DiscoveryTypes = DiscoveryType.DotNetSdk,
                    WorkingDirectory = workingDirectory,
                }).FirstOrDefault();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                (errorCode, error) = (BlockerCodes.MsBuildNotFound, ex.Message);
                return false;
            }

            if (instance is null)
            {
                (errorCode, error) = (BlockerCodes.MsBuildNotFound,
                    "No .NET SDK was found for the selected directory. Install the SDK that its global.json requires.");
                return false;
            }

            if (instance.Version.Major < MinimumSdkMajorVersion)
            {
                (errorCode, error) = (BlockerCodes.MsBuildSdkUnsupported,
                    $"This selection resolves to .NET SDK {instance.Version}, which is older than the oldest supported SDK ({MinimumSdkMajorVersion}.0); its MSBuild cannot be loaded safely.");
                return false;
            }

            if (s_registeredPath is null && !s_registeredExternally)
            {
                try
                {
                    if (MSBuildLocator.IsRegistered)
                    {
                        // Another component already registered MSBuild; verified against the loaded assembly below.
                        s_registeredExternally = true;
                    }
                    else
                    {
                        MSBuildLocator.RegisterInstance(instance);
                        s_registeredPath = instance.MSBuildPath;
                        return true;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    (errorCode, error) = (BlockerCodes.MsBuildNotFound, ex.Message);
                    return false;
                }
            }

            // When another component registered MSBuild, the only evidence of which SDK it is is where Microsoft.Build was loaded from.
            string? registered = s_registeredExternally ? LoadedMsBuildDirectory() : s_registeredPath;
            if (registered is not null && PathComparison.Equal(registered, instance.MSBuildPath))
            {
                return true;
            }

            (errorCode, error) = (BlockerCodes.MsBuildSdkMismatch, registered is null
                ? $"Another component already registered MSBuild in this process and its SDK cannot be determined, so it cannot be verified to match '{instance.MSBuildPath}' for this selection."
                : $"This process already loaded MSBuild from '{registered}', but this selection resolves to '{instance.MSBuildPath}' "
                  + "(a different global.json or SDK). MSBuild can only be loaded once per process, so evaluating it would use the wrong SDK.");
            return false;
        }
    }
}
