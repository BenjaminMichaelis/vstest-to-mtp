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
    /// <returns><see langword="null"/> when MSBuild is ready to evaluate this selection; otherwise why it is not.</returns>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static InventoryBlocker? TryRegister(string workingDirectory)
    {
        lock (Gate)
        {
            VisualStudioInstance? instance;
            try
            {
                // With a working directory the Locator lists every installed SDK but puts the one that hostfxr resolves
                // for that directory (honoring global.json) first, and throws when global.json cannot be satisfied.
                // By default it also drops SDKs newer than the running runtime, so a global.json pinning one would silently
                // fall through to another SDK; listing all of them (as Roslyn does) keeps the resolved SDK first so it can be refused below.
                // https://github.com/microsoft/MSBuildLocator/blob/ff0b9004f2548ba616d4d1a0f901c842b1d7f993/src/MSBuildLocator/DotNetSdkLocationHelper.cs#L57-L66
                // https://github.com/dotnet/roslyn/blob/33c9ed52c54827abaff4d4a2fb1f45efd7e5ac99/src/Workspaces/MSBuild/BuildHost/NetCoreBuildHost.cs
                instance = MSBuildLocator.QueryVisualStudioInstances(new VisualStudioInstanceQueryOptions
                {
                    DiscoveryTypes = DiscoveryType.DotNetSdk,
                    WorkingDirectory = workingDirectory,
                    AllowAllRuntimeVersions = true,
                }).FirstOrDefault();
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                return NotFound(ex.Message);
            }

            if (instance is null)
            {
                return NotFound("No .NET SDK was found for the selected directory. Install the SDK that its global.json requires.");
            }

            if (CheckSupported(instance.Version, Environment.Version) is { } unsupported)
            {
                return unsupported;
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
                        return null;
                    }
                }
                catch (InvalidOperationException ex)
                {
                    return NotFound(ex.Message);
                }
            }

            // When another component registered MSBuild, the only evidence of which SDK it is is where Microsoft.Build was loaded from.
            string? registered = s_registeredExternally ? LoadedMsBuildDirectory() : s_registeredPath;
            return registered is not null && PathComparison.Equal(registered, instance.MSBuildPath)
                ? null
                : Blocker(
                    BlockerCodes.MsBuildSdkMismatch,
                    registered is null
                        ? $"Another component already registered MSBuild in this process and its SDK cannot be determined, so it cannot be verified to match '{instance.MSBuildPath}' for this selection."
                        : $"This process already loaded MSBuild from '{registered}', but this selection resolves to '{instance.MSBuildPath}' "
                          + "(a different global.json or SDK). MSBuild can only be loaded once per process, so evaluating it would use the wrong SDK.",
                    "Inventory repositories that need different SDKs in separate processes (one vstest-to-mtp run per global.json).");
        }
    }

    /// <summary>Whether the SDK's MSBuild can be loaded into this process at all.</summary>
    private static InventoryBlocker? CheckSupported(Version sdk, Version runtime)
    {
        if (sdk.Major < MinimumSdkMajorVersion)
        {
            return Blocker(
                BlockerCodes.MsBuildSdkUnsupported,
                $"This selection resolves to .NET SDK {sdk}, which is older than the oldest supported SDK ({MinimumSdkMajorVersion}.0); its MSBuild cannot be loaded safely.",
                $"Use a global.json that selects .NET SDK {MinimumSdkMajorVersion}.0 or newer (the repository can be migrated with a newer SDK even if it targets older frameworks), then rerun.");
        }

        // The same rule the Locator applies by default: an SDK's MSBuild needs at least the runtime it shipped with.
        bool newerThanRuntime = sdk.Major > runtime.Major || (sdk.Major == runtime.Major && sdk.Minor > runtime.Minor);
        return newerThanRuntime
            ? Blocker(
                BlockerCodes.MsBuildSdkNewerThanRuntime,
                $"This selection resolves to .NET SDK {sdk}, whose MSBuild needs a newer .NET runtime than the {runtime.Major}.{runtime.Minor} runtime vstest-to-mtp runs on.",
                $"Use a vstest-to-mtp release built for .NET {sdk.Major}.{sdk.Minor} or later, or a global.json that selects an SDK no newer than {runtime.Major}.{runtime.Minor}, then rerun.")
            : null;
    }

    private static InventoryBlocker NotFound(string error) => Blocker(
        BlockerCodes.MsBuildNotFound,
        $"MSBuild could not be located: {error}",
        "Install the .NET SDK that the repository's global.json selects, then rerun.");

    private static InventoryBlocker Blocker(string code, string message, string remediation) =>
        new(code, BlockerSeverity.Error, message, remediation, null, null);
}
