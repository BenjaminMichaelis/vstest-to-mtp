namespace VsTestToMtp.Tests.Inventory;

using VsTestToMtp.Inventory;

/// <summary>
/// Tests that check the inventory against what MSBuild itself does, so semantics we re-implement
/// (here: which spellings count as a boolean) are pinned to MSBuild's behavior instead of our assumption of it.
/// </summary>
// MSBuild evaluation blocks threads; running many evaluations in parallel starves the thread pool and is far slower than serial.
[NotInParallel("MSBuildEvaluation")]
public class InventoryMsBuildOracleTests
{
    [Test]
    [Arguments("true")]
    [Arguments("TRUE")]
    [Arguments("yes")]
    [Arguments("On")]
    [Arguments("!false")]
    [Arguments("!off")]
    [Arguments("false")]
    [Arguments("no")]
    [Arguments("OFF")]
    [Arguments("!true")]
    [Arguments("!yes")]
    [Arguments("1")]
    [Arguments("0")]
    [Arguments("maybe")]
    public async Task IsTestProject_IsInterpretedAsMsBuildInterpretsIt(string value)
    {
        // The oracle: a condition in boolean context makes MSBuild itself decide whether the value is true, false or invalid.
        using ScenarioWorkspace oracle = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", $"  <PropertyGroup>\n    <IsTestProject>{value}</IsTestProject>\n  </PropertyGroup>\n  <PropertyGroup Condition=\"$(IsTestProject)\">\n    <IsPackable>false</IsPackable>\n  </PropertyGroup>");
        ProjectInventory evaluated = oracle.Inventory("App/App.csproj").Projects.Single();
        bool? msBuildSaysTrue = evaluated.Classification == ProjectClassification.Unknown
            ? null
            : evaluated.TargetFrameworks.Single().GetProperty("IsPackable")!.Value == "false";

        using ScenarioWorkspace plain = new ScenarioWorkspace()
            .WriteProject("App/App.csproj", $"  <PropertyGroup>\n    <IsTestProject>{value}</IsTestProject>\n  </PropertyGroup>");
        ProjectInventory classified = plain.Inventory("App/App.csproj").Projects.Single();

        switch (msBuildSaysTrue)
        {
            case true:
                await Assert.That(classified.Classification).IsEqualTo(ProjectClassification.TestApplication);
                break;
            case false:
                await Assert.That(classified.Classification).IsEqualTo(ProjectClassification.Production);
                break;
            default:
                // MSBuild rejects the value (MSB4113), so the classification must be undeterminable, not guessed.
                await Assert.That(evaluated.Blockers.Select(b => b.Message)).Contains(m => m.Contains("MSB4113"));
                await Assert.That(classified.Classification).IsEqualTo(ProjectClassification.Unknown);
                await Assert.That(classified.Blockers.Select(b => b.Code)).Contains(BlockerCodes.InvalidIsTestProject);
                break;
        }
    }
}
