namespace VsTestToMtp.Tests.Inventory;

using TUnit.Assertions.Enums;

using VsTestToMtp.Inventory;

// MSBuild evaluation blocks threads; running many evaluations in parallel starves the thread pool and is far slower than serial.
[NotInParallel("MSBuildEvaluation")]
public class InventoryScenarioTests
{
    private const string Sln = """
        Microsoft Visual Studio Solution File, Format Version 12.00
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "App", "src\App\App.csproj", "{11111111-1111-1111-1111-111111111111}"
        EndProject
        Project("{2150E333-8FDC-42A3-9474-1A3956D46DE8}") = "src", "src", "{22222222-2222-2222-2222-222222222222}"
        EndProject
        Project("{F184B08F-C81C-45F6-A57F-5ABD9991F28F}") = "Legacy", "src\Legacy\Legacy.vbproj", "{33333333-3333-3333-3333-333333333333}"
        EndProject
        Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Gone", "src\Gone\Gone.csproj", "{44444444-4444-4444-4444-444444444444}"
        EndProject
        Global
        EndGlobal
        """;

    [Test]
    public async Task Slnx_ListsExactMembersOnly()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("src/B/B.csproj")
            .WriteProject("src/A/A.csproj")
            .WriteProject("src/NotInSolution/NotInSolution.csproj")
            .WriteSlnx("All.slnx", "src/B/B.csproj", "src/A/A.csproj");

        InventoryResult result = workspace.Inventory("All.slnx");

        await Assert.That(result.Projects.Select(p => p.Path)).IsEquivalentTo(["src/A/A.csproj", "src/B/B.csproj"], CollectionOrdering.Matching);
        await Assert.That(result.Blockers).IsEmpty();
    }

    [Test]
    public async Task Sln_ListsCSharpMembersSkipsFoldersAndReportsMissingAndNonCSharpProjects()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("src/App/App.csproj")
            .WriteProject("src/NotInSolution/NotInSolution.csproj")
            .Write("src/Legacy/Legacy.vbproj", "<Project />")
            .Write("All.sln", Sln);

        InventoryResult result = workspace.Inventory("All.sln");

        await Assert.That(result.Selection.Kind).IsEqualTo("sln");
        await Assert.That(result.Projects.Select(p => p.Path)).IsEquivalentTo(["src/App/App.csproj"]);
        await Assert.That(result.Blockers.Select(b => b.Code)).IsEquivalentTo(
            [BlockerCodes.MissingProject, BlockerCodes.UnsupportedProjectLanguage]);
        await Assert.That(result.Blockers.Single(b => b.Code == BlockerCodes.MissingProject).Severity).IsEqualTo(BlockerSeverity.Error);
    }

    [Test]
    public async Task Sln_OrdersMembersByPath_NotByFileOrder()
    {
        // Unlike .slnx, a .sln keeps its file order, so the inventory itself has to make the order stable.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("src/z/Z.csproj")
            .WriteProject("src/B/B.csproj")
            .WriteProject("src/a/A.csproj")
            .Write("All.sln", """
                Microsoft Visual Studio Solution File, Format Version 12.00
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Z", "src\z\Z.csproj", "{11111111-1111-1111-1111-111111111111}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "B", "src\B\B.csproj", "{22222222-2222-2222-2222-222222222222}"
                EndProject
                Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "A", "src\a\A.csproj", "{33333333-3333-3333-3333-333333333333}"
                EndProject
                Global
                EndGlobal
                """);

        InventoryResult result = workspace.Inventory("All.sln");

        await Assert.That(result.Projects.Select(p => p.Path)).IsEquivalentTo(
            ["src/a/A.csproj", "src/B/B.csproj", "src/z/Z.csproj"],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task Selection_WithSeveralSolutionsInDirectory_IsAmbiguous()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("A.csproj")
            .WriteSlnx("One.slnx", "A.csproj")
            .WriteSlnx("Two.slnx", "A.csproj");

        InventoryResult result = InventoryBuilder.Build(workspace.RootPath, new InventoryOptions(workspace.RootPath));

        await Assert.That(result.Projects).IsEmpty();
        await Assert.That(result.Blockers.Select(b => b.Code)).IsEquivalentTo([BlockerCodes.AmbiguousSelection]);
    }

    [Test]
    public async Task DirectorySelection_FindsASolutionWhoseExtensionIsUpperCase()
    {
        // The same file is accepted when passed directly, so selecting its directory must find it on every file system.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj")
            .WriteSlnx("Only.SLNX", "App/App.csproj");

        InventoryResult result = InventoryBuilder.Build(workspace.RootPath, new InventoryOptions(workspace.RootPath));

        await Assert.That(result.Selection.Kind).IsEqualTo("slnx");
        await Assert.That(result.Projects.Select(p => p.Path)).IsEquivalentTo(["App/App.csproj"]);
        await Assert.That(result.Blockers).IsEmpty();
    }

    [Test]
    public async Task DirectorySelection_FindsASingleProjectWhoseExtensionIsUpperCase()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App.CSPROJ");

        InventoryResult result = InventoryBuilder.Build(workspace.RootPath, new InventoryOptions(workspace.RootPath));

        await Assert.That(result.Selection.Kind).IsEqualTo("project");
        await Assert.That(result.Projects.Select(p => p.Path)).IsEquivalentTo(["App.CSPROJ"]);
        await Assert.That(result.Blockers).IsEmpty();
    }

    [Test]
    public async Task DirectorySelection_AmbiguityMessageListsSolutionsInADeterministicOrder()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App.csproj")
            .WriteSlnx("b.slnx", "App.csproj")
            .WriteSlnx("A.SLN", "App.csproj");

        InventoryResult result = InventoryBuilder.Build(workspace.RootPath, new InventoryOptions(workspace.RootPath));

        await Assert.That(result.Blockers.Single(b => b.Code == BlockerCodes.AmbiguousSelection).Message).Contains("A.SLN, b.slnx");
    }

    [Test]
    [SkipOnCaseInsensitivePaths]
    public async Task DirectorySelection_OrdersSolutionsDifferingOnlyByCase_Deterministically()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App.csproj")
            .WriteSlnx("b.slnx", "App.csproj")
            .WriteSlnx("B.SLNX", "App.csproj");

        InventoryResult result = InventoryBuilder.Build(workspace.RootPath, new InventoryOptions(workspace.RootPath));

        // Case-insensitive order ties, then ordinal decides: uppercase sorts before lowercase.
        await Assert.That(result.Blockers.Single(b => b.Code == BlockerCodes.AmbiguousSelection).Message).Contains("B.SLNX, b.slnx");
    }

    [Test]
    public async Task Selection_OfMissingFile_IsReportedAsBlocker()
    {
        using ScenarioWorkspace workspace = new();

        InventoryResult result = workspace.Inventory("Nope.slnx");

        await Assert.That(result.Blockers.Select(b => b.Code)).IsEquivalentTo([BlockerCodes.SelectionNotFound]);
    }

    [Test]
    public async Task ProductionProject_WithTestLibraryDependency_IsNotATestApplication()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("src/Helpers/Helpers.csproj", ScenarioWorkspace.PackageReferences("MSTest.TestFramework|3.9.0"));

        ProjectInventory project = workspace.Inventory("src/Helpers/Helpers.csproj").Projects.Single();

        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Production);
        await Assert.That(project.HasTestLibraryDependency).IsTrue();
        await Assert.That(project.Frameworks).IsEquivalentTo(["MSTest"]);
    }

    [Test]
    public async Task TestSdkAlone_DoesNotEstablishATestApplication()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", ScenarioWorkspace.PackageReferences("Microsoft.NET.Test.Sdk|18.0.0"));

        await Assert.That(workspace.Inventory("App/App.csproj").Projects.Single().Classification).IsEqualTo(ProjectClassification.Production);
    }

    [Test]
    public async Task TestsFileName_WithoutTestEvidence_IsNotATestApplication()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("Thing.Tests/Thing.Tests.csproj");

        ProjectInventory project = workspace.Inventory("Thing.Tests/Thing.Tests.csproj").Projects.Single();

        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Production);
        await Assert.That(project.HasTestLibraryDependency).IsFalse();
    }

    [Test]
    public async Task SharedPropsPackages_DoNotEstablishATestApplication()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Build.props", $"<Project>\n{ScenarioWorkspace.PackageReferences("Microsoft.NET.Test.Sdk|18.0.0", "xunit.runner.visualstudio|3.1.0")}\n</Project>")
            .WriteProject("Lib/Lib.csproj", ScenarioWorkspace.PackageReferences("xunit|2.9.3"))
            .WriteProject("Lib.Tests/Lib.Tests.csproj", ScenarioWorkspace.PackageReferences("xunit|2.9.3", "xunit.runner.visualstudio|3.1.0"))
            .WriteSlnx("All.slnx", "Lib/Lib.csproj", "Lib.Tests/Lib.Tests.csproj");

        InventoryResult result = workspace.Inventory("All.slnx");

        ProjectInventory lib = result.Projects.Single(p => p.Name == "Lib");
        await Assert.That(lib.Classification).IsEqualTo(ProjectClassification.Production);
        await Assert.That(lib.HasTestLibraryDependency).IsTrue();
        await Assert.That(lib.Blockers.Select(b => b.Code)).Contains(BlockerCodes.TestEvidenceOnlyFromImports);

        ProjectInventory tests = result.Projects.Single(p => p.Name == "Lib.Tests");
        await Assert.That(tests.Classification).IsEqualTo(ProjectClassification.TestApplication);
        PackageReferenceState shared = tests.TargetFrameworks.Single().Packages.First(p => p.Name == "Microsoft.NET.Test.Sdk");
        await Assert.That(shared.Definition.Location.File).IsEqualTo("Directory.Build.props");
        await Assert.That(shared.Definition.IsFromProjectFile).IsFalse();
    }

    [Test]
    public async Task ExplicitIsTestProjectFalse_WinsOverTestPackages()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "Tool/Tool.csproj",
                "  <PropertyGroup>\n    <IsTestProject>false</IsTestProject>\n  </PropertyGroup>\n"
                + ScenarioWorkspace.PackageReferences("MSTest|4.0.2"));

        ProjectInventory project = workspace.Inventory("Tool/Tool.csproj").Projects.Single();

        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Production);
        await Assert.That(project.HasTestLibraryDependency).IsTrue();
    }

    [Test]
    public async Task EvaluatedIsTestProjectTrue_FromSharedProps_ClassifiesAndKeepsProvenance()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Build.props", """
                <Project>
                  <PropertyGroup Condition="$(MSBuildProjectName.EndsWith('.Specs'))">
                    <IsTestProject>true</IsTestProject>
                  </PropertyGroup>
                </Project>
                """)
            .WriteProject("A.Specs/A.Specs.csproj")
            .WriteProject("A/A.csproj")
            .WriteSlnx("All.slnx", "A.Specs/A.Specs.csproj", "A/A.csproj");

        InventoryResult result = workspace.Inventory("All.slnx");

        ProjectInventory specs = result.Projects.Single(p => p.Name == "A.Specs");
        await Assert.That(specs.Classification).IsEqualTo(ProjectClassification.TestApplication);
        PropertyState property = specs.TargetFrameworks.Single().GetProperty("IsTestProject")!;
        await Assert.That(property.Definition!.Location.File).IsEqualTo("Directory.Build.props");
        await Assert.That(property.Definition.Location.Line).IsEqualTo(workspace.LineOf("Directory.Build.props", "<IsTestProject>"));
        await Assert.That(property.Definition.Conditions).IsEquivalentTo(["$(MSBuildProjectName.EndsWith('.Specs'))"]);
        await Assert.That(result.Projects.Single(p => p.Name == "A").Classification).IsEqualTo(ProjectClassification.Production);
    }

    [Test]
    public async Task MultiTargeting_ClassifiesPerTargetFramework_AndReportsDisagreement()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                """
                  <ItemGroup Condition="'$(TargetFramework)' == 'net10.0'">
                    <PackageReference Include="NUnit" Version="4.3.2" />
                    <PackageReference Include="NUnit3TestAdapter" Version="5.0.0" />
                  </ItemGroup>
                """,
                "<TargetFrameworks>net10.0;net8.0</TargetFrameworks>");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(project.TargetFrameworks.Select(t => (t.TargetFramework, t.Classification))).IsEquivalentTo(
            [("net10.0", ProjectClassification.TestApplication), ("net8.0", ProjectClassification.Production)],
            CollectionOrdering.Matching);
        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Mixed);
        await Assert.That(project.Blockers.Select(b => b.Code)).Contains(BlockerCodes.TargetFrameworkClassificationDiffers);

        PackageReferenceState nunit = project.TargetFrameworks.Single(t => t.TargetFramework == "net10.0").Packages.Single(p => p.Name == "NUnit");
        await Assert.That(nunit.Definition.Conditions).IsEquivalentTo(["'$(TargetFramework)' == 'net10.0'"]);
        await Assert.That(project.TargetFrameworks.Single(t => t.TargetFramework == "net8.0").Packages).IsEmpty();
    }

    [Test]
    public async Task TargetFrameworkSetAlongsideTargetFrameworks_IsASingleTargetBuild()
    {
        // The SDK only dispatches to inner builds when TargetFramework is empty, so this project builds (and tests) net10.0 alone.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                """
                  <ItemGroup Condition="'$(TargetFramework)' == 'net8.0'">
                    <PackageReference Include="NUnit" Version="4.3.2" />
                    <PackageReference Include="NUnit3TestAdapter" Version="5.0.0" />
                  </ItemGroup>
                """,
                "<TargetFramework>net10.0</TargetFramework>\n    <TargetFrameworks>net10.0;net8.0</TargetFrameworks>");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(project.TargetFrameworks.Select(t => t.TargetFramework)).IsEquivalentTo(["net10.0"]);
        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Production);
        await Assert.That(project.Blockers).IsEmpty();
    }

    [Test]
    public async Task ExplicitParentImports_AreRecordedInTheImportChain()
    {
        const string importParent = "<Import Project=\"$([MSBuild]::GetPathOfFileAbove('{0}', '$(MSBuildThisFileDirectory)../'))\" />";
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Build.props", "<Project>\n  <PropertyGroup>\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>\n</Project>")
            .Write("Directory.Packages.props", """
                <Project>
                  <ItemGroup>
                    <PackageVersion Include="NUnit" Version="4.3.2" />
                  </ItemGroup>
                </Project>
                """)
            .Write("sub/Directory.Build.props", $"<Project>\n  {string.Format(importParent, "Directory.Build.props")}\n</Project>")
            .Write("sub/Directory.Packages.props", $"<Project>\n  {string.Format(importParent, "Directory.Packages.props")}\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n</Project>")
            .WriteProject("sub/App/App.csproj", ScenarioWorkspace.PackageReferences("NUnit"));

        ProjectInventory project = workspace.Inventory("sub/App/App.csproj").Projects.Single();

        await Assert.That(project.Imports.Where(i => !i.IsImplicit).Select(i => (i.File, i.ImportedBy.File))).IsEquivalentTo(
        [
            ("Directory.Build.props", "sub/Directory.Build.props"),
            ("Directory.Packages.props", "sub/Directory.Packages.props"),
        ]);

        PropertyState isPackable = project.TargetFrameworks.Single().GetProperty("IsPackable")!;
        await Assert.That(isPackable.Value).IsEqualTo("false");
        await Assert.That(isPackable.Definition!.Location.File).IsEqualTo("Directory.Build.props");
        await Assert.That(isPackable.Definition.ImportChain.Any(s => s.ImportedBy.File == "sub/Directory.Build.props")).IsTrue();

        PackageReferenceState nunit = project.TargetFrameworks.Single().Packages.Single();
        await Assert.That(nunit.VersionSource).IsEqualTo(PackageVersionSource.Central);
        await Assert.That(nunit.Version).IsEqualTo("4.3.2");
        await Assert.That(nunit.VersionDefinition!.Location.File).IsEqualTo("Directory.Packages.props");
        await Assert.That(nunit.VersionDefinition.ImportChain.Any(s => s.ImportedBy.File == "sub/Directory.Packages.props")).IsTrue();
    }

    [Test]
    public async Task VersionOverride_IsReportedAsOverride()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageVersion Include=\"NUnit\" Version=\"4.3.2\" />\n  </ItemGroup>\n</Project>")
            .WriteProject("App/App.csproj", "  <ItemGroup>\n    <PackageReference Include=\"NUnit\" VersionOverride=\"4.0.0\" />\n  </ItemGroup>");

        PackageReferenceState nunit = workspace.Inventory("App/App.csproj").Projects.Single().TargetFrameworks.Single().Packages.Single();

        await Assert.That(nunit.VersionSource).IsEqualTo(PackageVersionSource.VersionOverride);
        await Assert.That(nunit.Version).IsEqualTo("4.0.0");
        await Assert.That(nunit.VersionDefinition!.Location.File).IsEqualTo("App/App.csproj");
    }

    [Test]
    public async Task MissingImport_BlocksClassificationWithLocation()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", "  <Import Project=\"missing.props\" />\n" + ScenarioWorkspace.PackageReferences("MSTest|4.0.2"));

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Unknown);
        InventoryBlocker blocker = project.Blockers.Single(b => b.Code == BlockerCodes.MissingImport);
        await Assert.That(blocker.Severity).IsEqualTo(BlockerSeverity.Error);
        await Assert.That(blocker.Location!.File).IsEqualTo("App/App.csproj");
        await Assert.That(blocker.Location.Line).IsEqualTo(workspace.LineOf("App/App.csproj", "<Import Project=\"missing.props\""));
        await Assert.That(blocker.Remediation).IsNotEmpty();
    }

    [Test]
    public async Task ConditionalImportOfMissingFile_IsNotABlocker()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", "  <Import Project=\"missing.props\" Condition=\"Exists('missing.props')\" />");

        await Assert.That(workspace.Inventory("App/App.csproj").Blockers).IsEmpty();
    }

    [Test]
    public async Task UnconditionalImportThatResolvesToAnEmptyPath_IsAMissingImport()
    {
        // No parent Directory.Build.props exists, so GetPathOfFileAbove expands to nothing.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Build.props", "<Project>\n  <Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" />\n</Project>")
            .WriteProject("App/App.csproj");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        InventoryBlocker blocker = project.Blockers.Single(b => b.Code == BlockerCodes.MissingImport);
        await Assert.That(blocker.Severity).IsEqualTo(BlockerSeverity.Error);
        await Assert.That(blocker.Location!.File).IsEqualTo("Directory.Build.props");
        await Assert.That(blocker.Location.Line).IsEqualTo(workspace.LineOf("Directory.Build.props", "GetPathOfFileAbove"));
        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Unknown);
    }

    [Test]
    public async Task UnconditionalEmptyImport_InsideATargetFrameworkConditionedFile_IsAMissingImport()
    {
        // The file with the broken import is only imported by the net10.0 inner build, never by the outer one.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("App/net10.props", "<Project>\n  <Import Project=\"$([MSBuild]::GetPathOfFileAbove('no-such-parent.props', '$(MSBuildThisFileDirectory)../'))\" />\n</Project>")
            .WriteProject(
                "App/App.csproj",
                "  <Import Project=\"net10.props\" Condition=\"'$(TargetFramework)' == 'net10.0'\" />",
                "<TargetFrameworks>net10.0;net8.0</TargetFrameworks>");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        InventoryBlocker blocker = project.Blockers.Single(b => b.Code == BlockerCodes.MissingImport);
        await Assert.That(blocker.Location!.File).IsEqualTo("App/net10.props");
        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Unknown);
    }

    [Test]
    public async Task ImportWhoseConditionHoldsButPathIsEmpty_IsAMissingImport()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <PropertyGroup>\n    <Flag>true</Flag>\n  </PropertyGroup>\n  <Import Project=\"$(VSTEST_TO_MTP_MISSING_PATH)\" Condition=\"'$(Flag)' == 'true'\" />");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        InventoryBlocker blocker = project.Blockers.Single(b => b.Code == BlockerCodes.MissingImport);
        await Assert.That(blocker.Location!.Line).IsEqualTo(workspace.LineOf("App/App.csproj", "<Import Project"));
        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Unknown);
    }

    [Test]
    public async Task ImportWhoseConditionIsFalse_IsNotABlocker_EvenIfItsPathIsEmpty()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <Import Project=\"$(VSTEST_TO_MTP_MISSING_PATH)\" Condition=\"'$(VSTEST_TO_MTP_MISSING_FLAG)' == 'true'\" />");

        await Assert.That(workspace.Inventory("App/App.csproj").Projects.Single().Blockers.Select(b => b.Code))
            .DoesNotContain(BlockerCodes.MissingImport);
    }

    [Test]
    public async Task UnresolvableSdk_IsReportedAsEvaluationFailure_NotAsMissingImports()
    {
        // IgnoreMissingImports alone would swallow an unresolved Sdk= and leave a half-evaluated project that looks complete.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("App/App.csproj", "<Project Sdk=\"Does.Not.Exist.Sdk/1.0.0\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Unknown);
        InventoryBlocker blocker = project.Blockers.Single();
        await Assert.That(blocker.Code).IsEqualTo(BlockerCodes.EvaluationFailed);
        await Assert.That(blocker.Message).Contains("Does.Not.Exist.Sdk");
    }

    [Test]
    public async Task CircularImport_IsReportedAsEvaluationFailure_NotAcceptedAsComplete()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("App/a.props", "<Project>\n  <Import Project=\"b.props\" />\n</Project>")
            .Write("App/b.props", "<Project>\n  <Import Project=\"a.props\" />\n</Project>")
            .WriteProject("App/App.csproj", "  <Import Project=\"a.props\" />");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Unknown);
        await Assert.That(project.Blockers.Select(b => b.Code)).Contains(BlockerCodes.EvaluationFailed);
    }

    [Test]
    public async Task ImportGlobWithNoMatches_IsNotABlocker()
    {
        // MSBuild documents an unmatched glob as a normal, non-ignored import.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", "  <Import Project=\"*.nomatch\" />");

        await Assert.That(workspace.Inventory("App/App.csproj").Projects.Single().Blockers).IsEmpty();
    }

    [Test]
    public async Task ImportedTwice_IsNotAnEvaluationFailure()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("App/shared.props", "<Project>\n  <PropertyGroup>\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>\n</Project>")
            .WriteProject("App/App.csproj", "  <Import Project=\"shared.props\" />\n  <Import Project=\"shared.props\" />");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Production);
        await Assert.That(project.Blockers.Select(b => b.Code)).DoesNotContain(BlockerCodes.EvaluationFailed);
        await Assert.That(project.TargetFrameworks.Single().GetProperty("IsPackable")!.Value).IsEqualTo("false");
    }

    [Test]
    public async Task GuardedImportThatResolvesToAnEmptyPath_IsNotABlocker()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Build.props", "<Project>\n  <Import Project=\"$([MSBuild]::GetPathOfFileAbove('Directory.Build.props', '$(MSBuildThisFileDirectory)../'))\" Condition=\"'$([MSBuild]::GetPathOfFileAbove(`Directory.Build.props`, `$(MSBuildThisFileDirectory)../`))' != ''\" />\n</Project>")
            .WriteProject("App/App.csproj");

        await Assert.That(workspace.Inventory("App/App.csproj").Projects.Single().Blockers).IsEmpty();
    }

    [Test]
    public async Task IsTestProject_IsEvaluatedPerTargetFramework()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <PropertyGroup Condition=\"'$(TargetFramework)' == 'net10.0'\">\n    <IsTestProject>true</IsTestProject>\n  </PropertyGroup>",
                "<TargetFrameworks>net10.0;net8.0</TargetFrameworks>");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        TargetFrameworkState net10 = project.TargetFrameworks.Single(t => t.TargetFramework == "net10.0");
        TargetFrameworkState net8 = project.TargetFrameworks.Single(t => t.TargetFramework == "net8.0");
        await Assert.That(net10.Classification).IsEqualTo(ProjectClassification.TestApplication);
        await Assert.That(net10.GetProperty("IsTestProject")!.Definition!.Conditions).IsEquivalentTo(["'$(TargetFramework)' == 'net10.0'"]);
        await Assert.That(net8.Classification).IsEqualTo(ProjectClassification.Production);
        await Assert.That(net8.GetProperty("IsTestProject")).IsNull();
        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Mixed);
    }

    [Test]
    public async Task NonBooleanIsTestProject_MakesClassificationUndeterminable()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <PropertyGroup>\n    <IsTestProject>maybe</IsTestProject>\n  </PropertyGroup>\n"
                + ScenarioWorkspace.PackageReferences("MSTest|4.0.2"));

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Unknown);
        InventoryBlocker blocker = project.Blockers.Single(b => b.Code == BlockerCodes.InvalidIsTestProject);
        await Assert.That(blocker.Severity).IsEqualTo(BlockerSeverity.Error);
        await Assert.That(blocker.Location!.Line).IsEqualTo(workspace.LineOf("App/App.csproj", "<IsTestProject>"));
    }

    [Test]
    public async Task RestoreOutput_DoesNotChangeTheResult()
    {
        // Microsoft.NET.Test.Sdk's props set IsTestProject=true for any project that references it, which would make a
        // restored production project look like a test application. Restore output is ignored, so restoring changes nothing.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", ScenarioWorkspace.PackageReferences("Microsoft.NET.Test.Sdk|18.0.0"));
        ProjectInventory before = workspace.Inventory("App/App.csproj").Projects.Single();

        workspace.Write("App/obj/App.csproj.nuget.g.props", "<Project>\n  <PropertyGroup>\n    <IsTestProject>true</IsTestProject>\n  </PropertyGroup>\n</Project>");
        ProjectInventory after = workspace.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(before.Classification).IsEqualTo(ProjectClassification.Production);
        await Assert.That(after.Classification).IsEqualTo(ProjectClassification.Production);
        await Assert.That(after.TargetFrameworks.Single().GetProperty("IsTestProject")).IsNull();
        await Assert.That(after.Imports.Any(i => i.File.Contains("/obj/", StringComparison.Ordinal))).IsFalse();
    }

    [Test]
    public async Task InvalidProjectFile_IsReportedAsEvaluationFailure()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup>");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Unknown);
        await Assert.That(project.Blockers.Select(b => b.Code)).Contains(BlockerCodes.EvaluationFailed);
    }

    [Test]
    public async Task MissingTargetFramework_IsReportedAsBlocker()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", targetFramework: string.Empty);

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Unknown);
        await Assert.That(project.Blockers.Select(b => b.Code)).Contains(BlockerCodes.NoTargetFramework);
    }

    [Test]
    public async Task IsTestProjectCondition_InDirectoryBuildProps_IsFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Build.props", """
                <Project>
                  <PropertyGroup Condition="'$(IsTestProject)' == 'true'">
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                </Project>
                """)
            .WriteProject("App/App.csproj");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        InventoryBlocker blocker = project.Blockers.Single(b => b.Code == BlockerCodes.IsTestProjectEarlyCondition);
        await Assert.That(blocker.Location!.File).IsEqualTo("Directory.Build.props");
        await Assert.That(blocker.Location.Line).IsEqualTo(workspace.LineOf("Directory.Build.props", "$(IsTestProject)"));
        await Assert.That(blocker.Remediation).Contains("Directory.Build.targets");
    }

    [Test]
    public async Task IsTestProjectCondition_InExplicitlyImportedProps_FollowsEvaluationOrder()
    {
        const string guarded = "<Project>\n  <PropertyGroup Condition=\"'$(IsTestProject)' == 'true'\">\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>\n</Project>";
        const string assign = "  <PropertyGroup>\n    <IsTestProject>true</IsTestProject>\n  </PropertyGroup>\n";
        const string import = "  <Import Project=\"shared.props\" />\n";

        using ScenarioWorkspace before = new ScenarioWorkspace()
            .Write("App/shared.props", guarded)
            .WriteProject("App/App.csproj", import + assign);
        using ScenarioWorkspace after = new ScenarioWorkspace()
            .Write("App/shared.props", guarded)
            .WriteProject("App/App.csproj", assign + import);

        InventoryBlocker blocker = before.Inventory("App/App.csproj").Projects.Single().Blockers
            .Single(b => b.Code == BlockerCodes.IsTestProjectEarlyCondition);
        await Assert.That(blocker.Location!.File).IsEqualTo("App/shared.props");
        await Assert.That(after.Inventory("App/App.csproj").Projects.Single().Blockers.Select(b => b.Code))
            .DoesNotContain(BlockerCodes.IsTestProjectEarlyCondition);
    }

    [Test]
    public async Task IsTestProjectCondition_InImportedProps_IsFlagged_WhenNothingInTheRepositoryAssignsIt()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("App/shared.props", "<Project>\n  <PropertyGroup Condition=\"'$(IsTestProject)' == 'true'\">\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>\n</Project>")
            .WriteProject("App/App.csproj", "  <Import Project=\"shared.props\" />");

        await Assert.That(workspace.Inventory("App/App.csproj").Projects.Single().Blockers.Select(b => b.Code))
            .Contains(BlockerCodes.IsTestProjectEarlyCondition);
    }

    [Test]
    public async Task IsTestProjectCondition_InDirectoryBuildTargets_IsNotFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Build.targets", """
                <Project>
                  <PropertyGroup Condition="'$(IsTestProject)' == 'true'">
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                </Project>
                """)
            .WriteProject("App/App.csproj");

        await Assert.That(workspace.Inventory("App/App.csproj").Blockers).IsEmpty();
    }

    [Test]
    public async Task ConditionOnUnsetProperty_IsFlaggedWithLocation()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <PropertyGroup Condition=\"'$(VSTEST_TO_MTP_UNSET_FLAG)' == 'true'\">\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>");

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        InventoryBlocker blocker = project.Blockers.Single(b => b.Code == BlockerCodes.ConditionDependsOnUnsetProperty);
        await Assert.That(blocker.Severity).IsEqualTo(BlockerSeverity.Warning);
        await Assert.That(blocker.Message).Contains("VSTEST_TO_MTP_UNSET_FLAG");
        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Production);
    }

    [Test]
    public async Task PackageWithoutAnyVersion_IsFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", ScenarioWorkspace.PackageReferences("Some.Package"));

        ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(project.Blockers.Select(b => b.Code)).Contains(BlockerCodes.UnresolvedPackageVersion);
        await Assert.That(project.TargetFrameworks.Single().Packages.Single().VersionSource).IsEqualTo(PackageVersionSource.None);
    }

    [Test]
    public async Task ProjectReferences_RecordWhetherTargetIsInSelection()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("Core/Core.csproj")
            .WriteProject("Other/Other.csproj")
            .WriteProject(
                "App/App.csproj",
                "  <ItemGroup>\n    <ProjectReference Include=\"..\\Core\\Core.csproj\" />\n    <ProjectReference Include=\"..\\Other\\Other.csproj\" />\n  </ItemGroup>")
            .WriteSlnx("All.slnx", "App/App.csproj", "Core/Core.csproj");

        ProjectInventory app = workspace.Inventory("All.slnx").Projects.Single(p => p.Name == "App");

        await Assert.That(app.TargetFrameworks.Single().ProjectReferences.Select(r => (r.Path, r.IsInSelection))).IsEquivalentTo(
            [("Core/Core.csproj", true), ("Other/Other.csproj", false)],
            CollectionOrdering.Matching);
    }

    [Test]
    public async Task GlobalJson_IsParsedAndMalformedOneIsReported()
    {
        using ScenarioWorkspace good = new ScenarioWorkspace()
            .Write("global.json", "{ // comment\n \"sdk\": { \"version\": \"10.0.100\", \"rollForward\": \"latestMajor\", }, \"test\": { \"runner\": \"Microsoft.Testing.Platform\" } }")
            .WriteProject("App/App.csproj");

        GlobalJsonInfo? info = good.Inventory("App/App.csproj").GlobalJson;
        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Path).IsEqualTo("global.json");
        await Assert.That(info.SdkVersion).IsEqualTo("10.0.100");
        await Assert.That(info.RollForward).IsEqualTo("latestMajor");
        await Assert.That(info.TestRunner).IsEqualTo("Microsoft.Testing.Platform");

        using ScenarioWorkspace bad = new ScenarioWorkspace()
            .Write("global.json", "{ \"sdk\": ")
            .WriteProject("App/App.csproj");

        InventoryResult result = bad.Inventory("App/App.csproj");
        await Assert.That(result.GlobalJson).IsNull();
        await Assert.That(result.Blockers.Select(b => b.Code)).Contains(BlockerCodes.MalformedGlobalJson);
    }

    [Test]
    public async Task Automation_FindsCiAndScriptsAndSkipsGeneratedAndHiddenDirectories()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj")
            .Write(".circleci/config.yml", "jobs:\n  test:\n    steps:\n      - run: dotnet test\n")
            .Write(".gitlab-ci.yml", "test:\n  script: dotnet build\n")
            .Write(".github/workflows/ci.yml", "steps:\n  - run: dotnet test\n")
            .Write(".github/.cache/run.sh", "dotnet test")
            .Write("scripts/huge.sh", new string('#', (2 * 1024 * 1024) + 1))
            .Write(".github/workflows/lint.yaml", "steps:\n  - run: echo lint\n")
            .Write("azure-pipelines.yml", "steps:\n- script: dotnet  test\n")
            .Write("scripts/build.ps1", "dotnet build\n")
            .Write("scripts/run.sh", "dotnet test --no-build\n")
            .Write("Makefile", "test:\n\tdotnet test\n")
            .Write("App/bin/Debug/tool.ps1", "dotnet test")
            .Write("App/obj/tool.sh", "dotnet test")
            .Write(".git/hooks/pre-commit.sh", "dotnet test")
            .Write("node_modules/pkg/run.sh", "dotnet test")
            .Write(".cache/hidden.ps1", "dotnet test")
            .Write(".vs/v.cmd", "dotnet test");

        IReadOnlyList<AutomationFile> automation = workspace.Inventory("App/App.csproj").Automation;

        (string, AutomationKind, bool?)[] expected =
            [
                (".circleci/config.yml", AutomationKind.CircleCi, true),
                (".github/workflows/ci.yml", AutomationKind.GitHubWorkflow, true),
                (".github/workflows/lint.yaml", AutomationKind.GitHubWorkflow, false),
                (".gitlab-ci.yml", AutomationKind.GitLabCi, false),
                ("Makefile", AutomationKind.Makefile, true),
                ("azure-pipelines.yml", AutomationKind.AzurePipelines, true),
                ("scripts/build.ps1", AutomationKind.PowerShellScript, false),
                ("scripts/huge.sh", AutomationKind.ShellScript, null),
                ("scripts/run.sh", AutomationKind.ShellScript, true),
            ];

        await Assert.That(automation.Select(a => (a.Path, a.Kind, a.MentionsDotNetTest))).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task Inventory_IsReadOnly()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Packages.props", "<Project>\n  <PropertyGroup>\n    <ManagePackageVersionsCentrally>true</ManagePackageVersionsCentrally>\n  </PropertyGroup>\n  <ItemGroup>\n    <PackageVersion Include=\"MSTest\" Version=\"4.0.2\" />\n  </ItemGroup>\n</Project>")
            .WriteProject("App/App.csproj", ScenarioWorkspace.PackageReferences("MSTest"))
            .WriteSlnx("All.slnx", "App/App.csproj");
        string before = Fixtures.FixtureWorkspace.ComputeHash(workspace.RootPath);

        workspace.Inventory("All.slnx");

        await Assert.That(Fixtures.FixtureWorkspace.ComputeHash(workspace.RootPath)).IsEqualTo(before);
        await Assert.That(Directory.Exists(workspace.PathOf("App/obj"))).IsFalse();
    }
}
