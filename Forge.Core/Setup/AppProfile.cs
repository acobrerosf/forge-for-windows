namespace Forge.Core.Setup;

/// <summary>
/// Which copy of the app's data a run uses. The installed app uses the default
/// profile; a development build uses "dev", so testing never reads the real
/// tokens, never hands its launch to (or has to stop) the installed copy, and
/// never rewrites its start-up entry.
///
/// A profile keeps apart: the state file and log (<c>%LOCALAPPDATA%\ForgeForWindows-dev</c>),
/// the stored tokens (<c>ForgeForWindows-dev:&lt;account&gt;</c>), the single-instance key, and
/// the window titles and tray tooltip, which say "(dev)". Only the default profile
/// may start with Windows.
/// </summary>
public static class AppProfile
{
    private const string App = "ForgeForWindows";

    /// <summary>"" for the default profile, otherwise a short lower-case name such as "dev".</summary>
    public static string Name { get; private set; } = "";

    public static bool IsDefault => Name == "";

    /// <summary>
    /// Picks the profile once, at start-up. <c>FORGE_PROFILE</c> wins when set ("default" means the
    /// real one); otherwise <paramref name="fallback"/> ("" in release builds, "dev" in debug ones).
    /// </summary>
    public static void Choose(string fallback)
    {
        var requested = Environment.GetEnvironmentVariable("FORGE_PROFILE");
        Name = Clean(string.IsNullOrWhiteSpace(requested) ? fallback : requested);
    }

    /// <summary>Lower-case letters, digits and dashes only, since the name ends up in a folder and a credential name.</summary>
    internal static string Clean(string? name)
    {
        var cleaned = new string((name ?? "").Trim().ToLowerInvariant().Where(c => char.IsAsciiLetterOrDigit(c) || c == '-').ToArray());
        return cleaned is "" or "default" ? "" : cleaned;
    }

    /// <summary>The name things are stored under: ForgeForWindows, or ForgeForWindows-dev.</summary>
    public static string StorageName => IsDefault ? App : $"{App}-{Name}";

    /// <summary>Where the state file and log live. <c>FORGE_STATE_DIR</c> still overrides it, for throwaway state.</summary>
    public static string DataDirectory =>
        Environment.GetEnvironmentVariable("FORGE_STATE_DIR") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), StorageName);

    /// <summary>Credential Manager targets start with this, then the account name.</summary>
    public static string CredentialPrefix => StorageName + ":";

    /// <summary>What a second launch looks for to hand its activation to; each profile runs once, beside the others.</summary>
    public static string InstanceKey => IsDefault ? $"{App}.main" : $"{App}.{Name}";

    /// <summary>Appended to titles and the tray tooltip so a test copy is never mistaken for the real one.</summary>
    public static string Label => IsDefault ? "" : $" ({Name})";
}
