using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace Forge.Core.Secrets;

public interface ICredentialStore
{
    string? Read(string account);
    void Write(string account, string token);
    void Delete(string account);
}

/// <summary>
/// Tokens live in Windows Credential Manager (Generic credentials, target
/// <c>ForgeForWindows:&lt;account&gt;</c>), never in the state file.
///
/// One credential holds at most 2,560 bytes (CRED_MAX_CREDENTIAL_BLOB_SIZE)
/// and CredWrite refuses more with RPC_X_BAD_STUB_DATA, while a Forge token is
/// a JWT that grows with its scopes and can be longer. So a long token is split
/// across <c>…:&lt;account&gt;</c>, <c>…:&lt;account&gt;#1</c>, <c>#2</c>…, and the
/// first part's comment says how many parts there are — read back from there,
/// so a shorter token written later never picks up a stale tail.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    /// <summary><c>ForgeForWindows:</c>, or <c>ForgeForWindows-dev:</c> in a development profile, so its tokens are its own.</summary>
    public static string TargetPrefix => Setup.AppProfile.CredentialPrefix;
    public const int MaxBlobBytes = 2560;
    private const int MaxParts = 32;
    private const string CommentPrefix = "Laravel Forge API token (Forge for Windows)";

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static string Target(string account) => TargetPrefix + account;

    internal static string PartTarget(string account, int part) => part == 0 ? Target(account) : $"{Target(account)}#{part}";

    /// <summary>The part count recorded on the first credential; one when it records none (e.g. written by cmdkey).</summary>
    internal static int PartsFromComment(string? comment)
    {
        var marker = comment?.LastIndexOf(" of ", StringComparison.Ordinal) ?? -1;
        if (marker < 0) return 1;
        var digits = new string(comment![(marker + 4)..].TakeWhile(char.IsAsciiDigit).ToArray());
        return int.TryParse(digits, out var n) && n is >= 1 and <= MaxParts ? n : 1;
    }

    public string? Read(string account)
    {
        var first = ReadBlob(PartTarget(account, 0));
        if (first is null) return null;
        var parts = PartsFromComment(first.Value.Comment);
        var bytes = new List<byte>(first.Value.Bytes);
        for (var i = 1; i < parts; i++)
        {
            // A missing part means the set is broken; a truncated token would only be rejected later.
            if (ReadBlob(PartTarget(account, i)) is not { } part) return null;
            bytes.AddRange(part.Bytes);
        }
        return bytes.Count == 0 ? null : DecodeBlob(bytes.ToArray());
    }

    private static (byte[] Bytes, string? Comment)? ReadBlob(string target)
    {
        if (!CredRead(target, CredTypeGeneric, 0, out var ptr))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            var bytes = new byte[cred.CredentialBlobSize];
            if (bytes.Length > 0 && cred.CredentialBlob != IntPtr.Zero)
                Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return (bytes, cred.Comment);
        }
        finally
        {
            CredFree(ptr);
        }
    }

    /// <summary>
    /// This app writes UTF-8; <c>cmdkey</c> and the Credential Manager UI write
    /// UTF-16. A Forge token is ASCII, so a zero in every odd byte means UTF-16.
    /// </summary>
    internal static string DecodeBlob(byte[] bytes)
    {
        var utf16 = bytes.Length >= 2 && bytes.Length % 2 == 0;
        for (var i = 1; utf16 && i < bytes.Length; i += 2) utf16 = bytes[i] == 0;
        return (utf16 ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes)).Trim();
    }

    public void Write(string account, string token)
    {
        var bytes = Encoding.UTF8.GetBytes(token);
        var parts = Math.Max(1, (bytes.Length + MaxBlobBytes - 1) / MaxBlobBytes);
        if (parts > MaxParts) throw new InvalidOperationException("That token is too long to store.");

        // Later parts first, so the first part — which names the count — is never ahead of them.
        for (var i = parts - 1; i >= 0; i--)
        {
            var offset = i * MaxBlobBytes;
            var length = Math.Min(MaxBlobBytes, bytes.Length - offset);
            WriteBlob(PartTarget(account, i), account, bytes.AsSpan(offset, Math.Max(0, length)),
                parts == 1 ? CommentPrefix : $"{CommentPrefix}, part {i + 1} of {parts}");
        }
        // Parts a longer earlier token left behind.
        for (var i = parts; i < MaxParts; i++)
            if (!DeleteTarget(PartTarget(account, i))) break;
    }

    private static void WriteBlob(string target, string account, ReadOnlySpan<byte> bytes, string comment)
    {
        var blob = Marshal.AllocHGlobal(Math.Max(1, bytes.Length));
        try
        {
            Marshal.Copy(bytes.ToArray(), 0, blob, bytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = CredPersistLocalMachine,
                UserName = account,
            };
            if (!CredWrite(ref cred, 0)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public void Delete(string account)
    {
        DeleteTarget(PartTarget(account, 0));
        for (var i = 1; i < MaxParts; i++)
            if (!DeleteTarget(PartTarget(account, i))) break;
    }

    /// <summary>False when there was nothing to delete.</summary>
    private static bool DeleteTarget(string target)
    {
        if (CredDelete(target, CredTypeGeneric, 0)) return true;
        var error = Marshal.GetLastPInvokeError();
        if (error == ErrorNotFound) return false;
        throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, int flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = false)]
    private static extern void CredFree(IntPtr buffer);
}

/// <summary>For tests, and for <c>FORGE_TOKEN</c>-style overrides.</summary>
public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    public string? Read(string account) => _tokens.GetValueOrDefault(account);
    public void Write(string account, string token) => _tokens[account] = token;
    public void Delete(string account) => _tokens.Remove(account);
}
