using System.Diagnostics.CodeAnalysis;

using VsTestToMtp.Inventory;

namespace VsTestToMtp.Migration;

/// <summary>
/// Decides, before anything is changed, which inventoried projects can move to Microsoft.Testing.Platform v2 under a
/// <see cref="MigrationRuleSet"/>, which are already there and which are blocked, and plans the in-family package changes the move needs.
/// It works only from the inventory's evaluated state (properties, package references and their declarations); file and project
/// names never decide anything. Whatever cannot be decided is an explicit error diagnostic, never a guess: a test framework is never
/// migrated (xUnit v2 stays xUnit v2) and a project is never retargeted.
/// </summary>
public static class CompatibilityPreflight
{
    public static PreflightResult Assess(InventoryResult inventory, MigrationRuleSet? rules = null)
    {
        rules ??= MigrationRuleSet.Current;
        List<PreflightDiagnostic> selection = [];
        foreach (InventoryBlocker blocker in inventory.Blockers.Where(b => b.Project is null && b.Severity != BlockerSeverity.Info))
        {
            selection.Add(PreflightDiagnostic.Error(PreflightCode.UnresolvedSelection,
                $"The selection could not be inventoried completely ({blocker.Code}): {blocker.Message}",
                blocker.Remediation,
                location: blocker.Location));
        }

        List<(ProjectInventory Inventory, ProjectPreflight Preflight)> projects =
            [.. inventory.Projects.Select(p => (p, new ProjectAssessment(p, inventory, rules).Assess()))];

        AddSdkDiagnostics(inventory, rules, selection);
        AddMixedRunnerDiagnostics(projects, inventory, selection);

        return new PreflightResult(
            rules.Id,
            rules.Version,
            [.. projects.Select(p => p.Preflight)],
            PreflightDiagnostic.Normalize(selection.Concat(projects.SelectMany(p => p.Preflight.Diagnostics))));
    }

    // MTP v2 only runs under the MTP mode of `dotnet test`, which the .NET 10 SDK introduced. The SDK MSBuild evaluated with is the one
    // global.json resolves to for the selection, and every evaluated project reports it as NETCoreSdkVersion.
    private static void AddSdkDiagnostics(InventoryResult inventory, MigrationRuleSet rules, List<PreflightDiagnostic> diagnostics)
    {
        string[] versions = [.. inventory.Projects
            .SelectMany(p => p.TargetFrameworks)
            .Select(tf => tf.GetProperty("NETCoreSdkVersion")?.Value)
            .OfType<string>()
            .Where(v => v.Length > 0)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)];
        foreach (string version in versions)
        {
            if (!PackageVersion.TryParse(version, out PackageVersion? sdk) || sdk < rules.MinimumSdkVersion)
            {
                diagnostics.Add(PreflightDiagnostic.Error(PreflightCode.UnsupportedSdk,
                    $"The selection is evaluated with .NET SDK {version}, but Microsoft.Testing.Platform v2 needs the MTP mode of `dotnet test`, which the .NET SDK {rules.MinimumSdkVersion} and later provide.",
                    $"Install the .NET SDK {rules.MinimumSdkVersion.Major} or later and allow it in global.json (sdk.version/rollForward), then rerun.",
                    location: inventory.GlobalJson is { } globalJson ? new SourceLocation(globalJson.Path, 0, 0) : null));
            }
        }
    }

    // global.json's test.runner switches every project `dotnet test` runs from that directory: the MTP mode refuses VSTest projects
    // ("All projects must use that test runner") and the VSTest mode cannot run MTP v2 ones. So migrating only some test applications
    // of a run configuration leaves it unrunnable, and a project that could not be classified may be either.
    private static void AddMixedRunnerDiagnostics(
        List<(ProjectInventory Inventory, ProjectPreflight Preflight)> projects,
        InventoryResult inventory,
        List<PreflightDiagnostic> diagnostics)
    {
        string[] mtp = [.. projects
            .Where(p => p.Preflight.Readiness is MigrationReadiness.Eligible or MigrationReadiness.AlreadyMigrated)
            .Select(p => p.Inventory.Path)];
        if (mtp.Length == 0)
        {
            return;
        }

        string[] vstest = [.. projects
            .Where(p => p.Preflight.Readiness == MigrationReadiness.Blocked && p.Inventory.Classification == ProjectClassification.TestApplication
                && (p.Inventory.Runners.Count == 0 || p.Inventory.Runners.Contains(TestRunnerKind.VSTest)))
            .Select(p => p.Inventory.Path)];
        string[] undetermined = [.. projects
            .Where(p => p.Inventory.Classification is ProjectClassification.Unknown or ProjectClassification.Mixed)
            .Select(p => p.Inventory.Path)];
        if (vstest.Length == 0 && undetermined.Length == 0)
        {
            return;
        }

        string runner = inventory.GlobalJson?.TestRunner is { Length: > 0 } configured ? configured : "VSTest (no test.runner)";
        string where = inventory.GlobalJson is { } globalJson ? $"'{globalJson.Path}' selects {runner}" : "There is no global.json, so `dotnet test` uses VSTest";
        string remaining = string.Join("; ", [
            .. vstest.Length > 0 ? [$"would stay on VSTest: {string.Join(", ", vstest)}"] : Array.Empty<string>(),
            .. undetermined.Length > 0 ? [$"could not be classified: {string.Join(", ", undetermined)}"] : Array.Empty<string>()]);
        diagnostics.Add(PreflightDiagnostic.Error(PreflightCode.MixedTestRunners,
            $"Migrating would mix test runners in one `dotnet test` run configuration. {where} for every project in it; these would run on Microsoft.Testing.Platform: {string.Join(", ", mtp)}, but these {remaining}.",
            "Resolve the blocked projects first, or migrate a selection (solution or global.json scope) that does not contain them.",
            location: inventory.GlobalJson is { } json ? new SourceLocation(json.Path, 0, 0) : null) with
        {
            RelatedLocations = [.. vstest.Concat(undetermined).Select(p => new SourceLocation(p, 0, 0))],
        });
    }

    /// <summary>The rules applied to one project.</summary>
    private sealed class ProjectAssessment(ProjectInventory project, InventoryResult inventory, MigrationRuleSet rules)
    {
        private readonly List<PreflightDiagnostic> _diagnostics = [];
        private readonly List<PackageChange> _changes = [];
        private readonly PackageReferenceState[] _packages =
        [
            .. project.TargetFrameworks
                .SelectMany(tf => tf.Packages)
                .DistinctBy(p => (p.Name.ToUpperInvariant(), p.Version, p.Definition.Location)),
        ];

        private SourceLocation ProjectLocation => new(project.Path, 0, 0);

        private bool UsesMSTestSdk => AnyTrue("UsingMSTestSdk");

        public ProjectPreflight Assess()
        {
            AddInventoryDiagnostics();

            switch (project.Classification)
            {
                case ProjectClassification.Production:
                    return Result(MigrationReadiness.NotATestApplication, framework: null);

                case ProjectClassification.Unknown or ProjectClassification.Mixed:
                    if (!_diagnostics.Any(d => d.Severity == BlockerSeverity.Error))
                    {
                        Error(PreflightCode.UnclassifiedProject,
                            $"'{project.Path}' could not be classified as a test application or not ({project.Classification}), so it is not migrated.",
                            "Resolve the inventory blockers for this project, or make IsTestProject and the test packages consistent across target frameworks.");
                    }

                    return Result(MigrationReadiness.Blocked, framework: null);
            }

            TestFramework? framework = Framework();
            bool runnerEnabled = framework switch
            {
                TestFramework.MSTest => AssessMSTest(),
                TestFramework.NUnit => AssessNUnit(),
                TestFramework.XUnitV3 => AssessXUnitV3(),
                TestFramework.TUnit => AssessTUnit(),
                _ => false,
            };

            if (framework is { } known)
            {
                AssessTargetFrameworks(known);
            }

            AssessExtensions();

            bool blocked = _diagnostics.Any(d => d.Severity == BlockerSeverity.Error);
            if (!blocked && runnerEnabled && _changes.Count == 0)
            {
                return Result(MigrationReadiness.AlreadyMigrated, framework);
            }

            // A project that already runs on MTP v2 keeps whatever entry point it has; one that still has to move must use the generated one.
            AssessEntryPoint();
            return Result(_diagnostics.Any(d => d.Severity == BlockerSeverity.Error) ? MigrationReadiness.Blocked : MigrationReadiness.Eligible, framework);
        }

        private ProjectPreflight Result(MigrationReadiness readiness, TestFramework? framework) => new(
            project.Path,
            readiness,
            framework,
            UsesMSTestSdk,
            [.. _changes
                .DistinctBy(c => (c.Kind, c.Package.ToUpperInvariant(), c.Reference, c.ToPackage.ToUpperInvariant(), c.ToVersion))
                .OrderBy(c => c.Package, StringComparer.OrdinalIgnoreCase)
                .ThenBy(c => c.Reference.File, StringComparer.Ordinal)
                .ThenBy(c => c.Reference.Line)],
            PreflightDiagnostic.Normalize(_diagnostics));

        // Anything the inventory could not determine about a test application makes its migration a guess, including environment- or
        // order-dependent conditions (inventory warnings). For a production project it is reported, but nothing is migrated anyway.
        private void AddInventoryDiagnostics()
        {
            bool isProduction = project.Classification == ProjectClassification.Production;
            foreach (InventoryBlocker blocker in project.Blockers.Where(b => b.Severity != BlockerSeverity.Info))
            {
                PreflightDiagnostic diagnostic = new(
                    PreflightCode.UnresolvedProjectState,
                    isProduction ? BlockerSeverity.Warning : BlockerSeverity.Error,
                    $"The inventory could not fully determine '{project.Path}' ({blocker.Code}): {blocker.Message}",
                    blocker.Remediation,
                    project.Path,
                    blocker.Location)
                {
                    RelatedLocations = blocker.RelatedLocations,
                };
                _diagnostics.Add(diagnostic);
            }
        }

        private TestFramework? Framework()
        {
            TestFramework[] frameworks = [.. project.Frameworks];
            if (frameworks.Contains(TestFramework.XUnitV2))
            {
                PackageReferenceState? v2 = Packages("xunit", "xunit.core").FirstOrDefault();
                Error(PreflightCode.XUnitV2NotSupported,
                    $"'{project.Path}' uses xUnit v2{(v2 is null ? string.Empty : $" ({v2.Name} {v2.Version})")}, which does not run on Microsoft.Testing.Platform v2.",
                    "Migrating xUnit v2 to xUnit v3 changes the test framework, which this tool does not do. Migrate the project to xUnit v3 (https://xunit.net/docs/getting-started/v3/migration) and rerun, or keep it on VSTest in a separate run configuration.",
                    v2?.Definition.Location);
            }

            if (frameworks.Length > 1)
            {
                Error(PreflightCode.MultipleTestFrameworks,
                    $"'{project.Path}' references more than one test framework ({string.Join(", ", frameworks)}), so the rules cannot pick one.",
                    "Keep one test framework per test application, then rerun.");
                return null;
            }

            if (frameworks.Length == 0)
            {
                Error(PreflightCode.UnsupportedTestFramework,
                    $"'{project.Path}' is a test application, but none of its packages is a test framework the rules support (MSTest, NUnit, xUnit v3, TUnit).",
                    "Reference the test framework with a supported package, or set IsTestProject=false if it is not a test application.");
                return null;
            }

            return frameworks[0] == TestFramework.XUnitV2 ? null : frameworks[0];
        }

        // MSTest.Sdk turns MTP on unless UseVSTest=true; package-based MSTest turns it on with EnableMSTestRunner=true.
        private bool AssessMSTest()
        {
            bool sdkUpgrade = false;
            if (UsesMSTestSdk)
            {
                sdkUpgrade = AssessMSTestSdk();
            }
            else if (!Packages("MSTest", "MSTest.TestAdapter").Any())
            {
                Error(PreflightCode.MissingRunnerPackage,
                    $"'{project.Path}' references MSTest without MSTest.TestAdapter (or the MSTest package), so nothing runs its tests.",
                    "Reference MSTest (or MSTest.TestAdapter), then rerun.");
            }

            ApplyFamilyRules(TestFramework.MSTest, sdkUpgrade);
            return UsesMSTestSdk ? !AnyTrue("UseVSTest") : AllTrue("EnableMSTestRunner");
        }

        /// <summary>Plans an MSTest.Sdk upgrade when it is on MTP v1; returns whether one was planned.</summary>
        private bool AssessMSTestSdk()
        {
            PackageRule rule = rules.FindPackage(rules.MSTestSdk)!;
            SdkReferenceState? sdk = project.Sdks.FirstOrDefault(s => string.Equals(s.Name, rules.MSTestSdk, StringComparison.OrdinalIgnoreCase));
            string? version = sdk?.ResolvedVersion ?? sdk?.DeclaredVersion;
            if (sdk is null || !PackageVersion.TryParse(version, out PackageVersion? resolved))
            {
                // Without the reference the version cannot be edited, but MSTest.Sdk itself says which MTP it brings.
                string? platform = project.TargetFrameworks.Select(tf => tf.GetProperty("MicrosoftTestingPlatformVersion")?.Value).FirstOrDefault(v => v is { Length: > 0 });
                if (!PackageVersion.TryParse(platform, out PackageVersion? mtp) || mtp.Major < 2)
                {
                    Error(PreflightCode.PackageNotInRepository,
                        $"'{project.Path}' uses MSTest.Sdk, but its version could not be read from a repository file (MicrosoftTestingPlatformVersion is '{platform}').",
                        "Reference MSTest.Sdk with a version in the project (Sdk=\"MSTest.Sdk/x.y.z\") or in global.json msbuild-sdks, then rerun.");
                }

                return false;
            }

            if (resolved >= rule.MinimumVersion!)
            {
                return false;
            }

            SourceLocation versionLocation = sdk.DeclaredVersion is null && inventory.GlobalJson is { } globalJson
                ? new SourceLocation(globalJson.Path, 0, 0)
                : sdk.Location;
            _changes.Add(new PackageChange(
                PackageChangeKind.SdkUpgrade,
                sdk.Name,
                resolved.ToString(),
                sdk.Name,
                rule.UpgradeTo!.ToString(),
                $"{sdk.Name} {resolved} runs on Microsoft.Testing.Platform v1; {rule.Evidence}",
                sdk.Location,
                versionLocation));
            return true;
        }

        private bool AssessNUnit()
        {
            foreach (PackageReferenceState legacy in Packages("NUnitTestAdapter"))
            {
                Error(PreflightCode.UnsupportedTestFramework,
                    $"'{project.Path}' uses the NUnit 2 adapter (NUnitTestAdapter {legacy.Version}), which has no Microsoft.Testing.Platform support.",
                    "Moving to NUnit 3 and NUnit3TestAdapter is a framework migration this tool does not do; do it first, then rerun.",
                    legacy.Definition.Location);
            }

            if (!Packages("NUnit3TestAdapter").Any())
            {
                Error(PreflightCode.MissingRunnerPackage,
                    $"'{project.Path}' references NUnit without NUnit3TestAdapter, which is what runs NUnit tests on Microsoft.Testing.Platform.",
                    "Reference NUnit3TestAdapter (or run the tests some other way and set IsTestProject=false), then rerun.");
            }

            ApplyFamilyRules(TestFramework.NUnit, sdkUpgrade: false);
            return AllTrue("EnableNUnitRunner");
        }

        // Every xUnit v3 package except .mtp-off runs on MTP; MTP v2 needs the .mtp-v2 variant, or the default variant from 4.0.0.
        private bool AssessXUnitV3()
        {
            foreach (PackageReferenceState package in _packages)
            {
                if (!TrySplitXUnitV3(package.Name, out string? baseName, out string? variant))
                {
                    continue;
                }

                if (!TryResolve(package, out PackageVersion? version)
                    || variant == ".mtp-v2"
                    || (variant.Length == 0 && version >= rules.XUnitV3DefaultsToMtpV2))
                {
                    continue;
                }

                PackageVersion target = version >= rules.XUnitV3MtpV2Variant ? version : rules.XUnitV3UpgradeTo;
                string current = variant.Length == 0 ? $"defaults to Microsoft.Testing.Platform v1 before {rules.XUnitV3DefaultsToMtpV2}"
                    : variant == ".mtp-off" ? "turns Microsoft.Testing.Platform off"
                    : "selects Microsoft.Testing.Platform v1";
                string upgrade = target == version ? "same version" : $"{rules.XUnitV3MtpV2Variant} is the first version with the .mtp-v2 variant";
                Plan(package, PackageChangeKind.Replace, baseName + ".mtp-v2", target,
                    $"{package.Name} {package.Version} {current}; {baseName}.mtp-v2 is the same xUnit v3 on Microsoft.Testing.Platform v2 ({upgrade}).");
            }

            return true;
        }

        private bool AssessTUnit()
        {
            ApplyFamilyRules(TestFramework.TUnit, sdkUpgrade: false);
            return true;
        }

        private void AssessExtensions()
        {
            foreach (PackageReferenceState package in _packages)
            {
                if (rules.FindPackage(package.Name) is not { } rule)
                {
                    continue;
                }

                if (rule.Kind == PackageRuleKind.Extension)
                {
                    ApplyRule(package, rule, sdkUpgrade: _changes.Any(c => c.Kind == PackageChangeKind.SdkUpgrade));
                }
                else if (rule.Kind == PackageRuleKind.VsTestOnly)
                {
                    _diagnostics.Add(PreflightDiagnostic.Warning(PreflightCode.VsTestOnlyPackage,
                        $"{package.Name} {package.Version} in '{project.Path}' only works with VSTest and does nothing under Microsoft.Testing.Platform. {rule.Evidence}",
                        "Plan its replacement with the migration so its output (for example coverage) is not lost.",
                        project.Path,
                        package.Definition.Location));
                }
            }
        }

        private void ApplyFamilyRules(TestFramework framework, bool sdkUpgrade)
        {
            foreach (PackageReferenceState package in _packages)
            {
                if (rules.FindPackage(package.Name) is { Kind: PackageRuleKind.Framework } rule && rule.Framework == framework)
                {
                    ApplyRule(package, rule, sdkUpgrade);
                }
            }
        }

        private void ApplyRule(PackageReferenceState package, PackageRule rule, bool sdkUpgrade)
        {
            if (!TryResolve(package, out PackageVersion? version) || version >= rule.MinimumVersion!)
            {
                return;
            }

            if (rule.UpgradeTo is null)
            {
                Error(PreflightCode.UnsupportedTestFramework,
                    $"{package.Name} {package.Version} in '{project.Path}' does not run on Microsoft.Testing.Platform v2, and getting it there means migrating the test framework. {rule.Evidence}",
                    $"Upgrade {package.Name} to {rule.MinimumVersion} or later yourself (a framework migration this tool does not do), then rerun.",
                    package.Definition.Location);
                return;
            }

            // A package the SDK declares follows the SDK: upgrading MSTest.Sdk also upgrades the MSTest and MTP packages it brings,
            // unless the repository pins their versions with MSTest.Sdk's own properties.
            if (!package.Definition.IsRepositoryFile && sdkUpgrade && !PinsMSTestSdkVersions())
            {
                return;
            }

            Plan(package, PackageChangeKind.Upgrade, package.Name, rule.UpgradeTo,
                $"{package.Name} {package.Version} does not run on Microsoft.Testing.Platform v2. {rule.Evidence}");
        }

        private bool PinsMSTestSdkVersions() => project.TargetFrameworks.Any(tf =>
            tf.GetProperty("MSTestVersion")?.Definition is { IsRepositoryFile: true }
            || tf.GetProperty("MicrosoftTestingPlatformVersion")?.Definition is { IsRepositoryFile: true });

        private void Plan(PackageReferenceState package, PackageChangeKind kind, string toPackage, PackageVersion toVersion, string reason)
        {
            if (!package.Definition.IsRepositoryFile || package.VersionDefinition is { IsRepositoryFile: false })
            {
                Provenance declared = package.Definition.IsRepositoryFile ? package.VersionDefinition! : package.Definition;
                Error(PreflightCode.PackageNotInRepository,
                    $"{package.Name} {package.Version} in '{project.Path}' needs to become {toPackage} {toVersion}, but it is declared in '{declared.Location.File}', which belongs to an SDK or package, not the repository. {reason}",
                    package.Definition.IsRepositoryFile
                        ? $"Declare the version of {package.Name} in the repository (a PackageVersion or Version), or upgrade whatever provides it, then rerun."
                        : $"Upgrade the SDK or package that adds {package.Name}, or override its version in the repository, then rerun.",
                    declared.Location);
                return;
            }

            _changes.Add(new PackageChange(
                kind,
                package.Name,
                package.Version!,
                toPackage,
                toVersion.ToString(),
                reason,
                package.Definition.Location,
                package.VersionDefinition?.Location));
        }

        private bool TryResolve(PackageReferenceState package, [NotNullWhen(true)] out PackageVersion? version)
        {
            if (PackageVersion.TryResolveMinimum(package.Version, out version))
            {
                return true;
            }

            Error(PreflightCode.UnresolvableVersion,
                package.Version is null
                    ? $"{package.Name} in '{project.Path}' has no version, so whether it runs on Microsoft.Testing.Platform v2 cannot be decided."
                    : $"{package.Name} in '{project.Path}' has the version '{package.Version}', which only a feed can resolve (a floating version or an open range), so whether it runs on Microsoft.Testing.Platform v2 cannot be decided offline.",
                $"Give {package.Name} a specific version (or an inclusive lower bound such as [1.2.3,)), then rerun.",
                (package.VersionDefinition ?? package.Definition).Location);
            return false;
        }

        // Runtime targets come from the evaluated TargetFrameworkIdentifier/TargetFrameworkVersion of each target framework, not from its name.
        private void AssessTargetFrameworks(TestFramework framework)
        {
            IReadOnlyList<RuntimeTarget> supported = rules.RuntimeTargetsFor(framework);
            string allowed = string.Join(" or ", supported.Select(t => $"{t.Moniker} or later"));
            foreach (TargetFrameworkState state in project.TargetFrameworks)
            {
                string identifier = state.GetProperty("TargetFrameworkIdentifier")?.Value ?? string.Empty;
                string versionText = (state.GetProperty("TargetFrameworkVersion")?.Value ?? string.Empty).TrimStart('v', 'V');
                RuntimeTarget? target = supported.FirstOrDefault(t => string.Equals(t.Identifier, identifier, StringComparison.OrdinalIgnoreCase));
                if (target is not null && PackageVersion.TryParse(versionText, out PackageVersion? version) && version >= target.MinimumVersion)
                {
                    continue;
                }

                string evaluated = identifier.Length == 0 ? "no TargetFrameworkIdentifier" : $"{identifier} {versionText}";
                Error(PreflightCode.UnsupportedTargetFramework,
                    $"'{project.Path}' targets {state.TargetFramework} ({evaluated}), which {framework} on Microsoft.Testing.Platform v2 does not support; it supports {allowed}.",
                    "Retargeting changes what the tests run on, so it is never done automatically. Retarget the project (or drop the target framework) yourself, then rerun.",
                    (state.GetProperty("TargetFrameworks")?.Definition ?? state.GetProperty("TargetFramework")?.Definition)?.Location ?? ProjectLocation);
            }
        }

        // Microsoft.Testing.Platform generates the entry point of a test application. A project with its own (StartupObject, or the
        // generated one turned off) would end up with two, or one that does not start the platform.
        private void AssessEntryPoint()
        {
            foreach (TargetFrameworkState state in project.TargetFrameworks)
            {
                if (state.GetProperty("StartupObject") is { Value.Length: > 0 } startup)
                {
                    CustomEntryPoint(state, $"sets StartupObject to '{startup.Value}'", startup);
                }

                foreach (string name in new[] { "GenerateProgramFile", "GenerateTestingPlatformEntryPoint" })
                {
                    if (state.GetProperty(name) is { } property && MsBuildBoolean.TryParse(property.Value, out bool generate) && !generate)
                    {
                        CustomEntryPoint(state, $"sets {name}={property.Value}", property);
                    }
                }
            }
        }

        private void CustomEntryPoint(TargetFrameworkState state, string what, PropertyState property) => Error(PreflightCode.CustomEntryPoint,
            $"'{project.Path}' {what} for {state.TargetFramework}, so it supplies its own entry point instead of the generated one, which Microsoft.Testing.Platform needs.",
            "Remove the custom entry point (or move it into Microsoft.Testing.Platform's TestApplication builder yourself), then rerun.",
            property.Definition?.Location ?? ProjectLocation);

        private bool AnyTrue(string property) =>
            project.TargetFrameworks.Any(tf => MsBuildBoolean.TryParse(tf.GetProperty(property)?.Value, out bool value) && value);

        private bool AllTrue(string property) => project.TargetFrameworks.Count > 0 &&
            project.TargetFrameworks.All(tf => MsBuildBoolean.TryParse(tf.GetProperty(property)?.Value, out bool value) && value);

        private IEnumerable<PackageReferenceState> Packages(params string[] names) =>
            _packages.Where(p => names.Contains(p.Name, StringComparer.OrdinalIgnoreCase));

        private void Error(PreflightCode code, string message, string remediation, SourceLocation? location = null) =>
            _diagnostics.Add(PreflightDiagnostic.Error(code, message, remediation, project.Path, location ?? ProjectLocation));

        private bool TrySplitXUnitV3(string package, [NotNullWhen(true)] out string? baseName, [NotNullWhen(true)] out string? variant)
        {
            foreach (string candidate in rules.XUnitV3Packages.OrderByDescending(p => p.Length))
            {
                if (package.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                {
                    string suffix = package[candidate.Length..].ToLowerInvariant();
                    if (suffix is "" or ".mtp-off" or ".mtp-v1" or ".mtp-v2")
                    {
                        baseName = candidate;
                        variant = suffix;
                        return true;
                    }
                }
            }

            baseName = null;
            variant = null;
            return false;
        }
    }
}
