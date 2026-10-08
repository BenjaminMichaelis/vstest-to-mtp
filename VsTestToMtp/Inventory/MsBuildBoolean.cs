namespace VsTestToMtp.Inventory;

/// <summary>Parses booleans the way MSBuild conditions do: true/on/yes and false/off/no, case-insensitively.</summary>
internal static class MsBuildBoolean
{
    public static bool TryParse(string? value, out bool result)
    {
        string? text = value?.Trim().ToLowerInvariant();
        if (text is { Length: > 1 } && text[0] == '!' && TryParse(text[1..], out bool negated) && text[1] != '!')
        {
            // MSBuild also accepts the negated spellings (!true, !false, !on, ...).
            result = !negated;
            return true;
        }

        switch (text)
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
