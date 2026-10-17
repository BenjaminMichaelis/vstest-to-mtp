using System.Runtime.CompilerServices;
using System.Text.Json;

using VsTestToMtp.Inventory;

// The request arrives on standard input; the response goes to the file named by the only argument. Standard output is not
// used because native code (hostfxr, while locating an SDK) writes diagnostics straight to it.
if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: vstest-to-mtp-msbuild-host <response file> (JSON HostRequest on standard input)");
    return 2;
}

HostRequest? request = JsonSerializer.Deserialize(Console.OpenStandardInput(), InventoryJsonContext.Default.HostRequest);
if (request is null)
{
    Console.Error.WriteLine("Expected a JSON HostRequest on standard input.");
    return 2;
}

HostResponse response = InventoryHost.Evaluate(request);
using (FileStream output = File.Create(args[0]))
{
    JsonSerializer.Serialize(output, response, InventoryJsonContext.Default.HostResponse);
}

return 0;

/// <summary>
/// Evaluates the requested projects. Kept separate and un-inlined: MSBuild must be registered before anything touching
/// Microsoft.Build is JIT-compiled.
/// </summary>
internal static class InventoryHost
{
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static HostResponse Evaluate(HostRequest request)
    {
        PathFormatter formatter = new(request.RootPath);

        if (MsBuildEnvironment.TryRegister(request.SelectionDirectory) is { } blocker)
        {
            return new HostResponse(
                [blocker],
                [
                    .. request.ProjectPaths.Select(p => new ProjectInventory(
                        Path: formatter.Format(p),
                        Name: Path.GetFileNameWithoutExtension(p),
                        Classification: ProjectClassification.Unknown,
                        HasTestLibraryDependency: false,
                        Frameworks: [],
                        Runners: [],
                        TargetFrameworks: [],
                        Imports: [],
                        Sdks: [],
                        Blockers: [blocker])),
                ]);
        }

        return new HostResponse([], EvaluateRegistered(request, formatter));
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static List<ProjectInventory> EvaluateRegistered(HostRequest request, PathFormatter formatter)
    {
        HashSet<string> selected = new(request.ProjectPaths, PathComparison.Comparer);

        // Evaluation must not extract an SDK package into the repository; see NuGetPackagesScope (it only touches the
        // environment when a NuGet.config could redirect the packages folder, or the caller asked for a directory).
        using NuGetPackagesScope packages = NuGetPackagesScope.Redirect(request.SdkPackagesDirectory, request.ProjectPaths);
        using ProjectEvaluator evaluator = new(formatter, selected);
        return
        [
            .. request.ProjectPaths.Select(p =>
                TestProjectClassifier.Classify(evaluator.Evaluate(p), Path.GetFileNameWithoutExtension(p))),
        ];
    }
}
