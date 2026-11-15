using Microsoft.Win32;

namespace Forge.App.Services;

/// <summary>The per-user Run key, written only when the user turns "Start with Windows" on.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ForgeForWindows";

    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsRegistered()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(ValueName) is string;
    }

    /// <summary>Makes the Run key match the setting, and points it at this exe if the app moved.</summary>
    public static void Sync(bool enabled)
    {
        // The Run value is the installed app's; a development profile pointing it at a build folder
        // would make Windows start the wrong copy at sign-in.
        if (!Forge.Core.Setup.AppProfile.IsDefault) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (enabled)
            {
                if (key.GetValue(ValueName) as string != Command) key.SetValue(ValueName, Command);
            }
            else if (key.GetValue(ValueName) is not null)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
            }
        }
        catch (Exception e)
        {
            Log.Write("Startup registration: " + e.Message);
        }
    }
}
