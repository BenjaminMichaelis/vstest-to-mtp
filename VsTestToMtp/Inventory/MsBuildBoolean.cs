namespace VsTestToMtp.Inventory;

/// <summary>Parses booleans the way MSBuild conditions do: true/on/yes and false/off/no, case-insensitively.</summary>
internal static class MsBuildBoolean
{
    public static bool TryParse(string? value, out bool result)
    {
        switch (value?.Trim().ToLowerInvariant())
        {
            case "true" or "on" or "yes":
                result = true;
                return true;
            case "false" or "off" or "no":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}
