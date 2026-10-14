namespace VsTestToMtp.Inventory;

internal sealed record SkippedImport(string? ImportedFile, string UnexpandedProject, string ImportingFile, int Line, int Column);
