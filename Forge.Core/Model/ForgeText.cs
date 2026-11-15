using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Forge.Core.Model;

/// <summary>
/// Guards for API strings on their way out of the process — to a browser, a
/// toast, a clipboard, a file name. Port of Model.js's externalUrl, notifyText,
/// plainText, logText and safeFileName. An API string handed to another
/// program is untrusted, even when no shell is involved.
/// </summary>
public static partial class ForgeText
{
    /// <summary>Culture-aware ordering, so <c>admin</c> sorts before <c>Zeta</c> the way the API sorts servers.</summary>
    public static readonly StringComparer Collation = StringComparer.Create(CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);

    public static string Number(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    public static string Pluralize(int count, string singular) => $"{count} {singular}{(count == 1 ? "" : "s")}";

    // -------------------------------------------------------------- address

    [GeneratedRegex(@"^(https?://)([^/?#]+)([/?#][\s\S]*)?$", RegexOptions.IgnoreCase)]
    private static partial Regex UrlParts();

    // What RFC 3986 allows unescaped, less the apostrophe. A `%` not starting
    // an escape triplet is encoded; surrogate pairs match whole.
    [GeneratedRegex(@"[\uD800-\uDBFF][\uDC00-\uDFFF]|%(?![0-9A-Fa-f]{2})|[^A-Za-z0-9\-._~:/?#\[\]@!$&()*+,;=%]")]
    private static partial Regex UrlUnsafe();

    /// <summary>
    /// Every address that leaves the app passes through here: absolute
    /// http(s) only, no userinfo, everything else percent-encoded. Returns ""
    /// for anything it will not vouch for.
    /// </summary>
    public static string ExternalUrl(string? value)
    {
        var m = UrlParts().Match((value ?? "").Trim());
        if (!m.Success || m.Groups[2].Value.Contains('@')) return "";
        return m.Groups[1].Value.ToLowerInvariant() + EncodeUrlChars(m.Groups[2].Value) +
               EncodeUrlChars(m.Groups[3].Success ? m.Groups[3].Value : "");
    }

    private static string EncodeUrlChars(string text) => UrlUnsafe().Replace(text, c =>
    {
        if (c.Value == "'") return "%27";
        if (c.Value.Length == 1 && char.IsSurrogate(c.Value[0])) return "";
        return Uri.EscapeDataString(c.Value);
    });

    // ------------------------------------------------------------- dashboard

    public const string DefaultDashboardTemplate = "https://forge.laravel.com/{org}/{server}/{site}";

    [GeneratedRegex(@"[?&][^/?&=]*=$")]
    private static partial Regex StrandedParam();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex Placeholder();

    /// <summary>
    /// The API never hands out a dashboard link, so it is a template the user
    /// can correct. <c>{server}</c> is the slug, which does not follow a rename.
    /// </summary>
    public static string DashboardUrl(string template, string org, Server server, string? siteId)
    {
        var url = template;
        if (!string.IsNullOrEmpty(siteId))
        {
            url = url.Replace("{site}", Uri.EscapeDataString(siteId));
        }
        else
        {
            url = url.Replace("{site}", "");
            url = StrandedParam().Replace(url, "");
            url = url.TrimEnd('?', '&').TrimEnd('/');
        }
        url = url.Replace("{org}", Uri.EscapeDataString(org));
        if (server.Id != "") url = url.Replace("{serverId}", Uri.EscapeDataString(server.Id));
        if (server.Slug != "") url = url.Replace("{server}", Uri.EscapeDataString(server.Slug));
        return Placeholder().IsMatch(url) ? "" : ExternalUrl(url);
    }

    public static string OrgDashboardUrl(string org) => "https://forge.laravel.com/" + Uri.EscapeDataString(org);

    // ------------------------------------------------------------------ ssh

    [GeneratedRegex(@"^[A-Za-z0-9.\-:]+$")]
    private static partial Regex AddressChars();

    /// <summary>Ends up on the clipboard or in a terminal's argv, so an address with anything but address characters is refused.</summary>
    public static bool IsSafeHost(string? ip) => !string.IsNullOrEmpty(ip) && AddressChars().IsMatch(ip);

    public static string SshCommand(Server? server)
    {
        if (server is null || !IsSafeHost(server.Ip)) return "";
        var port = server.SshPort != 22 ? " -p " + server.SshPort.ToString(CultureInfo.InvariantCulture) : "";
        return "ssh forge@" + server.Ip + port;
    }

    // ----------------------------------------------------------------- text

    [GeneratedRegex(@"[\u0000-\u001F\u007F]")]
    private static partial Regex Controls();

    [GeneratedRegex(@"^[\s-]+")]
    private static partial Regex LeadingHyphens();

    /// <summary>For one-line strings that reach a toast or a tooltip.</summary>
    public static string PlainText(string? value) => Controls().Replace(value ?? "", " ");

    /// <summary>
    /// For strings handed to another program: leading hyphens go (an option
    /// parser would read them as flags), and the controls with them.
    /// </summary>
    public static string NotifyText(string? value) =>
        LeadingHyphens().Replace(PlainText(value), "").TrimEnd();

    // ------------------------------------------------------------------ logs

    [GeneratedRegex(@"\u001B(?:\[[0-?]*[ -/]*[@-~]|\][\s\S]*?(?:\u0007|\u001B\\)|[ -/]*[0-~])")]
    private static partial Regex Ansi();

    [GeneratedRegex(@"(?m)^[^\n]*\r")]
    private static partial Regex CarriageFrames();

    [GeneratedRegex(@"[\u0000-\u0008\u000B-\u001F\u007F]")]
    private static partial Regex LogControls();

    public const int LogCharCap = 512 * 1024;
    public const int LogLineCap = 5000;

    /// <summary>
    /// A log is remote text. ANSI goes, a bare <c>\r</c> resolves to the last
    /// frame of its line the way a terminal shows it, and the tail is kept —
    /// the answer is always at the end.
    /// </summary>
    public static string LogText(string? value)
    {
        var text = value ?? "";
        if (text.Length > LogCharCap) text = text[^LogCharCap..];
        text = Ansi().Replace(text, "");
        text = text.Replace("\r\n", "\n");
        text = CarriageFrames().Replace(text, "");
        return LogControls().Replace(text, "");
    }

    public static IReadOnlyList<string> LogLines(string? value)
    {
        var lines = LogText(value).Split('\n').ToList();
        if (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        if (lines.Count > LogLineCap) lines = lines.GetRange(lines.Count - LogLineCap, LogLineCap);
        return lines;
    }

    /// <summary>Forge answers an empty site log with one line saying so; that line is not the log.</summary>
    public const string EmptyLogSentinel = "=== Empty log file ===";

    public static string SiteLogContent(string? value) =>
        (value ?? "").Trim() == EmptyLogSentinel ? "" : value ?? "";

    // ------------------------------------------------------------ file names

    [GeneratedRegex(@"[^A-Za-z0-9._-]+")]
    private static partial Regex NotNameChars();

    /// <summary>API data in a path: keep the set that is unambiguously a name and hyphenate the rest.</summary>
    public static string SafeFileName(string? value)
    {
        var name = NotNameChars().Replace(value ?? "", "-").TrimStart('.', '-').TrimEnd('.', '-');
        if (name.Length > 80) name = name[..80];
        return name == "" ? "site" : name;
    }

    // ------------------------------------------------------------------ time

    public static string RelativeTime(string? iso, long nowMs)
    {
        if (string.IsNullOrEmpty(iso)) return "";
        var then = Json.ParseIsoMs(iso);
        return then is null ? "" : RelativeMs(then.Value, nowMs);
    }

    public static string RelativeMs(long thenMs, long nowMs)
    {
        if (thenMs <= 0) return "";
        var seconds = Math.Max(0, (long)Math.Round((nowMs - thenMs) / 1000.0));
        if (seconds < 60) return "just now";
        var minutes = (long)Math.Round(seconds / 60.0);
        if (minutes < 60) return $"{minutes}m ago";
        var hours = (long)Math.Round(minutes / 60.0);
        if (hours < 24) return $"{hours}h ago";
        var days = (long)Math.Round(hours / 24.0);
        if (days < 30) return $"{days}d ago";
        return $"{(long)Math.Round(days / 30.0)}mo ago";
    }

    /// <summary>Account names: lower-case words joined by hyphens.</summary>
    public static string Slugify(string? value)
    {
        var sb = new StringBuilder();
        foreach (var c in (value ?? "").ToLowerInvariant())
            sb.Append(c is >= 'a' and <= 'z' or >= '0' and <= '9' ? c : '-');
        var slug = Regex.Replace(sb.ToString(), "-+", "-").Trim('-');
        return slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }
}
