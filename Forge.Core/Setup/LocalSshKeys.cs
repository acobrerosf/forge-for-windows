using Forge.Core.Model;

namespace Forge.Core.Setup;

/// <summary>One public key found on this machine.</summary>
public sealed record LocalSshKey(string FileName, string Key, string Comment);

/// <summary>
/// The public keys in <c>%USERPROFILE%\.ssh</c>, for "Add my key". Only <c>*.pub</c> files at the
/// top of the folder are opened; a private key file is never read, whatever it is called.
/// </summary>
public static class LocalSshKeys
{
    /// <summary>A public key is one line; anything much bigger is not one.</summary>
    private const int MaxBytes = 16 * 1024;

    public static string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");

    /// <summary>Keys that pass <see cref="Actions.SshKeyProblem"/>, ed25519 first, then by file name.</summary>
    public static IReadOnlyList<LocalSshKey> Find(string? directory = null)
    {
        var dir = directory ?? DefaultDirectory;
        var list = new List<LocalSshKey>();
        IEnumerable<string> files;
        try
        {
            if (!Directory.Exists(dir)) return list;
            files = Directory.EnumerateFiles(dir, "*.pub", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return list;
        }

        foreach (var file in files)
            if (Read(file).Key is { } key) list.Add(key);

        return list
            .OrderBy(k => k.Key.StartsWith("ssh-ed25519 ", StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(k => k.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// One public key file the reader chose. Only a <c>.pub</c> file is opened, so a private key is never
    /// read even when picked by mistake; anything else gets the reason it can't be used.
    /// </summary>
    public static (LocalSshKey? Key, string Problem) Read(string path)
    {
        // On Windows "*.pub" also matches longer extensions such as ".pubx" (8.3 names); insist on the real one.
        if (!string.Equals(Path.GetExtension(path), ".pub", StringComparison.OrdinalIgnoreCase))
            return (null, "Pick the public key: the file that ends in .pub");
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (null, "That file is gone");
            // Symlinks and junctions only: a OneDrive placeholder is a reparse point too, but no link.
            if (info.LinkTarget is not null) return (null, "That file is a link; pick the key itself");
            if (info.Length > MaxBytes) return (null, "That file is too big to be a public key");
            var key = File.ReadAllText(path).Trim();
            var problem = Actions.SshKeyProblem(key);
            return problem != "" ? (null, problem) : (new LocalSshKey(info.Name, key, Actions.SshKeyComment(key)), "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, "Could not read that file: " + e.Message);
        }
    }
}
