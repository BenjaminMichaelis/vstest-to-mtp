namespace VsTestToMtp.Tests.Inventory;

using VsTestToMtp.Inventory;

// MSBuild evaluation blocks threads; running many evaluations in parallel starves the thread pool and is far slower than serial.
[NotInParallel("MSBuildEvaluation")]
public class InventoryConditionTests
{
    private const string GuardedProperty = """
          <PropertyGroup Condition="'$(Flag)' == 'true'">
            <IsPackable>false</IsPackable>
          </PropertyGroup>
        """;

    private const string DefineFlag = """
          <PropertyGroup>
            <Flag>true</Flag>
          </PropertyGroup>
        """;

    [Test]
    public async Task PropertyCondition_OnAPropertyDefinedLater_IsFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", $"""
                {GuardedProperty}
                {DefineFlag}
                """);

        InventoryBlocker blocker = Blockers(workspace).Single(b => b.Code == BlockerCode.ConditionDependsOnUnsetProperty);

        // The property is defined, but only after the condition that tests it.
        await Assert.That(blocker.RelatedLocations.Select(l => (l.File, l.Line))).IsEquivalentTo([("App/App.csproj", workspace.LineOf("App/App.csproj", "<Flag>"))]);
        await Assert.That(blocker.Location!.File).IsEqualTo("App/App.csproj");
        // The guarded element is the IsPackable property inside the conditioned PropertyGroup.
        await Assert.That(blocker.Location.Line).IsEqualTo(workspace.LineOf("App/App.csproj", "<IsPackable>"));
    }

    [Test]
    public async Task PropertyCondition_OnAPropertyDefinedEarlier_IsNotFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", $"""
                {DefineFlag}
                {GuardedProperty}
                """);

        await Assert.That(Blockers(workspace)).IsEmpty();
    }

    [Test]
    public async Task PropertyCondition_OnAPropertyFromDirectoryBuildProps_IsNotFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Build.props", $"""
                <Project>
                {DefineFlag}
                </Project>
                """)
            .WriteProject("App/App.csproj", GuardedProperty);

        await Assert.That(Blockers(workspace)).IsEmpty();
    }

    [Test]
    public async Task PropertyCondition_OnAPropertyFromDirectoryBuildTargets_IsFlagged()
    {
        // Directory.Build.targets is imported after the project body, so the property is not yet set there.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Build.targets", $"""
                <Project>
                {DefineFlag}
                </Project>
                """)
            .WriteProject("App/App.csproj", GuardedProperty);

        await Assert.That(Blockers(workspace).Select(b => b.Code)).IsEquivalentTo([BlockerCode.ConditionDependsOnUnsetProperty]);
    }

    [Test]
    public async Task ItemCondition_SeesPropertiesDefinedAfterIt()
    {
        // MSBuild evaluates every property before any item, so the final value is what the item condition sees.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                $"""
                  <ItemGroup Condition="'$(Flag)' == 'true'">
                    <PackageReference Include="NUnit" Version="4.3.2" />
                  </ItemGroup>
                {DefineFlag}
                """);

        await Assert.That(Blockers(workspace)).IsEmpty();
    }

    [Test]
    public async Task EmptyLiteralComparison_IsADeliberateUnsetProbe_AndNotFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                """
                  <PropertyGroup Condition="'$(VSTEST_TO_MTP_UNSET_FLAG)' == ''">
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                """);

        await Assert.That(Blockers(workspace)).IsEmpty();
    }

    [Test]
    public async Task EnvironmentProperty_IsFlagged_EvenWhenComparedWithAnEmptyLiteral()
    {
        const string variable = "VSTEST_TO_MTP_ENV_FLAG";
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                $"""
                  <PropertyGroup Condition="'$({variable})' != ''">
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                """);

        Environment.SetEnvironmentVariable(variable, "1");
        try
        {
            InventoryBlocker blocker = Blockers(workspace).Single(b => b.Code == BlockerCode.ConditionDependsOnEnvironment);

            await Assert.That(blocker.Severity).IsEqualTo(BlockerSeverity.Warning);
            await Assert.That(blocker.Message).Contains(variable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Test]
    public async Task BareBooleanCondition_OnAnEnvironmentProperty_IsFlagged()
    {
        const string variable = "VSTEST_TO_MTP_ENV_BOOL";
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                $"""
                  <PropertyGroup Condition="$({variable})">
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                """);

        Environment.SetEnvironmentVariable(variable, "true");
        try
        {
            await Assert.That(Blockers(workspace).Select(b => b.Code)).IsEquivalentTo([BlockerCode.ConditionDependsOnEnvironment]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Test]
    public async Task EnvironmentValue_SeenByAnEarlierCondition_IsFlagged_EvenIfALaterAssignmentOverridesIt()
    {
        // The final value of Flag comes from the project file, but when the condition runs it still comes from the environment.
        const string variable = "VSTEST_TO_MTP_ENV_OVERRIDDEN";
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                $"""
                  <PropertyGroup Condition="'$({variable})' != ''">
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                  <PropertyGroup>
                    <{variable}>from-file</{variable}>
                  </PropertyGroup>
                """);

        Environment.SetEnvironmentVariable(variable, "1");
        try
        {
            await Assert.That(Blockers(workspace).Select(b => b.Code)).IsEquivalentTo([BlockerCode.ConditionDependsOnEnvironment]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Test]
    [Arguments("MSBuildProjectName")]
    [Arguments("MSBuildProjectDirectory")]
    [Arguments("MSBuildProjectFile")]
    [Arguments("MSBuildProjectFullPath")]
    [Arguments("MSBuildProjectExtensionsPath")]
    [Arguments("MSBuildStartupDirectory")]
    [Arguments("MSBuildToolsPath")]
    [Arguments("MSBuildToolsVersion")]
    [Arguments("MSBuildBinPath")]
    [Arguments("MSBuildExtensionsPath")]
    [Arguments("MSBuildSDKsPath")]
    [Arguments("MSBuildRuntimeType")]
    [Arguments("MSBuildThisFileDirectory")]
    [Arguments("MSBuildThisFile")]
    public async Task ReservedMsBuildProperty_IsNotFlagged(string property)
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                $"""
                  <PropertyGroup Condition="'$({property})' != 'no-such-value'">
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                """);

        await Assert.That(Blockers(workspace)).IsEmpty();
    }

    [Test]
    public async Task CustomPropertyThatMerelyStartsWithMsBuild_IsAnalysedLikeAnyOther()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                """
                  <PropertyGroup Condition="'$(MSBuildFeatureFlag)' == 'on'">
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                """);

        await Assert.That(Blockers(workspace).Select(b => b.Code)).IsEquivalentTo([BlockerCode.ConditionDependsOnUnsetProperty]);
    }

    [Test]
    public async Task EnvironmentPropertyThatMerelyStartsWithMsBuild_IsFlagged()
    {
        const string variable = "MSBuildEnableWorkloadResolverForTest";
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                $"""
                  <PropertyGroup Condition="'$({variable})' == 'true'">
                    <IsPackable>false</IsPackable>
                  </PropertyGroup>
                """);

        Environment.SetEnvironmentVariable(variable, "true");
        try
        {
            await Assert.That(Blockers(workspace).Select(b => b.Code)).IsEquivalentTo([BlockerCode.ConditionDependsOnEnvironment]);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    [Test]
    public async Task OsProperty_IsFlagged_BecauseItsValueDiffersBetweenWindowsAndUnix()
    {
        // The same project can be a test application on Windows and production on Linux when a package is guarded by $(OS).
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <ItemGroup Condition=\"'$(OS)' == 'Windows_NT'\">\n    <PackageReference Include=\"MSTest\" Version=\"4.0.2\" />\n  </ItemGroup>");

        InventoryBlocker blocker = Blockers(workspace).Single(b => b.Code == BlockerCode.ConditionDependsOnEnvironment);

        await Assert.That(blocker.Message).Contains("OS");
    }

    [Test]
    public async Task OsProperty_AssignedByTheProjectBeforeTheCondition_IsNotFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <PropertyGroup>\n    <OS>Custom</OS>\n  </PropertyGroup>\n"
                + "  <PropertyGroup Condition=\"'$(OS)' == 'Custom'\">\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>");

        await Assert.That(Blockers(workspace)).IsEmpty();
    }

    [Test]
    public async Task ConditionOnPackageMetadata_DependingOnAnUnsetProperty_IsFlagged()
    {
        // The Version can come from conditional metadata, so the condition decides the effective version.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <ItemGroup>\n    <PackageReference Include=\"NUnit\">\n      <Version Condition=\"'$(VSTEST_TO_MTP_UNSET_FLAG)' == 'x'\">4.3.2</Version>\n    </PackageReference>\n  </ItemGroup>");

        InventoryBlocker blocker = Blockers(workspace).Single(b => b.Code == BlockerCode.ConditionDependsOnUnsetProperty);

        await Assert.That(blocker.Location!.Line).IsEqualTo(workspace.LineOf("App/App.csproj", "<Version Condition"));
    }

    [Test]
    public async Task ConditionOnItemDefinitionMetadata_DependingOnAnUnsetProperty_IsFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <ItemDefinitionGroup>\n    <PackageReference>\n      <VersionOverride Condition=\"'$(VSTEST_TO_MTP_UNSET_FLAG)' == 'x'\">4.3.2</VersionOverride>\n    </PackageReference>\n  </ItemDefinitionGroup>");

        await Assert.That(Blockers(workspace).Select(b => b.Code)).Contains(BlockerCode.ConditionDependsOnUnsetProperty);
    }

    [Test]
    public async Task ConditionOnMetadataOfAnIrrelevantItemType_IsNotFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <ItemGroup>\n    <Content Include=\"a.txt\">\n      <Link Condition=\"'$(VSTEST_TO_MTP_UNSET_FLAG)' == 'x'\">b.txt</Link>\n    </Content>\n  </ItemGroup>");

        await Assert.That(Blockers(workspace)).IsEmpty();
    }

    [Test]
    public async Task HyphenatedPropertyName_IsAnalysedLikeAnyOther()
    {
        // MSBuild property names may contain hyphens, so a condition on an unset one must still be reported.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <PropertyGroup Condition=\"'$(Feature-Flag)' == 'true'\">\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>");

        InventoryBlocker blocker = Blockers(workspace).Single(b => b.Code == BlockerCode.ConditionDependsOnUnsetProperty);

        await Assert.That(blocker.Message).Contains("Feature-Flag");
    }

    [Test]
    public async Task HyphenatedPropertyThatIsDefined_IsNotFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <PropertyGroup>\n    <Feature-Flag>true</Feature-Flag>\n  </PropertyGroup>\n"
                + "  <PropertyGroup Condition=\"'$(Feature-Flag)' == 'true'\">\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>");

        await Assert.That(Blockers(workspace)).IsEmpty();
    }

    private static IReadOnlyList<InventoryBlocker> Blockers(ScenarioWorkspace workspace) =>
        workspace.Inventory("App/App.csproj").Projects.Single().Blockers;
}
