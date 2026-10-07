namespace VsTestToMtp.Inventory;

/// <summary>A CI definition or script found under the inventory root.</summary>
public sealed record AutomationFile(string Path, AutomationKind Kind, bool MentionsDotNetTest);
