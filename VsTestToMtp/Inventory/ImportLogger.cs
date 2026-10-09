using Microsoft.Build.Framework;

namespace VsTestToMtp.Inventory;

/// <summary>Collects imports MSBuild skipped because the file was missing, empty or invalid.</summary>
internal sealed class ImportLogger : ILogger
{
    public List<SkippedImport> Skipped { get; } = [];

    public LoggerVerbosity Verbosity { get; set; } = LoggerVerbosity.Diagnostic;

    public string? Parameters { get; set; }

    public void Initialize(IEventSource eventSource) => eventSource.MessageRaised += OnMessage;

    public void Shutdown()
    {
    }

    private void OnMessage(object sender, BuildMessageEventArgs args)
    {
        // Missing, invalid and empty imports are all "ignored" (as are false conditions); the detector tells them apart.
        if (args is ProjectImportedEventArgs { ImportIgnored: true } import)
        {
            Skipped.Add(new SkippedImport(
                import.ImportedProjectFile,
                import.UnexpandedProject ?? string.Empty,
                import.ProjectFile ?? string.Empty,
                import.LineNumber,
                import.ColumnNumber));
        }
    }
}
