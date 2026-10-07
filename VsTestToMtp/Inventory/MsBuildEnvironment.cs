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
    private static string? s_sdkPath;
    private static string? s_error;
    private static bool s_attempted;

    /// <summary>
    /// Registers the SDK that <c>dotnet</c> would select for <paramref name="workingDirectory"/> (honoring <c>global.json</c>).
    /// MSBuild can only be registered once per process, so later calls reuse the first registration.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static bool TryRegister(string workingDirectory, out string? sdkPath, out string? error)
    {
        lock (Gate)
        {
            if (!s_attempted)
            {
                s_attempted = true;
                Register(workingDirectory);
            }

            sdkPath = s_sdkPath;
            error = s_error;
            return s_sdkPath is not null;
        }
    }

    private static void Register(string workingDirectory)
    {
        try
        {
            if (MSBuildLocator.IsRegistered)
            {
                // Another component in this process already registered MSBuild; use it.
                s_sdkPath = AppContext.BaseDirectory;
                return;
            }

            VisualStudioInstanceQueryOptions options = new()
            {
                DiscoveryTypes = DiscoveryType.DotNetSdk,
                WorkingDirectory = workingDirectory,
            };
            VisualStudioInstance? instance = MSBuildLocator.QueryVisualStudioInstances(options)
                .OrderByDescending(i => i.Version)
                .FirstOrDefault();
            if (instance is null)
            {
                s_error = "No .NET SDK was found for the selected directory. Install the SDK that its global.json requires.";
                return;
            }

            MSBuildLocator.RegisterInstance(instance);
            s_sdkPath = instance.MSBuildPath;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            s_error = ex.Message;
        }
    }
}
