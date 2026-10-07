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
            .WriteProject("App/App.csproj", GuardedProperty + "\n" + DefineFlag);

        InventoryBlocker blocker = Blockers(workspace).Single(b => b.Code == BlockerCodes.ConditionDependsOnUnsetProperty);

        await Assert.That(blocker.Message).Contains("later");
        await Assert.That(blocker.Location!.File).IsEqualTo("App/App.csproj");
        // The guarded element is the IsPackable property inside the conditioned PropertyGroup.
        await Assert.That(blocker.Location.Line).IsEqualTo(workspace.LineOf("App/App.csproj", "<IsPackable>"));
    }

    [Test]
    public async Task PropertyCondition_OnAPropertyDefinedEarlier_IsNotFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", DefineFlag + "\n" + GuardedProperty);

        await Assert.That(Blockers(workspace)).IsEmpty();
    }

    [Test]
    public async Task PropertyCondition_OnAPropertyFromDirectoryBuildProps_IsNotFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Build.props", "<Project>\n" + DefineFlag + "\n</Project>")
            .WriteProject("App/App.csproj", GuardedProperty);

        await Assert.That(Blockers(workspace)).IsEmpty();
    }

    [Test]
    public async Task PropertyCondition_OnAPropertyFromDirectoryBuildTargets_IsFlagged()
    {
        // Directory.Build.targets is imported after the project body, so the property is not yet set there.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("Directory.Build.targets", "<Project>\n" + DefineFlag + "\n</Project>")
            .WriteProject("App/App.csproj", GuardedProperty);

        await Assert.That(Blockers(workspace).Select(b => b.Code)).Contains(BlockerCodes.ConditionDependsOnUnsetProperty);
    }

    [Test]
    public async Task ItemCondition_SeesPropertiesDefinedAfterIt()
    {
        // MSBuild evaluates every property before any item, so the final value is what the item condition sees.
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <ItemGroup Condition=\"'$(Flag)' == 'true'\">\n    <PackageReference Include=\"NUnit\" Version=\"4.3.2\" />\n  </ItemGroup>\n" + DefineFlag);

        await Assert.That(Blockers(workspace)).IsEmpty();
    }

    [Test]
    public async Task EmptyLiteralComparison_IsADeliberateUnsetProbe_AndNotFlagged()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                "  <PropertyGroup Condition=\"'$(VSTEST_TO_MTP_UNSET_FLAG)' == ''\">\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>");

        await Assert.That(Blockers(workspace)).IsEmpty();
    }

    [Test]
    public async Task EnvironmentProperty_IsFlagged_EvenWhenComparedWithAnEmptyLiteral()
    {
        const string variable = "VSTEST_TO_MTP_ENV_FLAG";
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject(
                "App/App.csproj",
                $"  <PropertyGroup Condition=\"'$({variable})' != ''\">\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>");

        Environment.SetEnvironmentVariable(variable, "1");
        try
        {
            InventoryBlocker blocker = Blockers(workspace).Single(b => b.Code == BlockerCodes.ConditionDependsOnEnvironment);

            await Assert.That(blocker.Severity).IsEqualTo(BlockerSeverity.Warning);
            await Assert.That(blocker.Message).Contains(variable);
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }

    private static IReadOnlyList<InventoryBlocker> Blockers(ScenarioWorkspace workspace) =>
        workspace.Inventory("App/App.csproj").Projects.Single().Blockers;
}
