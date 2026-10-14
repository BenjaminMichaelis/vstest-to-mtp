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
        ".github", ".azuredevops", ".azure-pipelines", ".circleci",
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
                string json = File.ReadAllText(path);
                using JsonDocument document = JsonDocument.Parse(json, JsonOptions);
                return ParseGlobalJson(document.RootElement, path, formatter, blockers);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                blockers.Add(InventoryBlocker.Error(BlockerCode.UnreadableGlobalJson,
                    $"'{formatter.Format(path)}' could not be read: {ex.Message}",
                    "Grant read access to global.json (or fix the path), then rerun, so the SDK and test runner configuration can be read.", location: formatter.Location(path)));
                return null;
            }
            catch (JsonException ex)
            {
                blockers.Add(InventoryBlocker.Error(BlockerCode.MalformedGlobalJson,
                    $"'{formatter.Format(path)}' is not valid JSON: {ex.Message}",
                    "Fix global.json so the SDK and test runner configuration can be read.", location: formatter.Location(path, (int)(ex.LineNumber ?? 0) + 1, (int)(ex.BytePositionInLine ?? 0) + 1)));
                return null;
            }
        }

        return null;
    }

    /// <summary>
    /// CI definitions and scripts under the formatter's root. A directory that cannot be listed (permissions, deleted
    /// while scanning) is skipped and reported as a blocker so the rest of the inventory stays usable.
    /// </summary>
    public static IReadOnlyList<AutomationFile> FindAutomation(PathFormatter formatter, List<InventoryBlocker> blockers)
    {
        List<AutomationFile> files = [];
        Walk(formatter.Root, files, formatter, blockers);
        return [.. files.OrderBy(f => f.Path, StringComparer.Ordinal)];
    }

    private static void Walk(string directory, List<AutomationFile> files, PathFormatter formatter, List<InventoryBlocker> blockers)
    {
        List<string> descend = [];
        try
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
                    || (isHidden && !isCi))
                {
                    continue;
                }

                descend.Add(child);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            blockers.Add(InventoryBlocker.Warning(BlockerCode.UnreadableDirectory,
                $"'{formatter.Format(directory)}' could not be scanned for CI definitions and scripts: {ex.Message}",
                "Grant read access (or remove the directory), then rerun. Automation files under it are not listed.", location: formatter.Location(directory)));
        }

        foreach (string path in descend)
        {
            Walk(path, files, formatter, blockers);
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
            ".yml" or ".yaml" when string.Equals(parent, ".circleci", StringComparison.OrdinalIgnoreCase) => AutomationKind.CircleCi,
            ".yml" or ".yaml" when name.Equals(".gitlab-ci.yml", StringComparison.OrdinalIgnoreCase)
                || name.Equals(".gitlab-ci.yaml", StringComparison.OrdinalIgnoreCase) => AutomationKind.GitLabCi,
            ".ps1" => AutomationKind.PowerShellScript,
            ".sh" => AutomationKind.ShellScript,
            ".cmd" or ".bat" => AutomationKind.BatchScript,
            _ when string.Equals(name, "Makefile", StringComparison.OrdinalIgnoreCase) => AutomationKind.Makefile,
            _ => null,
        };
    }

    // null when the file could not be inspected (too large, unreadable, removed), so it is never reported as "does not mention it".
    private static bool? MentionsDotNetTest(string file)
    {
        try
        {
            return new FileInfo(file).Length <= MaxScannedFileBytes ? DotNetTest().IsMatch(File.ReadAllText(file)) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads the settings we care about. A syntactically valid file with the wrong shape is reported, not read as "setting absent":
    /// an absent <c>test.runner</c> means VSTest, so silently ignoring a malformed one would change the answer. JSON <c>null</c> and
    /// unknown properties are accepted, as <c>dotnet</c> does.
    /// </summary>
    private static GlobalJsonInfo? ParseGlobalJson(JsonElement root, string path, PathFormatter formatter, List<InventoryBlocker> blockers)
    {
        List<string> problems = [];
        List<string> msbuildSdks = [];
        string? version = null;
        string? rollForward = null;
        string? runner = null;

        if (root.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"the root must be a JSON object, not {Describe(root)}");
        }
        else
        {
            JsonElement sdk = Section(root, "sdk", problems);
            JsonElement test = Section(root, "test", problems);
            JsonElement sdks = Section(root, "msbuild-sdks", problems);
            version = StringValue(sdk, "sdk", "version", problems);
            rollForward = StringValue(sdk, "sdk", "rollForward", problems);
            runner = StringValue(test, "test", "runner", problems);

            if (sdks.ValueKind == JsonValueKind.Object)
            {
                foreach (JsonProperty entry in sdks.EnumerateObject())
                {
                    if (entry.Value.ValueKind == JsonValueKind.String)
                    {
                        msbuildSdks.Add($"{entry.Name}/{entry.Value.GetString()}");
                    }
                    else
                    {
                        problems.Add($"msbuild-sdks.{entry.Name} must be a string, not {Describe(entry.Value)}");
                    }
                }
            }
        }

        if (problems.Count == 0)
        {
            return new GlobalJsonInfo(formatter.Format(path), version, rollForward, runner, [.. msbuildSdks.Order(StringComparer.Ordinal)]);
        }

        foreach (string problem in problems)
        {
            blockers.Add(InventoryBlocker.Error(BlockerCode.MalformedGlobalJson,
                $"'{formatter.Format(path)}' is not a valid global.json: {problem}.",
                "Fix global.json so the SDK and test runner configuration can be read.", location: formatter.Location(path)));
        }

        return null;
    }

    // A section that is absent or null is simply not there; one of the wrong kind is a problem.
    private static JsonElement Section(JsonElement root, string name, List<string> problems)
    {
        if (!root.TryGetProperty(name, out JsonElement section) || section.ValueKind == JsonValueKind.Null)
        {
            return default;
        }

        if (section.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{name} must be an object, not {Describe(section)}");
            return default;
        }

        return section;
    }

    private static string? StringValue(JsonElement section, string sectionName, string name, List<string> problems)
    {
        if (section.ValueKind != JsonValueKind.Object || !section.TryGetProperty(name, out JsonElement value) || value.ValueKind == JsonValueKind.Null)
        {
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
        {
            problems.Add($"{sectionName}.{name} must be a string, not {Describe(value)}");
            return null;
        }

        return value.GetString();
    }

    private static string Describe(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        _ => "null",
    };

    [GeneratedRegex(@"\bdotnet\s+test\b", RegexOptions.IgnoreCase)]
    private static partial Regex DotNetTest();
}
