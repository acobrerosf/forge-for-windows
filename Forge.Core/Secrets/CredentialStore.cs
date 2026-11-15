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
/// </summary>
[SupportedOSPlatform("windows")]
public sealed partial class WindowsCredentialStore : ICredentialStore
{
    public const string TargetPrefix = "ForgeForWindows:";

    private const int CredTypeGeneric = 1;
    private const int CredPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;

    public static string Target(string account) => TargetPrefix + account;

    public string? Read(string account)
    {
        if (!CredRead(Target(account), CredTypeGeneric, 0, out var ptr))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error);
        }
        try
        {
            var cred = Marshal.PtrToStructure<Credential>(ptr);
            if (cred.CredentialBlobSize == 0 || cred.CredentialBlob == IntPtr.Zero) return null;
            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, bytes.Length);
            return DecodeBlob(bytes);
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
        var blob = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var cred = new Credential
            {
                Type = CredTypeGeneric,
                TargetName = Target(account),
                Comment = "Laravel Forge API token (Forge for Windows)",
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
        if (!CredDelete(Target(account), CredTypeGeneric, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != ErrorNotFound) throw new Win32Exception(error);
        }
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
