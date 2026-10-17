namespace VsTestToMtp.Inventory;

/// <summary>The platform a test project runs on, recognised from package evidence.</summary>
public enum TestRunnerKind
{
    /// <summary>VSTest: <c>Microsoft.NET.Test.Sdk</c> and/or a framework's VSTest adapter.</summary>
    VSTest,

    /// <summary>Microsoft.Testing.Platform (MTP).</summary>
    MicrosoftTestingPlatform,
}
