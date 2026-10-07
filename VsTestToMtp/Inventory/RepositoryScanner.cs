using System.Text.Json;
using System.Text.RegularExpressions;

namespace VsTestToMtp.Inventory;

/// <summary>
/// Finds repository-level context around the selection: the effective <c>global.json</c> and CI/script files.
/// Read-only; generated and hidden directories are skipped.
/// </summary>
internal static partial class RepositoryScanner
{
    private const long MaxScannedFileBytes = 2 * 1024 * 1024;

    private static readonly HashSet<string> ExcludedDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", "node_modules", "packages", "TestResults", "artifacts",
        ".git", ".vs", ".idea", ".vscode", ".nuget", ".dotnet",
    };

    // Hidden directories that legitimately hold CI definitions.
    private static readonly HashSet<string> HiddenCiDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        ".github", ".azuredevops", ".azure-pipelines", ".circleci", ".gitlab",
    };

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>The nearest <c>global.json</c> at or above <paramref name="startDirectory"/>, as <c>dotnet</c> resolves it.</summary>
    public static GlobalJsonInfo? ReadGlobalJson(string startDirectory, PathFormatter formatter, List<InventoryBlocker> blockers)
    {
        for (DirectoryInfo? directory = new(startDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "global.json");
            if (!File.Exists(path))
            {
                continue;
            }

            try
            {
                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path), JsonOptions);
                JsonElement root = document.RootElement;
                JsonElement sdk = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("sdk", out JsonElement s) ? s : default;
                JsonElement test = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("test", out JsonElement t) ? t : default;
                JsonElement msbuildSdks = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("msbuild-sdks", out JsonElement m) ? m : default;

                return new GlobalJsonInfo(
                    formatter.Format(path),
                    String(sdk, "version"),
                    String(sdk, "rollForward"),
                    String(test, "runner"),
                    msbuildSdks.ValueKind == JsonValueKind.Object
                        ? [.. msbuildSdks.EnumerateObject().Select(p => $"{p.Name}/{p.Value}").Order(StringComparer.Ordinal)]
                        : []);
            }
            catch (JsonException ex)
            {
                blockers.Add(new InventoryBlocker(
                    BlockerCodes.MalformedGlobalJson, BlockerSeverity.Error,
                    $"'{formatter.Format(path)}' is not valid JSON: {ex.Message}",
                    "Fix global.json so the SDK and test runner configuration can be read.",
                    null, formatter.Location(path, (int)(ex.LineNumber ?? 0) + 1, (int)(ex.BytePositionInLine ?? 0) + 1)));
                return null;
            }
        }

        return null;
    }

    /// <summary>CI definitions and scripts under the formatter's root.</summary>
    public static IReadOnlyList<AutomationFile> FindAutomation(PathFormatter formatter)
    {
        List<AutomationFile> files = [];
        Walk(formatter.Root, insideHiddenCiDirectory: false, files, formatter);
        return [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    private static void Walk(string directory, bool insideHiddenCiDirectory, List<AutomationFile> files, PathFormatter formatter)
    {
        foreach (string file in Directory.EnumerateFiles(directory))
        {
            if (Classify(file) is { } kind)
            {
                files.Add(new AutomationFile(formatter.Format(file), kind, MentionsDotNetTest(file)));
            }
        }

        foreach (string child in Directory.EnumerateDirectories(directory))
        {
            string name = Path.GetFileName(child);
            DirectoryInfo info = new(child);
            bool isHidden = name.StartsWith('.') || info.Attributes.HasFlag(FileAttributes.Hidden);
            bool isCi = HiddenCiDirectories.Contains(name);

            if (ExcludedDirectories.Contains(name)
                || info.Attributes.HasFlag(FileAttributes.ReparsePoint)
                || (isHidden && !isCi && !insideHiddenCiDirectory))
            {
                continue;
            }

            Walk(child, insideHiddenCiDirectory || isCi, files, formatter);
        }
    }

    private static AutomationKind? Classify(string file)
    {
        string name = Path.GetFileName(file);
        string extension = Path.GetExtension(file).ToLowerInvariant();
        string? parent = Path.GetFileName(Path.GetDirectoryName(file));
        string? grandParent = Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(file)));

        return extension switch
        {
            ".yml" or ".yaml" when string.Equals(parent, "workflows", StringComparison.OrdinalIgnoreCase)
                && string.Equals(grandParent, ".github", StringComparison.OrdinalIgnoreCase) => AutomationKind.GitHubWorkflow,
            ".yml" or ".yaml" when name.StartsWith("azure-pipelines", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parent, ".azure-pipelines", StringComparison.OrdinalIgnoreCase)
                || string.Equals(parent, ".azuredevops", StringComparison.OrdinalIgnoreCase) => AutomationKind.AzurePipelines,
            ".ps1" => AutomationKind.PowerShellScript,
            ".sh" => AutomationKind.ShellScript,
            ".cmd" or ".bat" => AutomationKind.BatchScript,
            _ when string.Equals(name, "Makefile", StringComparison.OrdinalIgnoreCase) => AutomationKind.Makefile,
            _ => null,
        };
    }

    private static bool MentionsDotNetTest(string file)
    {
        try
        {
            return new FileInfo(file).Length <= MaxScannedFileBytes && DotNetTest().IsMatch(File.ReadAllText(file));
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? String(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out JsonElement value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    [GeneratedRegex(@"\bdotnet\s+test\b", RegexOptions.IgnoreCase)]
    private static partial Regex DotNetTest();
}
