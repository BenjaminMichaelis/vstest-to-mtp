namespace VsTestToMtp.Inventory;

public enum ProjectClassification
{
    /// <summary>State could not be evaluated; see the project's blockers.</summary>
    Unknown,

    /// <summary>Not a test application (may still reference a test library).</summary>
    Production,

    /// <summary>A test application.</summary>
    TestApplication,

    /// <summary>Target frameworks disagree.</summary>
    Mixed,
}
