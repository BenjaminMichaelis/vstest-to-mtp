namespace VsTestToMtp.Tests.Inventory;

using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

using VsTestToMtp.Inventory;

// MSBuild evaluation blocks threads; running many evaluations in parallel starves the thread pool and is far slower than serial.
[NotInParallel("MSBuildEvaluation")]
public class InventoryEnvironmentTests
{
    [Test]
    public async Task UnsatisfiableGlobalJson_IsReportedAsMsBuildNotFound_AndDoesNotPoisonLaterSelections()
    {
        using ScenarioWorkspace broken = new ScenarioWorkspace()
            .Write("global.json", """{ "sdk": { "version": "99.0.100", "rollForward": "disable" } }""")
            .WriteProject("App/App.csproj");
        using ScenarioWorkspace healthy = new ScenarioWorkspace()
            .WriteProject("App/App.csproj");

        ProjectInventory failed = broken.Inventory("App/App.csproj").Projects.Single();
        ProjectInventory ok = healthy.Inventory("App/App.csproj").Projects.Single();

        await Assert.That(failed.Classification).IsEqualTo(ProjectClassification.Unknown);
        await Assert.That(failed.Blockers.Select(b => b.Code)).IsEquivalentTo([BlockerCode.MsBuildNotFound]);
        await Assert.That(ok.Classification).IsEqualTo(ProjectClassification.Production);
        await Assert.That(ok.Blockers).IsEmpty();
    }

    [Test]
    public async Task MsBuildHostThatCannotBeStarted_IsReportedAsMsBuildHostFailed()
    {
        // The test layout has no self-contained host to fall back to, so a machine without a usable `dotnet` has nowhere to run it.
        using EnvironmentVariableScope noDotNet = new("DOTNET_HOST_PATH", Path.Combine(Path.GetTempPath(), "no-such-dotnet-" + Guid.NewGuid().ToString("N")));
        using ScenarioWorkspace workspace = new ScenarioWorkspace().WriteProject("App/App.csproj");

        InventoryResult result = workspace.Inventory("App/App.csproj");

        ProjectInventory project = result.Projects.Single();
        await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Unknown);
        await Assert.That(project.Blockers.Select(b => b.Code)).IsEquivalentTo([BlockerCode.MsBuildHostFailed]);
        await Assert.That(result.Blockers.Select(b => b.Code)).Contains(BlockerCode.MsBuildHostFailed);
    }

    [Test]
    [SkipUnlessReadDenialSupported]
    public async Task UnreadableDirectory_IsReportedAsBlocker_AndRestOfInventoryIsKept()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj")
            .Write("scripts/build.ps1", "dotnet build")
            .Write("locked/inner.ps1", "dotnet test");
        string locked = workspace.PathOf("locked");

        using (Lock(locked))
        {
            InventoryResult result = workspace.Inventory("App/App.csproj");

            await Assert.That(result.Projects.Single().Classification).IsEqualTo(ProjectClassification.Production);
            await Assert.That(result.Automation.Select(a => a.Path)).IsEquivalentTo(["scripts/build.ps1"]);
            InventoryBlocker blocker = result.Blockers.Single(b => b.Code == BlockerCode.UnreadableDirectory);
            await Assert.That(blocker.Severity).IsEqualTo(BlockerSeverity.Warning);
            await Assert.That(blocker.Location!.File).IsEqualTo("locked");
            await Assert.That(blocker.Remediation).IsNotEmpty();
        }
    }

    [Test]
    [SkipUnlessReadDenialSupported]
    public async Task UnreadableSelectedDirectory_IsReportedAsBlocker_InsteadOfThrowing()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("sub/App/App.csproj");

        using (Lock(workspace.PathOf("sub")))
        {
            InventoryResult result = InventoryBuilder.Build(workspace.PathOf("sub"), new InventoryOptions(workspace.RootPath));

            await Assert.That(result.Projects).IsEmpty();
            InventoryBlocker blocker = result.Blockers.Single(b => b.Code == BlockerCode.UnreadableDirectory && b.Severity == BlockerSeverity.Error);
            await Assert.That(blocker.Location!.File).IsEqualTo("sub");
            await Assert.That(blocker.Remediation).IsNotEmpty();
        }
    }

    [Test]
    [SkipUnlessReadDenialSupported]
    public async Task UnreadableProjectFile_IsReportedAsBlocker_InsteadOfThrowing()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj");

        using (Lock(workspace.PathOf("App/App.csproj")))
        {
            ProjectInventory project = workspace.Inventory("App/App.csproj").Projects.Single();

            await Assert.That(project.Classification).IsEqualTo(ProjectClassification.Unknown);
            InventoryBlocker blocker = project.Blockers.Single(b => b.Code == BlockerCode.EvaluationFailed);
            await Assert.That(blocker.Severity).IsEqualTo(BlockerSeverity.Error);
            await Assert.That(blocker.Location!.File).IsEqualTo("App/App.csproj");
        }
    }

    [Test]
    [SkipUnlessReadDenialSupported]
    public async Task UnreadableSolutionFile_IsReportedAsBlocker_InsteadOfThrowing()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("App/App.csproj")
            .WriteSlnx("All.slnx", "App/App.csproj");

        using (Lock(workspace.PathOf("All.slnx")))
        {
            InventoryResult result = workspace.Inventory("All.slnx");

            await Assert.That(result.Projects).IsEmpty();
            InventoryBlocker blocker = result.Blockers.Single(b => b.Code == BlockerCode.MalformedSolution);
            await Assert.That(blocker.Severity).IsEqualTo(BlockerSeverity.Error);
            await Assert.That(blocker.Location!.File).IsEqualTo("All.slnx");
        }
    }

    [Test]
    [SkipUnlessReadDenialSupported]
    public async Task UnreadableGlobalJson_IsReportedAsBlocker_InsteadOfThrowing()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .Write("global.json", """{ "sdk": { "version": "10.0.100" } }""")
            .WriteProject("App/App.csproj");

        using (Lock(workspace.PathOf("global.json")))
        {
            InventoryResult result = workspace.Inventory("App/App.csproj");

            await Assert.That(result.GlobalJson).IsNull();
            InventoryBlocker blocker = result.Blockers.Single(b => b.Code == BlockerCode.UnreadableGlobalJson);
            await Assert.That(blocker.Severity).IsEqualTo(BlockerSeverity.Error);
            await Assert.That(blocker.Location!.File).IsEqualTo("global.json");
        }
    }

    [Test]
    [SkipOnCaseInsensitivePaths]
    public async Task ProjectReference_DifferingOnlyByCase_IsNotInSelectionOnCaseSensitiveFileSystems()
    {
        using ScenarioWorkspace workspace = new ScenarioWorkspace()
            .WriteProject("A/A.csproj")
            .WriteProject("a/a.csproj")
            .WriteProject("App/App.csproj", """
                  <ItemGroup>
                    <ProjectReference Include="../a/a.csproj" />
                  </ItemGroup>
                """)
            .WriteSlnx("All.slnx", "App/App.csproj", "A/A.csproj");

        ProjectInventory app = workspace.Inventory("All.slnx").Projects.Single(p => p.Name == "App");

        ProjectReferenceState reference = app.TargetFrameworks.Single().ProjectReferences.Single();
        await Assert.That(reference.Path).IsEqualTo("a/a.csproj");
        await Assert.That(reference.IsInSelection).IsFalse();
    }

    // Denies reading the file or listing the directory for the current user (ACL on Windows, mode bits elsewhere) until disposed.
    private static Restore Lock(string path) =>
        OperatingSystem.IsWindows() ? LockWithAcl(path) : LockWithMode(path);

    [SupportedOSPlatform("windows")]
    private static Restore LockWithAcl(string path)
    {
        SecurityIdentifier me = WindowsIdentity.GetCurrent().User!;
        FileSystemAccessRule deny = new(me, FileSystemRights.ListDirectory | FileSystemRights.ReadData, AccessControlType.Deny);

        if (Directory.Exists(path))
        {
            DirectoryInfo directory = new(path);
            DirectorySecurity security = directory.GetAccessControl();
            security.AddAccessRule(deny);
            directory.SetAccessControl(security);
            return new Restore(() =>
            {
                DirectorySecurity current = directory.GetAccessControl();
                current.RemoveAccessRule(deny);
                directory.SetAccessControl(current);
            });
        }

        FileInfo file = new(path);
        FileSecurity fileSecurity = file.GetAccessControl();
        fileSecurity.AddAccessRule(deny);
        file.SetAccessControl(fileSecurity);
        return new Restore(() =>
        {
            FileSecurity current = file.GetAccessControl();
            current.RemoveAccessRule(deny);
            file.SetAccessControl(current);
        });
    }

    [UnsupportedOSPlatform("windows")]
    private static Restore LockWithMode(string path)
    {
        UnixFileMode original = File.GetUnixFileMode(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        return new Restore(() => File.SetUnixFileMode(path, original));
    }

    private sealed class Restore(Action undo) : IDisposable
    {
        public void Dispose() => undo();
    }
}
