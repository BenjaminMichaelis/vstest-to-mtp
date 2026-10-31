namespace VsTestToMtp.Migration;

public enum PackageChangeKind
{
    /// <summary>Raise the version of the same package.</summary>
    Upgrade,

    /// <summary>Swap the package for another variant of the same package family (<c>xunit.v3</c> for <c>xunit.v3.mtp-v2</c>), possibly at another version.</summary>
    Replace,

    /// <summary>
    /// Add a package of the framework's own family that the project lacks (the NUnit or MSTest adapter, without which nothing runs its tests
    /// on Microsoft.Testing.Platform).
    /// </summary>
    Add,

    /// <summary>Raise the version of a project SDK (<c>MSTest.Sdk</c>).</summary>
    SdkUpgrade,
}
