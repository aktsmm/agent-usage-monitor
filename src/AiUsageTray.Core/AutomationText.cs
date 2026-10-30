namespace AiUsageTray.Core;

public static class AutomationText
{
    /// <summary>
    /// WPF button-like controls treat "_" in an automation name as an access-key marker and drop it.
    /// Doubling it keeps account names such as "tatsumiy_microsoft" intact for screen readers.
    /// </summary>
    public static string ForButtonLike(string name) => name.Replace("_", "__", StringComparison.Ordinal);
}
