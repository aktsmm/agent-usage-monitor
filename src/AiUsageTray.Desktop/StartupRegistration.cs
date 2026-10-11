using System;
using System.IO;
using Microsoft.Win32;

namespace AiUsageTray.Desktop;

public static class StartupRegistration
{
    public const string ValueName = "AiUsageTray";

    public static string Command(string executable) => $"\"{Path.GetFullPath(executable)}\" --background";

    public static bool IsEnabled(string executable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return string.Equals(key?.GetValue(ValueName) as string, Command(executable), StringComparison.OrdinalIgnoreCase);
    }

    public static bool BelongsToInstallation(string? command, string rootDirectory)
    {
        if (command is null || !command.EndsWith("\" --background", StringComparison.Ordinal))
            return false;
        if (!command.StartsWith('"'))
            return false;
        string executable = command[1..^14];
        if (!Path.IsPathFullyQualified(executable) ||
            !string.Equals(Path.GetFileName(executable), "AiUsageTray.Desktop.exe", StringComparison.OrdinalIgnoreCase))
            return false;
        var root = Path.GetFullPath(rootDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(executable).StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static void Set(bool enabled, string executable)
    {
        using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        if (enabled)
            key.SetValue(ValueName, Command(executable));
        else
        {
            var command = key.GetValue(ValueName) as string;
            if (command == Command(executable))
                key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }

    public static void BeforeUninstall()
    {
        var executable = Environment.ProcessPath;
        if (executable is null) return;
        var current = Path.GetDirectoryName(executable)!;
        var root = string.Equals(Path.GetFileName(current), "current", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(current)! : current;
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        if (BelongsToInstallation(key?.GetValue(ValueName) as string, root))
            key!.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
