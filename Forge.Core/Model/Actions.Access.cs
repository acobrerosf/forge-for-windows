using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Forge.Core.Model;

/// <summary>Who may reach the server: SSH keys and firewall rules.</summary>
public static partial class Actions
{
    // Write scopes as the token page names them. Adding and removing a key are separate scopes.
    public const string SshKeyCreateScopeMessage =
        "Your token can't add SSH keys — that needs the server:create-keys scope";
    public const string SshKeyDeleteScopeMessage =
        "Your token can't remove SSH keys — that needs the server:delete-keys scope";
    public const string SshKeyReadScopeMessage =
        "Your token can't read SSH keys — that needs the server:view scope";
    public const string FirewallScopeMessage =
        "Your token can't change the firewall — that needs the server:manage-network scope";
    public const string FirewallReadScopeMessage =
        "Your token can't read the firewall — that needs the server:view scope";
    public const string CertificateReadScopeMessage =
        "Your token can't read certificates — that needs the site:manage-ssl scope";

    /// <summary>The POST field that names the server user the key signs in as.</summary>
    private const string SshKeyUserField = "username";

    // ------------------------------------------------------------- SSH keys

    /// <summary>
    /// The key types OpenSSH writes to a <c>.pub</c> file and a server accepts, each with the number
    /// of length-prefixed fields its blob holds after the type name: ed25519 its 32-byte key, RSA e
    /// and n, ECDSA the curve and the point, and a security key's types the application as well.
    /// </summary>
    private static readonly Dictionary<string, int> SshKeyFields = new(StringComparer.Ordinal)
    {
        ["ssh-ed25519"] = 1, ["ssh-rsa"] = 2,
        ["ecdsa-sha2-nistp256"] = 2, ["ecdsa-sha2-nistp384"] = 2, ["ecdsa-sha2-nistp521"] = 2,
        ["sk-ssh-ed25519@openssh.com"] = 2, ["sk-ecdsa-sha2-nistp256@openssh.com"] = 3,
    };

    /// <summary>
    /// "" when <paramref name="text"/> is one OpenSSH public key, otherwise why not. The key's
    /// blob has to decode, name the same type as its prefix and hold that type's key fields, so a
    /// truncated paste is caught here rather than installed broken. Private key text is refused outright.
    /// </summary>
    public static string SshKeyProblem(string? text)
    {
        var value = (text ?? "").Trim();
        if (value == "") return "Paste a public key first";
        if (value.Contains("PRIVATE KEY", StringComparison.OrdinalIgnoreCase))
            return "That is a private key. Only the public key (the .pub file) goes to the server";
        if (value.Contains('\n') || value.Contains('\r')) return "Paste one key, on one line";
        var parts = SshKeyParts(value);
        if (parts.Length < 2 || !SshKeyFields.TryGetValue(parts[0], out var expected)) return "That isn't an OpenSSH public key";
        const string damaged = "The key is damaged or cut short";
        var blob = new byte[parts[1].Length];
        if (!Convert.TryFromBase64String(parts[1], blob, out var length)) return damaged;
        if (SshBlobFields(blob.AsSpan(0, length)) is not { } fields || fields.Count != expected + 1) return damaged;
        var (nameAt, nameLength) = fields[0];
        if (Encoding.ASCII.GetString(blob, nameAt, nameLength) != parts[0]) return damaged;
        if (parts[0].Contains("ed25519", StringComparison.Ordinal) && fields[1].Length != 32) return damaged;
        return "";
    }

    /// <summary>
    /// Where each <c>uint32 length + bytes</c> field of a key blob starts and how long it is, or null when
    /// a field is empty, runs past the end, or the fields don't use up the blob exactly.
    /// </summary>
    private static List<(int At, int Length)>? SshBlobFields(ReadOnlySpan<byte> blob)
    {
        var fields = new List<(int, int)>();
        var at = 0;
        while (at < blob.Length)
        {
            if (blob.Length - at < 4) return null;
            var length = BinaryPrimitives.ReadUInt32BigEndian(blob[at..]);
            at += 4;
            if (length == 0 || length > (uint)(blob.Length - at)) return null;
            fields.Add((at, (int)length));
            at += (int)length;
        }
        return fields.Count == 0 ? null : fields;
    }

    /// <summary>The key's comment (usually user@machine), or "" when it has none.</summary>
    public static string SshKeyComment(string? key)
    {
        var parts = SshKeyParts(key);
        return parts.Length == 3 ? parts[2].Trim() : "";
    }

    /// <summary>A key as a list or a dialog shows it: its type and the end of its blob.</summary>
    public static string SshKeyShort(string? key)
    {
        var parts = SshKeyParts(key);
        if (parts.Length < 2) return "";
        var blob = parts[1].TrimEnd('=');
        return parts[0] + " …" + (blob.Length > 12 ? blob[^12..] : blob);
    }

    /// <summary>A key line's type, blob and comment (the comment may hold spaces).</summary>
    private static string[] SshKeyParts(string? key) =>
        (key ?? "").Trim().Split([' ', '\t'], 3, StringSplitOptions.RemoveEmptyEntries);

    /// <summary>Linux user names as Forge creates them (forge, an isolated site's user, root).</summary>
    public static string SshUserProblem(string? user)
    {
        var value = (user ?? "").Trim();
        if (value == "") return "Name the server user the key signs in as";
        if (value.Length > 32 || !value.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-') || value[0] is '-' or >= '0' and <= '9')
            return "That isn't a server user name";
        return "";
    }

    public static ActionSpec AddSshKey(string org, Server? server, string? name, string? key, string? user)
    {
        var label = (name ?? "").Trim();
        var value = (key ?? "").Trim();
        var account = (user ?? "").Trim();
        var problem = label == "" ? "Give the key a name" : SshKeyProblem(value) is { Length: > 0 } k ? k : SshUserProblem(account);
        var shortKey = SshKeyShort(value);
        var comment = SshKeyComment(value);
        return new ActionSpec
        {
            Id = "ssh-key-add", Label = "Add SSH key", Armable = true,
            ArmIntent = account + "|" + label + "|" + shortKey,
            Available = server is not null && problem == "", Reason = server is null ? "not listed" : problem,
            ArmedText = "press Y to add the key", Done = "SSH key added — Forge is installing it",
            ScopeMessage = SshKeyCreateScopeMessage,
            Path = server is null ? "" : Paths.SshKeyCreate(org, server.Id),
            Body = Obj(("name", label), ("key", value), (SshKeyUserField, account)),
            Wording = ($"Add this key to {ServerName(server)}?",
                $"Whoever holds the private key for {shortKey}{(comment != "" ? $" ({comment})" : "")} " +
                $"can sign in to {ServerName(server)} as {account}.", "Add key"),
        };
    }

    public static ActionSpec DeleteSshKey(string org, Server? server, SshKey? key)
    {
        var listed = server is not null && key is not null;
        return new ActionSpec
        {
            Id = "ssh-key-delete", Label = "Delete SSH key", Armable = true, ArmIntent = key?.Id ?? "",
            Available = listed, Reason = "not listed",
            ArmedText = "press Y to remove the key", Done = "SSH key removal requested",
            Method = "DELETE", ScopeMessage = SshKeyDeleteScopeMessage,
            Path = listed ? Paths.SshKey(org, server!.Id, key!.Id) : "",
            Wording = ($"Remove the key \"{key?.Name}\"?",
                $"It can no longer sign in to {ServerName(server)}{(key?.User is { Length: > 0 } u ? " as " + u : "")}. " +
                "If it is the key you sign in with, you will need another key or the Forge website to get back in.", "Remove"),
        };
    }

    // ------------------------------------------------------- firewall rules

    /// <summary>"" when <paramref name="port"/> is one port or a <c>from:to</c> range, otherwise why not.</summary>
    public static string FirewallPortProblem(string? port)
    {
        var value = (port ?? "").Trim();
        if (value == "") return "Type a port";
        var ends = value.Split(':');
        if (ends.Length > 2) return "Use one port, or a range like 8000:8010";
        var numbers = new List<int>();
        foreach (var end in ends)
        {
            if (!TryPort(end, out var n) || n is < 1 or > 65535)
                return "A port is a number from 1 to 65535";
            numbers.Add(n);
        }
        if (numbers.Count == 2 && numbers[0] >= numbers[1]) return "A range goes from the lower port to the higher one";
        return "";
    }

    /// <summary>Plain digits only: no sign, spaces or separators.</summary>
    private static bool TryPort(string s, out int n) =>
        int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out n);

    /// <summary>The comma-separated entries of a rule's port or address field, trimmed, blanks dropped.</summary>
    private static string[] CommaList(string? s) =>
        (s ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>The addresses, one by one: blank means any address.</summary>
    public static IReadOnlyList<string> FirewallAddresses(string? ip) => CommaList(ip);

    /// <summary>"" when every comma-separated entry is an IP address or a CIDR range (blank is any address).</summary>
    public static string FirewallAddressProblem(string? ip)
    {
        foreach (var entry in FirewallAddresses(ip))
            if (!IsAddress(entry) && !IsRange(entry))
                return $"\"{entry}\" isn't an IP address or a range like 203.0.113.0/24";
        return "";
    }

    /// <summary>
    /// An address written out in full. <see cref="IPAddress.TryParse(string, out IPAddress)"/> also takes
    /// shorthand ("22", "192.168.1", "0x7f000001") that ufw reads differently or not at all, so IPv4 must be
    /// four plain dotted numbers that read back the same, and IPv6 may not carry a %zone.
    /// </summary>
    private static bool IsAddress(string s)
    {
        if (!s.Contains(':'))
        {
            var parts = s.Split('.');
            return parts.Length == 4
                && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit))
                && IPAddress.TryParse(s, out var v4) && v4.ToString() == s;
        }
        return !s.Contains('%') && IPAddress.TryParse(s, out var v6) && v6.AddressFamily == AddressFamily.InterNetworkV6;
    }

    /// <summary>A CIDR range whose address part is written out in full and whose prefix is a plain number.</summary>
    private static bool IsRange(string s)
    {
        var slash = s.IndexOf('/');
        if (slash < 0) return false;
        var prefix = s[(slash + 1)..];
        return IsAddress(s[..slash]) && prefix.Length is > 0 and <= 3 && prefix.All(char.IsAsciiDigit)
            && IPNetwork.TryParse(s, out _);
    }

    /// <summary>"any address" for a blank rule, otherwise the addresses as typed, comma-separated.</summary>
    public static string FirewallAddressText(string? ip) =>
        FirewallAddresses(ip) is { Count: > 0 } list ? string.Join(", ", list) : "any address";

    public static ActionSpec AddFirewallRule(string org, Server? server, string? name, string? port, string? ip, bool allow)
    {
        var label = (name ?? "").Trim();
        var value = (port ?? "").Trim();
        var addresses = string.Join(",", FirewallAddresses(ip));
        var problem = label == "" ? "Give the rule a name"
            : FirewallPortProblem(value) is { Length: > 0 } p ? p
            : FirewallAddressProblem(addresses);
        var type = allow ? "allow" : "deny";
        var body = Obj(("name", label), ("port", value), ("type", type));
        if (addresses != "") body["ip_address"] = addresses;
        return new ActionSpec
        {
            Id = "firewall-add", Label = "Add firewall rule", Armable = true,
            ArmIntent = $"{type}|{value}|{addresses}|{label}",
            Available = server is not null && problem == "", Reason = server is null ? "not listed" : problem,
            ArmedText = "press Y to add the rule", Done = "Firewall rule added — Forge is applying it",
            ScopeMessage = FirewallScopeMessage,
            Path = server is null ? "" : Paths.FirewallRuleCreate(org, server.Id),
            Body = body,
            Wording = FirewallAddWording(server, value, addresses, allow),
        };
    }

    public static ActionSpec DeleteFirewallRule(string org, Server? server, FirewallRule? rule)
    {
        var listed = server is not null && rule is not null;
        return new ActionSpec
        {
            Id = "firewall-delete", Label = "Delete firewall rule", Armable = true, ArmIntent = rule?.Id ?? "",
            Available = listed, Reason = "not listed",
            ArmedText = "press Y to remove the rule", Done = "Firewall rule removal requested",
            Method = "DELETE", ScopeMessage = FirewallScopeMessage,
            Path = listed ? Paths.FirewallRule(org, server!.Id, rule!.Id) : "",
            Wording = FirewallDeleteWording(server, rule),
        };
    }

    private static string ServerName(Server? server) => server?.Name ?? "this server";

    /// <summary>
    /// "port 22", "ports 8000–8010" for a range, "ports 22, 80" for a list, or "all ports" for a rule with
    /// none: as the list and the confirm dialog both say it.
    /// </summary>
    public static string PortText(string? port)
    {
        var parts = FirewallPortParts(port);
        if (parts.Count == 0) return "all ports";
        var text = string.Join(", ", parts.Select(p => p.Replace(":", "–")));
        return parts.Count > 1 || parts[0].Contains(':') ? "ports " + text : "port " + text;
    }

    /// <summary>A listed rule's ports, one by one: Forge may report a comma-separated list, or none for every port.</summary>
    private static IReadOnlyList<string> FirewallPortParts(string? port) => CommaList(port);

    private static (string, string, string) FirewallAddWording(Server? server, string port, string addresses, bool allow)
    {
        var where = FirewallAddressText(addresses);
        var ports = PortText(port);
        if (allow)
            return ($"Allow {ports} from {where}?", $"{ServerName(server)} starts accepting connections on {ports} from {where}.", "Allow");
        var body = $"{ServerName(server)} refuses connections on {ports} from {where}. A deny rule wins over any allow rule.";
        if (FirewallCoversSsh(port, server))
            body += $" ssh listens on port {server?.SshPort ?? 22}: if you sign in from {where}, you will be locked out.";
        return ($"Block {ports} from {where}?", body, "Block");
    }

    private static (string, string, string) FirewallDeleteWording(Server? server, FirewallRule? rule)
    {
        if (rule is null) return ("Delete this firewall rule?", "", "Delete");
        var where = FirewallAddressText(rule.IpAddress);
        var ports = PortText(rule.Port);
        if (rule.Type == "deny")
            return ($"Delete the rule that blocks {ports} from {where}?",
                $"{ServerName(server)} stops refusing connections on {ports} from {where}, if another rule allows them.", "Delete");
        var body = $"{ServerName(server)} stops accepting connections on {ports} from {where}, unless another rule allows them.";
        if (FirewallCoversSsh(rule, server))
            body += $" This is how ssh reaches the server (port {server?.SshPort ?? 22}). If it is the rule that lets you in, " +
                    "you will be locked out and need the Forge website to get back in.";
        return ($"Delete the rule that allows {ports} from {where}?", body, "Delete");
    }

    /// <summary>True when the rule covers the port the server's ssh listens on: removing it can lock the reader out.</summary>
    public static bool FirewallCoversSsh(FirewallRule? rule, Server? server) =>
        rule is not null && FirewallCoversSsh(rule.Port, server);

    /// <summary>
    /// True when <paramref name="port"/> (one port, a <c>from:to</c> range, or a comma-separated list of
    /// either) takes in the server's ssh port. A blank port is a rule for every port, and a part that can't
    /// be read counts as covering ssh: a needless warning is better than a missing one.
    /// </summary>
    public static bool FirewallCoversSsh(string? port, Server? server)
    {
        var ssh = server?.SshPort ?? 22;
        var parts = FirewallPortParts(port);
        if (parts.Count == 0) return true;
        foreach (var part in parts)
        {
            var ends = part.Split(':');
            if (ends.Length > 2 || !TryPort(ends[0], out var from)) return true;
            var to = from;
            if (ends.Length == 2 && !TryPort(ends[1], out to)) return true;
            if (ssh >= from && ssh <= to) return true;
        }
        return false;
    }
}
