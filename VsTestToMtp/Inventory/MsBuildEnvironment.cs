using System.Runtime.CompilerServices;

using Microsoft.Build.Locator;

namespace VsTestToMtp.Inventory;

/// <summary>
/// Registers the .NET SDK's MSBuild once per process. This type must not reference <c>Microsoft.Build</c>
/// types so that registration can happen before any of them is loaded.
/// </summary>
internal static class MsBuildEnvironment
{
    private static readonly Lock Gate = new();
    private static string? s_registeredPath;
    private static bool s_registeredExternally;

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

            if (s_registeredPath is null && !s_registeredExternally)
            {
                try
                {
                    if (MSBuildLocator.IsRegistered)
                    {
                        // Another component already registered MSBuild; we cannot know which SDK it is.
                        s_registeredExternally = true;
                        return true;
                    }

                    MSBuildLocator.RegisterInstance(instance);
                    s_registeredPath = instance.MSBuildPath;
                    return true;
                }
                catch (InvalidOperationException ex)
                {
                    (errorCode, error) = (BlockerCodes.MsBuildNotFound, ex.Message);
                    return false;
                }
            }

            if (s_registeredExternally || PathComparison.Equal(s_registeredPath!, instance.MSBuildPath))
            {
                return true;
            }

            (errorCode, error) = (BlockerCodes.MsBuildSdkMismatch,
                $"This process already loaded MSBuild from '{s_registeredPath}', but this selection resolves to '{instance.MSBuildPath}' "
                + "(a different global.json or SDK). MSBuild can only be loaded once per process, so evaluating it would use the wrong SDK.");
            return false;
        }
    }
}
