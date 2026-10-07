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
        // A skipped import because of a false condition carries no imported file; missing/empty/invalid ones do.
        if (args is ProjectImportedEventArgs { ImportIgnored: true, ImportedProjectFile: { Length: > 0 } imported } import)
        {
            Skipped.Add(new SkippedImport(imported, import.UnexpandedProject ?? imported, import.ProjectFile ?? string.Empty, import.LineNumber, import.ColumnNumber));
        }
    }
}
