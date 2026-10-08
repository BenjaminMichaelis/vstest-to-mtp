namespace VsTestToMtp.Inventory;

/// <summary>A CI definition or script found under the inventory root.</summary>
/// <param name="MentionsDotNetTest">
/// Whether the file mentions <c>dotnet test</c>; <see langword="null"/> when it could not be inspected
/// (larger than 2 MiB, unreadable or removed), which is different from "inspected and does not mention it".
/// </param>
public sealed record AutomationFile(string Path, AutomationKind Kind, bool? MentionsDotNetTest);
