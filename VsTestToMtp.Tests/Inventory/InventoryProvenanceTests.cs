namespace VsTestToMtp.Tests.Inventory;

using TUnit.Assertions.Enums;

using VsTestToMtp.Inventory;

// MSBuild evaluation blocks threads; running many evaluations in parallel starves the thread pool and is far slower than serial.
[NotInParallel("MSBuildEvaluation")]
public class InventoryProvenanceTests
{
    private const string MultiTarget = "<TargetFrameworks>net10.0;net8.0</TargetFrameworks>";

    [Test]
    public async Task ProjectReferences_AreReportedPerTargetFramework()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("Core/Core.csproj")
            .WriteProject("Legacy/Legacy.csproj")
            .WriteProject(
                "App/App.csproj",
                """
                  <ItemGroup>
                    <ProjectReference Include="..\Core\Core.csproj" />
                  </ItemGroup>
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                    <ProjectReference Include="..\Legacy\Legacy.csproj" />
                  </ItemGroup>
                """,
                MultiTarget)
            .WriteSlnx("All.slnx", "App/App.csproj", "Core/Core.csproj");

        ProjectInventory app = workspace.Inventory("All.slnx").Projects.Single(p => p.Name == "App");

        TargetFrameworkState net10 = app.TargetFrameworks.Single(t => t.TargetFramework == "net10.0");
        TargetFrameworkState net8 = app.TargetFrameworks.Single(t => t.TargetFramework == "net8.0");
        await Assert.That(net10.ProjectReferences.Select(r => r.Path)).IsEquivalentTo(["Core/Core.csproj"]);
        await Assert.That(net8.ProjectReferences.Select(r => (r.Path, r.IsInSelection))).IsEquivalentTo(
            [("Core/Core.csproj", true), ("Legacy/Legacy.csproj", false)],
            CollectionOrdering.Matching);
        await Assert.That(net8.ProjectReferences.Single(r => r.Path == "Legacy/Legacy.csproj").Definition.Conditions)
            .IsEquivalentTo(["'$(TargetFramework)' == 'net8.0'"]);
    }

    [Test]
    public async Task ImportConditionedOnTargetFramework_IsRecordedWithItsImportChain()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("App/net10.props", "<Project>\n  <PropertyGroup>\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>\n</Project>")
            .WriteProject(
                "App/App.csproj",
                "  <Import Project=\"net10.props\" Condition=\"'$(TargetFramework)' == 'net10.0'\" />",
                MultiTarget);

        ProjectInventory app = workspace.Inventory("App/App.csproj").Projects.Single();

        ImportRecord import = app.Imports.Single(i => i.File == "App/net10.props");
        await Assert.That(import.ImportedBy.File).IsEqualTo("App/App.csproj");
        await Assert.That(import.Condition).IsEqualTo("'$(TargetFramework)' == 'net10.0'");

        PropertyState net10 = app.TargetFrameworks.Single(t => t.TargetFramework == "net10.0").GetProperty("IsPackable")!;
        await Assert.That(net10.Value).IsEqualTo("false");
        await Assert.That(net10.Definition!.Location.File).IsEqualTo("App/net10.props");
        await Assert.That(net10.Definition.ImportChain.Select(s => s.ImportedBy.File)).IsEquivalentTo(["App/App.csproj"]);
        await Assert.That(net10.Definition.ImportChain.Single().Condition).IsEqualTo("'$(TargetFramework)' == 'net10.0'");

        PropertyState net8 = app.TargetFrameworks.Single(t => t.TargetFramework == "net8.0").GetProperty("IsPackable")!;
        await Assert.That(net8.Value).IsEqualTo("true");
        await Assert.That(net8.Definition!.IsRepositoryFile).IsFalse();
    }

    [Test]
    public async Task InlineVersion_SuppliedByAnUpdate_PointsAtTheUpdate()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                """
                  <ItemGroup>
                    <PackageReference Include="NUnit" Version="1.0.0" />
                    <PackageReference Update="NUnit" Version="2.0.0" />
                  </ItemGroup>
                """);

        PackageReferenceState nunit = workspace.Inventory("App/App.csproj").Projects.Single().TargetFrameworks.Single().Packages.Single();

        await Assert.That(nunit.Version).IsEqualTo("2.0.0");
        await Assert.That(nunit.VersionSource).IsEqualTo(PackageVersionSource.Inline);
        await Assert.That(nunit.Definition.Location.Line).IsEqualTo(workspace.LineOf("App/App.csproj", "Include=\"NUnit\""));
        await Assert.That(nunit.VersionDefinition!.Location.File).IsEqualTo("App/App.csproj");
        int update = workspace.LineOf("App/App.csproj", "Update=\"NUnit\"");
        await Assert.That(nunit.VersionDefinition.Location.Line).IsEqualTo(update);
        await Assert.That(nunit.Modifiers.Select(m => m.Location.Line)).IsEquivalentTo([update]);
    }

    [Test]
    public async Task CentralVersion_SuppliedByAnUpdate_PointsAtTheUpdate()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Packages.props", """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="NUnit" Version="4.3.2" />
                    <PackageVersion Update="NUnit" Version="4.9.0" />
                  </ItemGroup>
                </Project>
                """)
            .WriteProject("App/App.csproj", ScenarioWorkspace.PackageReferences("NUnit"));

        PackageReferenceState nunit = workspace.Inventory("App/App.csproj").Projects.Single().TargetFrameworks.Single().Packages.Single();

        await Assert.That(nunit.Version).IsEqualTo("4.9.0");
        await Assert.That(nunit.VersionSource).IsEqualTo(PackageVersionSource.Central);
        await Assert.That(nunit.VersionDefinition!.Location.File).IsEqualTo("Directory.Packages.props");
        await Assert.That(nunit.VersionDefinition.Location.Line).IsEqualTo(workspace.LineOf("Directory.Packages.props", "Update=\"NUnit\""));
    }

    [Test]
    public async Task PackageVersion_IsIgnored_WhenCentralPackageManagementIsOff()
    {
        // NuGet ignores PackageVersion items unless ManagePackageVersionsCentrally is true.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Packages.props", "<Project>\n  <ItemGroup>\n    <PackageVersion Include=\"NUnit\" Version=\"4.3.2\" />\n  </ItemGroup>\n</Project>")
            .WriteProject("App/App.csproj", ScenarioWorkspace.PackageReferences("NUnit"));

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        PackageReferenceState nunit = project.TargetFrameworks.Single().Packages.Single();
        await Assert.That(nunit.VersionSource).IsEqualTo(PackageVersionSource.None);
        await Assert.That(nunit.Version).IsNull();
        await Assert.That(project.Blockers.Select(b => b.Code)).Contains(BlockerCodes.UnresolvedPackageVersion);
    }

    [Test]
    public async Task DuplicatePackageVersion_IsAmbiguous_NotResolvedByOverwriting()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Packages.props", """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="NUnit" Version="4.0.0" />
                    <PackageVersion Include="NUnit" Version="4.3.2" />
                  </ItemGroup>
                </Project>
                """)
            .WriteProject("App/App.csproj", ScenarioWorkspace.PackageReferences("NUnit"));

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        InventoryBlocker duplicate = project.Blockers.Single(b => b.Code == BlockerCodes.DuplicatePackageVersion);
        await Assert.That(duplicate.Message).Contains("4.0.0").Or.Contains("Directory.Packages.props");
        await Assert.That(project.TargetFrameworks.Single().Packages.Single().VersionSource).IsEqualTo(PackageVersionSource.None);
        await Assert.That(project.Blockers.Select(b => b.Code)).Contains(BlockerCodes.UnresolvedPackageVersion);
    }

    [Test]
    public async Task VersionOverride_IsIgnored_WhenCentralPackageManagementIsOff()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", "  <ItemGroup>\n    <PackageReference Include=\"NUnit\" VersionOverride=\"4.0.0\" />\n  </ItemGroup>");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        PackageReferenceState nunit = project.TargetFrameworks.Single().Packages.Single();
        await Assert.That(nunit.VersionSource).IsEqualTo(PackageVersionSource.None);
        await Assert.That(nunit.Version).IsNull();
        await Assert.That(project.Blockers.Select(b => b.Code)).IsEquivalentTo(
            [BlockerCodes.IneffectiveVersionOverride, BlockerCodes.UnresolvedPackageVersion]);
    }

    [Test]
    public async Task VersionOverride_WhenOverridesAreDisabled_IsAnError_AndNotFallenBackToTheCentralVersion()
    {
        // NuGet fails restore (NU1013) instead of ignoring the override, so no version is effective.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Packages.props", """
                <Project>
                  <PropertyGroup>
                    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>
                    <CentralPackageVersionOverrideEnabled>false</CentralPackageVersionOverrideEnabled>
                  </PropertyGroup>
                  <ItemGroup>
                    <PackageVersion Include="NUnit" Version="4.3.2" />
                  </ItemGroup>
                </Project>
                """)
            .WriteProject("App/App.csproj", "  <ItemGroup>\n    <PackageReference Include=\"NUnit\" VersionOverride=\"4.0.0\" />\n  </ItemGroup>");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        PackageReferenceState nunit = project.TargetFrameworks.Single().Packages.Single();
        await Assert.That(nunit.VersionSource).IsEqualTo(PackageVersionSource.None);
        await Assert.That(nunit.Version).IsNull();
        InventoryBlocker blocker = project.Blockers.Single();
        await Assert.That(blocker.Code).IsEqualTo(BlockerCodes.IneffectiveVersionOverride);
        await Assert.That(blocker.Severity).IsEqualTo(BlockerSeverity.Error);
    }

    [Test]
    public async Task InlineVersion_UnderCentralPackageManagement_IsAnError_AndNotAnEffectiveVersion()
    {
        // NU1008: a PackageReference must not carry Version when central management is on.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageVersion Include=\"MSTest\" Version=\"4.0.2\" />\n  </ItemGroup>\n</Project>")
            .WriteProject("App/App.csproj", ScenarioWorkspace.PackageReferences("MSTest|4.0.2"));

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        PackageReferenceState mstest = project.TargetFrameworks.Single().Packages.Single();
        await Assert.That(mstest.VersionSource).IsEqualTo(PackageVersionSource.None);
        await Assert.That(mstest.Version).IsNull();
        InventoryBlocker blocker = project.Blockers.Single();
        await Assert.That(blocker.Code).IsEqualTo(BlockerCodes.InlineVersionUnderCentralManagement);
        await Assert.That(blocker.Severity).IsEqualTo(BlockerSeverity.Error);
        await Assert.That(blocker.Location!.Line).IsEqualTo(workspace.LineOf("App/App.csproj", "Include=\"MSTest\""));

        // The declaration is invalid for NuGet, but MSBuild's picture of the project is complete, so it is still classified.
        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.TestApplication);
    }

    [Test]
    public async Task GlobalPackageReferenceVersion_UnderCentralPackageManagement_IsValid()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n  <ItemGroup>\n    <GlobalPackageReference Include=\"Some.Analyzer\" Version=\"1.2.3\" />\n  </ItemGroup>\n</Project>")
            .WriteProject("App/App.csproj");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        PackageReferenceState analyzer = project.TargetFrameworks.Single().Packages.Single();
        await Assert.That(analyzer.IsGlobal).IsTrue();
        await Assert.That(analyzer.Version).IsEqualTo("1.2.3");
        await Assert.That(analyzer.VersionSource).IsEqualTo(PackageVersionSource.Inline);
        await Assert.That(project.Blockers).IsEmpty();
    }

    [Test]
    public async Task PropertyNames_AreCaseInsensitive_AndReportedCanonically()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "Tool/Tool.csproj",
                "  <PropertyGroup>\n    <isTestProject>false</isTestProject>\n  </PropertyGroup>\n"
                + ScenarioWorkspace.PackageReferences("MSTest|4.0.2"));

        ProjectInventory project = workspace.Inventory("Tool/Tool.csproj").Projects.Single();

        PropertyState property = project.TargetFrameworks.Single().GetProperty("IsTestProject")!;
        await Assert.That(property.Name).IsEqualTo("IsTestProject");
        await Assert.That(property.Value).IsEqualTo("false");
        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Production);
        await Assert.That(project.HasTestLibraryDependency).IsTrue();
    }

    [Test]
    public async Task ItemTypes_AreCaseInsensitive_ForConditionAnalysis()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <ItemGroup Condition=\"'$(VSTEST_TO_MTP_UNSET_FLAG)' == 'true'\">\n    <packagereference Include=\"NUnit\" Version=\"4.3.2\" />\n  </ItemGroup>");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(project.Blockers.Select(b => b.Code)).Contains(BlockerCodes.ConditionDependsOnUnsetProperty);
    }
}
