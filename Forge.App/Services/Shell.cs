using System.Diagnostics;
using System.Globalization;
using Forge.Core.Model;
using Windows.ApplicationModel.DataTransfer;

namespace Forge.App.Services;

/// <summary>Everything that leaves the app for another program goes through here, re-validated at the boundary.</summary>
internal static class Shell
{
    /// <summary>Opens an address in the default browser — only one <see cref="ForgeText.ExternalUrl"/> vouches for.</summary>
    public static bool OpenUrl(string? url)
    {
        var safe = ForgeText.ExternalUrl(url);
        if (safe == "") return false;
        try
        {
            Process.Start(new ProcessStartInfo(safe) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception e)
        {
            Log.Write("OpenUrl: " + e.Message);
            return false;
        }
    }

    public static void CopyText(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContent(package);
        Clipboard.Flush();
    }

    /// <summary>
    /// Text that holds credentials (an environment file): on the clipboard for a paste, but kept
    /// out of Win+V history and off the clipboard sync to other devices.
    /// </summary>
    public static void CopySecret(string text)
    {
        var package = new DataPackage { RequestedOperation = DataPackageOperation.Copy };
        package.SetText(text);
        Clipboard.SetContentWithOptions(package, new ClipboardContentOptions { IsAllowedInHistory = false, IsRoamable = false });
        Clipboard.Flush();
    }

    /// <summary>
    /// <c>wt.exe ssh forge@host -p N</c>, as an argv — no shell string, and the
    /// host is held to address characters first. Falls back to a plain console.
    /// </summary>
    public static bool OpenSsh(Server server)
    {
        if (!ForgeText.IsSafeHost(server.Ip)) return false;
        var target = "forge@" + server.Ip;
        var port = server.SshPort.ToString(CultureInfo.InvariantCulture);
        try
        {
            var wt = new ProcessStartInfo("wt.exe") { UseShellExecute = false };
            foreach (var a in new[] { "new-tab", "--title", server.Name.Replace(';', ' '), "ssh", target, "-p", port })
                wt.ArgumentList.Add(a);
            Process.Start(wt)?.Dispose();
            return true;
        }
        catch (Exception)
        {
            try
            {
                var ssh = new ProcessStartInfo("ssh.exe") { UseShellExecute = true };
                ssh.ArgumentList.Add(target);
                ssh.ArgumentList.Add("-p");
                ssh.ArgumentList.Add(port);
                Process.Start(ssh)?.Dispose();
                return true;
            }
            catch (Exception e)
            {
                Log.Write("OpenSsh: " + e.Message);
                return false;
            }
        }
    }

    public static string DownloadsFolder()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(path) ? path : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    public static void RevealFolder(string folder)
    {
        try { Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { folder } })?.Dispose(); }
        catch (Exception e) { Log.Write("Reveal: " + e.Message); }
    }
}

internal static class Log
{
    private static readonly object Gate = new();

    public static string Path { get; } = System.IO.Path.Combine(Forge.Core.Setup.AppProfile.DataDirectory, "app.log");

    public static void Write(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
                var info = new FileInfo(Path);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                File.AppendAllText(Path, $"{DateTimeOffset.Now:u} {message}{Environment.NewLine}");
            }
        }
        catch (IOException)
        {
        }
    }
}
