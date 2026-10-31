using System.Diagnostics.CodeAnalysis;

using NuGet.Versioning;

using VsTestToMtp.Inventory;

namespace VsTestToMtp.Migration;

/// <summary>What a framework's rules found about a project.</summary>
/// <param name="IsOnMtpV2">Whether the project already runs on MTP v2 as it is configured: runner enabled and every framework package on MTP v2.</param>
/// <param name="SdkUpgradePlanned">Whether an upgrade of the project's SDK (MSTest.Sdk) was planned, which also moves the packages that SDK declares.</param>
internal readonly record struct FrameworkVerdict(bool IsOnMtpV2, bool SdkUpgradePlanned);

/// <summary>
/// The rules applied to one project: the steps every project goes through (inventory state, classification, test framework, SDK,
/// target frameworks, extensions, entry point), and the context the framework rules (<see cref="MSTestRules"/>, <see cref="NUnitRules"/>,
/// <see cref="XUnitV3Rules"/>, <see cref="TUnitRules"/>) work with. Findings go to <see cref="Error"/> and <see cref="Plan"/>.
/// </summary>
internal sealed class ProjectAssessment
{
    private readonly List<PreflightDiagnostic> _diagnostics = [];
    private readonly List<PackageChange> _changes = [];

    public ProjectAssessment(ProjectInventory project, InventoryResult inventory, MigrationRuleSet rules)
    {
        Project = project;
        Inventory = inventory;
        Rules = rules;
        Packages = [.. project.TargetFrameworks
            .SelectMany(tf => tf.Packages)
            .DistinctBy(p => (p.Name.ToUpperInvariant(), p.Version, p.Definition.Location))];
    }

    public ProjectInventory Project { get; }

    public InventoryResult Inventory { get; }

    public MigrationRuleSet Rules { get; }

    /// <summary>The project's package references across its target frameworks, once each.</summary>
    public IReadOnlyList<PackageReferenceState> Packages { get; }

    public SourceLocation ProjectLocation => SourceLocation.OfFile(Project.Path);

    public bool UsesMSTestSdk => AnyTrue(MsBuildPropertyNames.UsingMSTestSdk);

    private bool HasErrors => _diagnostics.Any(d => d.Severity == BlockerSeverity.Error);

    public ProjectPreflight Assess()
    {
        AddInventoryDiagnostics();

        switch (Project.Classification)
        {
            case ProjectClassification.Production:
                return Result(MigrationReadiness.NotATestApplication, framework: null);

            case ProjectClassification.Unknown or ProjectClassification.Mixed:
                if (!HasErrors)
                {
                    Error(PreflightCode.UnclassifiedProject,
                        $"'{Project.Path}' could not be classified as a test application or not ({Project.Classification}), so it is not migrated.",
                        "Resolve the inventory blockers for this project, or make IsTestProject and the test packages consistent across target frameworks.");
                }

                return Result(MigrationReadiness.Blocked, framework: null);
        }

        AssessSdk();
        TestFramework? framework = Framework();
        FrameworkVerdict verdict = framework switch
        {
            TestFramework.MSTest => MSTestRules.Assess(this),
            TestFramework.NUnit => NUnitRules.Assess(this),
            TestFramework.XUnitV3 => XUnitV3Rules.Assess(this),
            TestFramework.TUnit => TUnitRules.Assess(this),
            _ => default,
        };

        if (framework is { } known)
        {
            AssessTargetFrameworks(known);
        }

        AssessExtensions(verdict.SdkUpgradePlanned);

        // A project that already runs on MTP v2 keeps whatever entry point it has; one that still has to move must use the generated one.
        bool alreadyMigrated = verdict.IsOnMtpV2 && _changes.Count == 0 && !HasErrors;
        if (!alreadyMigrated)
        {
            AssessEntryPoint();
        }

        return Result(alreadyMigrated ? MigrationReadiness.AlreadyMigrated : HasErrors ? MigrationReadiness.Blocked : MigrationReadiness.Eligible, framework);
    }

    public void Error(PreflightCode code, string message, string remediation, SourceLocation? location = null) =>
        _diagnostics.Add(PreflightDiagnostic.Error(code, message, remediation, Project.Path, location ?? ProjectLocation));

    public IEnumerable<PackageReferenceState> PackagesNamed(params string[] names) =>
        Packages.Where(p => names.Contains(p.Name, StringComparer.OrdinalIgnoreCase));

    public bool AnyTrue(string property) =>
        Project.TargetFrameworks.Any(tf => MsBuildBoolean.TryParse(tf.GetProperty(property)?.Value, out bool value) && value);

    public bool AllTrue(string property) => Project.TargetFrameworks.Count > 0 &&
        Project.TargetFrameworks.All(tf => MsBuildBoolean.TryParse(tf.GetProperty(property)?.Value, out bool value) && value);

    /// <summary>The property's state in the first target framework that defines it.</summary>
    public PropertyState? Property(string name) =>
        Project.TargetFrameworks.Select(tf => tf.GetProperty(name)).FirstOrDefault(p => p is not null);

    /// <summary>
    /// Applies the matrix rows of <paramref name="framework"/> to the project's packages; returns whether any of them is not on MTP v2.
    /// </summary>
    public bool ApplyFamilyRules(TestFramework framework, bool sdkUpgradePlanned)
    {
        bool anyBelow = false;
        foreach (PackageReferenceState package in Packages)
        {
            if (Rules.FindPackage(package.Name) is { Kind: PackageRuleKind.Framework } rule && rule.Framework == framework)
            {
                anyBelow |= ApplyRule(package, rule, sdkUpgradePlanned);
            }
        }

        return anyBelow;
    }

    /// <summary>
    /// Plans the upgrade of <paramref name="package"/> when it is below the rule's threshold, or explains why it cannot be planned;
    /// returns whether the package is not (or not known to be) on MTP v2.
    /// </summary>
    public bool ApplyRule(PackageReferenceState package, PackageRule rule, bool sdkUpgradePlanned)
    {
        if (!TryResolve(package, out NuGetVersion? version))
        {
            return true;
        }

        if (version >= rule.MinimumVersion)
        {
            return false;
        }

        if (rule.UpgradeTo is null)
        {
            Error(PreflightCode.UnsupportedTestFramework,
                $"{package.Name} {package.Version} in '{Project.Path}' does not run on Microsoft.Testing.Platform v2, and getting it there means migrating the test framework. {rule.Evidence}",
                $"Upgrade {package.Name} to {rule.MinimumVersion} or later yourself (a framework migration this tool does not do), then rerun.",
                package.Definition.Location);
            return true;
        }

        if (!package.Definition.IsRepositoryFile)
        {
            // MSTest.Sdk declares the package with a version from one of its properties. When the repository sets that property, the
            // property is what has to change; otherwise an SDK upgrade brings the package along.
            if (rule.MSTestSdkVersionProperty is { } name && Property(name) is { Definition.IsRepositoryFile: true } pinned)
            {
                Error(PreflightCode.SdkPackageVersionPinned,
                    $"{package.Name} {package.Version} in '{Project.Path}' is declared by MSTest.Sdk with the version from {name}, which the repository sets to '{pinned.Value}' at {pinned.Definition!.Location}; it must be at least {rule.MinimumVersion} to run on Microsoft.Testing.Platform v2. {rule.Evidence}",
                    $"Raise {name} to {rule.UpgradeTo} or later (or remove it to use MSTest.Sdk's own version), then rerun.",
                    pinned.Definition.Location);
                return true;
            }

            if (sdkUpgradePlanned)
            {
                return true;
            }
        }

        Plan(package, PackageChangeKind.Upgrade, package.Name, rule.UpgradeTo,
            $"{package.Name} {package.Version} does not run on Microsoft.Testing.Platform v2. {rule.Evidence}");
        return true;
    }

    /// <summary>Plans a change of <paramref name="package"/>, or explains why it cannot be edited.</summary>
    public void Plan(PackageReferenceState package, PackageChangeKind kind, string toPackage, NuGetVersion toVersion, string reason)
    {
        if (!package.Definition.IsRepositoryFile || package.VersionDefinition is { IsRepositoryFile: false })
        {
            Provenance declared = package.Definition.IsRepositoryFile ? package.VersionDefinition! : package.Definition;
            Error(PreflightCode.PackageNotInRepository,
                $"{package.Name} {package.Version} in '{Project.Path}' needs to become {toPackage} {toVersion}, but it is declared in '{declared.Location.File}', which belongs to an SDK or package, not the repository. {reason}",
                package.Definition.IsRepositoryFile
                    ? $"Declare the version of {package.Name} in the repository (a PackageVersion or Version), or upgrade whatever provides it, then rerun."
                    : $"Upgrade the SDK or package that adds {package.Name}, or reference {package.Name} with a version in the repository, then rerun.",
                declared.Location);
            return;
        }

        _changes.Add(new PackageChange(
            kind,
            package.Name,
            package.Version!,
            toPackage,
            toVersion.ToNormalizedString(),
            reason,
            package.Definition.Location,
            package.VersionDefinition?.Location));
    }

    /// <summary>Adds a change the framework rules worked out themselves (an SDK reference rather than a package reference).</summary>
    public void Plan(PackageChange change) => _changes.Add(change);

    /// <summary>The version NuGet would resolve <paramref name="package"/> to, or an error explaining why it cannot be decided offline.</summary>
    public bool TryResolve(PackageReferenceState package, [NotNullWhen(true)] out NuGetVersion? version)
    {
        if (PackageVersions.TryResolveMinimum(package.Version, out version))
        {
            return true;
        }

        Error(PreflightCode.UnresolvableVersion,
            package.Version is null
                ? $"{package.Name} in '{Project.Path}' has no version, so whether it runs on Microsoft.Testing.Platform v2 cannot be decided."
                : $"{package.Name} in '{Project.Path}' has the version '{package.Version}', which only a feed can resolve (a floating version or an open range), so whether it runs on Microsoft.Testing.Platform v2 cannot be decided offline.",
            $"Give {package.Name} a specific version (or an inclusive lower bound such as [1.2.3,)), then rerun.",
            (package.VersionDefinition ?? package.Definition).Location);
        return false;
    }

    private ProjectPreflight Result(MigrationReadiness readiness, TestFramework? framework) => new(
        Project.Path,
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
        bool isProduction = Project.Classification == ProjectClassification.Production;
        foreach (InventoryBlocker blocker in Project.Blockers.Where(b => b.Severity != BlockerSeverity.Info))
        {
            _diagnostics.Add(new PreflightDiagnostic(
                PreflightCode.UnresolvedProjectState,
                isProduction ? BlockerSeverity.Warning : BlockerSeverity.Error,
                $"The inventory could not fully determine '{Project.Path}' ({blocker.Code}): {blocker.Message}",
                blocker.Remediation,
                Project.Path,
                blocker.Location)
            {
                RelatedLocations = blocker.RelatedLocations,
            });
        }
    }

    // Whether the SDK is new enough is decided once for the selection; a test application whose evaluation did not report its SDK at all
    // cannot be checked, which is not the same as passing.
    private void AssessSdk()
    {
        if (!Project.TargetFrameworks.Any(tf => tf.GetProperty(MsBuildPropertyNames.NETCoreSdkVersion) is { Value.Length: > 0 }))
        {
            Error(PreflightCode.UnsupportedSdk,
                $"'{Project.Path}' does not report which .NET SDK evaluated it ({MsBuildPropertyNames.NETCoreSdkVersion} is not set), so whether that SDK supports Microsoft.Testing.Platform v2 cannot be decided.",
                $"Evaluate the project with the .NET SDK {Rules.MinimumSdkVersion.Major} or later (an SDK-style project sets {MsBuildPropertyNames.NETCoreSdkVersion}), then rerun.");
        }
    }

    private TestFramework? Framework()
    {
        TestFramework[] frameworks = [.. Project.Frameworks];
        if (frameworks.Contains(TestFramework.XUnitV2))
        {
            PackageReferenceState? v2 = PackagesNamed("xunit", "xunit.core").FirstOrDefault();
            Error(PreflightCode.XUnitV2NotSupported,
                $"'{Project.Path}' uses xUnit v2{(v2 is null ? string.Empty : $" ({v2.Name} {v2.Version})")}, which does not run on Microsoft.Testing.Platform v2.",
                "Migrating xUnit v2 to xUnit v3 changes the test framework, which this tool does not do. Migrate the project to xUnit v3 (https://xunit.net/docs/getting-started/v3/migration) and rerun, or keep it on VSTest in a separate run configuration.",
                v2?.Definition.Location);
        }

        switch (frameworks)
        {
            case []:
                Error(PreflightCode.UnsupportedTestFramework,
                    $"'{Project.Path}' is a test application, but none of its packages is a test framework the rules support (MSTest, NUnit, xUnit v3, TUnit).",
                    "Reference the test framework with a supported package, or set IsTestProject=false if it is not a test application.");
                return null;

            // Already reported above.
            case [TestFramework.XUnitV2]:
                return null;

            case [var only]:
                return only;

            default:
                Error(PreflightCode.MultipleTestFrameworks,
                    $"'{Project.Path}' references more than one test framework ({string.Join(", ", frameworks)}), so the rules cannot pick one.",
                    "Keep one test framework per test application, then rerun.");
                return null;
        }
    }

    // Runtime targets come from the evaluated TargetFrameworkIdentifier/TargetFrameworkVersion of each target framework, not from its name.
    private void AssessTargetFrameworks(TestFramework framework)
    {
        IReadOnlyList<RuntimeTarget> supported = Rules.RuntimeTargetsFor(framework);
        string allowed = string.Join(" or ", supported.Select(t => $"{t.Moniker} or later"));
        foreach (TargetFrameworkState state in Project.TargetFrameworks)
        {
            string identifier = state.GetProperty(MsBuildPropertyNames.TargetFrameworkIdentifier)?.Value ?? string.Empty;
            string versionText = (state.GetProperty(MsBuildPropertyNames.TargetFrameworkVersion)?.Value ?? string.Empty).TrimStart('v', 'V');
            RuntimeTarget? target = supported.FirstOrDefault(t => string.Equals(t.Identifier, identifier, StringComparison.OrdinalIgnoreCase));
            if (target is not null && NuGetVersion.TryParse(versionText, out NuGetVersion? version) && version >= target.MinimumVersion)
            {
                continue;
            }

            string evaluated = identifier is "" ? "no TargetFrameworkIdentifier" : $"{identifier} {versionText}";
            Error(PreflightCode.UnsupportedTargetFramework,
                $"'{Project.Path}' targets {state.TargetFramework} ({evaluated}), which {framework} on Microsoft.Testing.Platform v2 does not support; it supports {allowed}.",
                "Retargeting changes what the tests run on, so it is never done automatically. Retarget the project (or drop the target framework) yourself, then rerun.",
                (state.GetProperty(MsBuildPropertyNames.TargetFrameworks)?.Definition ?? state.GetProperty(MsBuildPropertyNames.TargetFramework)?.Definition)?.Location ?? ProjectLocation);
        }
    }

    private void AssessExtensions(bool sdkUpgradePlanned)
    {
        foreach (PackageReferenceState package in Packages)
        {
            if (Rules.FindPackage(package.Name) is { Kind: PackageRuleKind.Extension } rule)
            {
                ApplyRule(package, rule, sdkUpgradePlanned);
            }
            else if (Rules.FindVsTestOnlyPackage(package.Name) is { } vstestOnly)
            {
                _diagnostics.Add(PreflightDiagnostic.Warning(PreflightCode.VsTestOnlyPackage,
                    $"{package.Name} {package.Version} in '{Project.Path}' only works with VSTest and does nothing under Microsoft.Testing.Platform. {vstestOnly.Evidence}",
                    "Plan its replacement with the migration so its output (for example coverage) is not lost.",
                    Project.Path,
                    package.Definition.Location));
            }
        }
    }

    // Microsoft.Testing.Platform generates the entry point of a test application. A project with its own (StartupObject, or the
    // generated one turned off) would end up with two, or one that does not start the platform.
    private void AssessEntryPoint()
    {
        foreach (TargetFrameworkState state in Project.TargetFrameworks)
        {
            if (state.GetProperty(MsBuildPropertyNames.StartupObject) is { Value.Length: > 0 } startup)
            {
                CustomEntryPoint(state, $"sets StartupObject to '{startup.Value}'", startup);
            }

            foreach (string name in new[] { MsBuildPropertyNames.GenerateProgramFile, MsBuildPropertyNames.GenerateTestingPlatformEntryPoint })
            {
                if (state.GetProperty(name) is { } property && MsBuildBoolean.TryParse(property.Value, out bool generate) && !generate)
                {
                    CustomEntryPoint(state, $"sets {name}={property.Value}", property);
                }
            }
        }
    }

    private void CustomEntryPoint(TargetFrameworkState state, string what, PropertyState property) => Error(PreflightCode.CustomEntryPoint,
        $"'{Project.Path}' {what} for {state.TargetFramework}, so it supplies its own entry point instead of the generated one, which Microsoft.Testing.Platform needs.",
        "Remove the custom entry point (or move it into Microsoft.Testing.Platform's TestApplication builder yourself), then rerun.",
        property.Definition?.Location ?? ProjectLocation);
}
