namespace VsTestToMtp.Migration;

/// <summary>Where a project stands against the migration rules, decided before anything is changed.</summary>
public enum MigrationReadiness
{
    /// <summary>Not a test application; nothing to migrate (it may still reference a test library).</summary>
    NotATestApplication,

    /// <summary>A test application the rules can migrate; <see cref="ProjectPreflight.PackageChanges"/> lists the package edits it needs.</summary>
    Eligible,

    /// <summary>Already configured for Microsoft.Testing.Platform v2; nothing in the project needs to change.</summary>
    AlreadyMigrated,

    /// <summary>Cannot be migrated as it is; see the project's error diagnostics.</summary>
    Blocked,
}
