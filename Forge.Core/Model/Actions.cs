using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Forge.Core.Model;

public enum Refetch { None, Org, Sites }

/// <summary>
/// One row of a server's or site's action list. A write declares everything
/// that varies — path, method, body, the 403 wording, what to re-read — beside
/// the label that describes it, so the service needs one code path for all of them.
/// </summary>
public sealed record ActionSpec
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    /// <summary>The key that reaches the same action straight from the list.</summary>
    public string Hint { get; init; } = "";
    public bool Available { get; init; } = true;
    public string Reason { get; init; } = "";

    /// <summary>Writes arm on the first press and are sent by a capital Y.</summary>
    public bool Armable { get; init; }
    /// <summary>The deploy arms on the site's key, so the tree row lights up with it.</summary>
    public bool ArmsSubject { get; init; }
    /// <summary>Folded into the arm key, so a refresh that changes what the row means invalidates the arm.</summary>
    public string ArmIntent { get; init; } = "";
    public string ArmedText { get; init; } = "press Y to confirm";
    public string Done { get; init; } = "Requested";

    public string Method { get; init; } = "POST";
    public string Path { get; init; } = "";
    public JsonObject? Body { get; init; }
    /// <summary>A job with one is reported on its row; a job without one (the deploy) reports on the organization.</summary>
    public string? ScopeMessage { get; init; }
    public Refetch Refetch { get; init; }
    /// <summary>Maintenance flips land a few seconds after the 202; re-read the server until they settle.</summary>
    public bool SettleSites { get; init; }

    /// <summary>For server rows: which service the server type is asked about.</summary>
    public string Service { get; init; } = "";
    /// <summary>For a clear-log write: which log it empties.</summary>
    public string Log { get; init; } = "";
    public string RecipeId { get; init; } = "";
}

public static partial class Actions
{
    public const string ServerScopeMessage =
        "Your token can't manage servers — that needs the server:manage-services scope";

    public static readonly IReadOnlyList<(string Kind, string Label)> SiteLogKinds =
    [
        ("application", "Application log"),
        ("nginx-error", "Nginx error log"),
        ("nginx-access", "Nginx access log"),
    ];

    public static bool IsSiteLogKind(string kind) => SiteLogKinds.Any(k => k.Kind == kind);

    public static string SiteLogLabel(string kind) =>
        SiteLogKinds.FirstOrDefault(k => k.Kind == kind).Label ?? kind;

    public const string LogScopeMessage =
        "Your token can't clear logs — that needs the server:manage-logs scope";

    public const string ProcessScopeMessage =
        "Your token can't manage background processes — that needs the server:create-daemons scope";

    // ---------------------------------------------------------- server logs

    /// <summary>Every key <c>logs/{key}</c> accepts, in the order a list shows them.</summary>
    private static readonly IReadOnlyList<(string Key, string Label)> AllServerLogKinds =
    [
        ("nginx-error", "Nginx error log"),
        ("nginx-access", "Nginx access log"),
        ("php-8.5", "PHP 8.5 log"), ("php-8.4", "PHP 8.4 log"), ("php-8.3", "PHP 8.3 log"),
        ("php-8.2", "PHP 8.2 log"), ("php-8.1", "PHP 8.1 log"), ("php-8.0", "PHP 8.0 log"),
        ("php-7.4", "PHP 7.4 log"), ("php-7.3", "PHP 7.3 log"), ("php-7.2", "PHP 7.2 log"),
        ("php-7.1", "PHP 7.1 log"), ("php-7.0", "PHP 7.0 log"), ("php-5.6", "PHP 5.6 log"),
        ("php-5", "PHP 5 log"),
        ("database-mysql", "MySQL log"),
        ("database-mariadb", "MariaDB log"),
        ("database-postgresql", "Postgres log"),
        ("redis-server", "Redis log"),
        ("meilisearch", "Meilisearch log"),
        ("ssh", "SSH log"),
        ("unattended-upgrades", "Unattended upgrades log"),
    ];

    [GeneratedRegex(@"^php([0-9])([0-9]*)(-old)?$")]
    private static partial Regex PhpPoolParts();

    /// <summary>The server reports <c>php84</c>; the log key is <c>php-8.4</c>.</summary>
    public static string ServerPhpLogKey(Server? server)
    {
        var m = PhpPoolParts().Match(server?.PhpVersion ?? "");
        if (!m.Success) return "";
        var key = m.Groups[2].Value == "" ? "php-" + m.Groups[1].Value : $"php-{m.Groups[1].Value}.{m.Groups[2].Value}";
        return IsServerLogKind(key) ? key : "";
    }

    /// <summary>
    /// Only the logs this server can have: what its type runs, the PHP version
    /// it reports and its database. The rest would answer with an empty file or a 404.
    /// </summary>
    public static IReadOnlyList<(string Key, string Label)> ServerLogKinds(Server? server)
    {
        if (server is null) return [];
        var keep = new HashSet<string>(StringComparer.Ordinal) { "ssh", "unattended-upgrades" };
        if (ServerRuns(server, "nginx")) { keep.Add("nginx-error"); keep.Add("nginx-access"); }
        if (ServerRuns(server, "php") && ServerPhpLogKey(server) is { Length: > 0 } php) keep.Add(php);
        if (ServerRuns(server, "redis")) keep.Add("redis-server");
        if (server.Kind == "meilisearch") keep.Add("meilisearch");
        var db = server.DatabaseType;
        if (ServerRuns(server, "database") && db != "")
        {
            if (db.StartsWith("mysql", StringComparison.Ordinal)) keep.Add("database-mysql");
            else if (db.StartsWith("mariadb", StringComparison.Ordinal)) keep.Add("database-mariadb");
            else if (db.StartsWith("postgres", StringComparison.Ordinal)) keep.Add("database-postgresql");
        }
        return AllServerLogKinds.Where(k => keep.Contains(k.Key)).ToList();
    }

    public static bool IsServerLogKind(string key) => AllServerLogKinds.Any(k => k.Key == key);

    public static string ServerLogLabel(string key) =>
        AllServerLogKinds.FirstOrDefault(k => k.Key == key).Label ?? key;

    // -------------------------------------------------------- log clearing

    /// <summary>"Nginx error log" reads as "the nginx error log" mid-sentence; names written in capitals stay as they are.</summary>
    private static string Lower(string label) =>
        label.Length > 1 && char.IsLower(label[1]) && !label.StartsWith("My", StringComparison.Ordinal)
            && !label.StartsWith("Maria", StringComparison.Ordinal)
            ? char.ToLowerInvariant(label[0]) + label[1..] : label;

    /// <summary>Empties one of a site's three logs. The open log is read again by whoever asked.</summary>
    public static ActionSpec ClearSiteLog(string org, Site? site, string kind)
    {
        var known = IsSiteLogKind(kind);
        var label = SiteLogLabel(kind);
        return new ActionSpec
        {
            Id = "clear-log", Log = kind, Label = "Clear " + Lower(label), Armable = true, ArmIntent = kind,
            Available = site is not null && known, Reason = site is null ? "not listed" : "unknown log",
            ArmedText = "press Y to empty the " + Lower(label), Done = label + " cleared",
            Method = "DELETE", ScopeMessage = LogScopeMessage,
            Path = site is null || !known ? "" : Paths.SiteLog(org, site.ServerId, site.Id, kind),
        };
    }

    public static ActionSpec ClearServerLog(string org, Server? server, string key)
    {
        var known = IsServerLogKind(key);
        var label = ServerLogLabel(key);
        return new ActionSpec
        {
            Id = "clear-log", Log = key, Label = "Clear " + Lower(label), Armable = true, ArmIntent = key,
            Available = server is not null && known, Reason = server is null ? "not listed" : "unknown log",
            ArmedText = "press Y to empty the " + Lower(label), Done = label + " cleared",
            Method = "DELETE", ScopeMessage = LogScopeMessage,
            Path = server is null || !known ? "" : Paths.ServerLog(org, server.Id, key),
        };
    }

    // ------------------------------------------------- background processes

    private static string Short(string command) => command.Length <= 60 ? command : command[..57] + "…";

    /// <summary>
    /// Forge accepts restart, start, stop and empty-log; only restart is offered.
    /// A provisioning or unreachable server answers 4xx, so the row says so first.
    /// </summary>
    public static ActionSpec ProcessRestart(string org, Server? server, BackgroundProcess? process)
    {
        var ready = server?.State == ServerState.Ready;
        var listed = server is not null && process is not null;
        return new ActionSpec
        {
            Id = "process-restart", Label = "Restart process", Armable = true, ArmIntent = process?.Id ?? "",
            Available = listed && ready,
            Reason = !listed ? "not listed" : Status.ServerStateLabel(server!.State),
            ArmedText = "press Y to restart " + Short(process?.Command ?? "the process"),
            Done = "Restart requested", ScopeMessage = ProcessScopeMessage,
            Path = listed ? Paths.BackgroundProcessAction(org, server!.Id, process!.Id) : "",
            Body = Obj(("action", "restart")),
        };
    }

    // ----------------------------------------------------------------- php

    [GeneratedRegex(@"^php[0-9]{1,3}(-old)?$")]
    private static partial Regex PhpPoolWord();

    [GeneratedRegex(@"^PHP ([0-9]+)\.([0-9]+)$")]
    private static partial Regex PhpDisplay();

    public static string PhpPool(string? word) => word is not null && PhpPoolWord().IsMatch(word) ? word : "";

    /// <summary>A site reports "PHP 8.4" where the endpoint wants "php84".</summary>
    public static string SitePhpPool(Site? site)
    {
        var value = (site?.PhpVersion ?? "").Trim();
        var m = PhpDisplay().Match(value);
        return PhpPool(m.Success ? "php" + m.Groups[1].Value + m.Groups[2].Value : value);
    }

    // ------------------------------------------------------------- servers

    /// <summary>
    /// Which services a server runs, by type — the payload cannot say. A type
    /// missing from the table runs everything, so the failure is a row too many.
    /// </summary>
    private static readonly Dictionary<string, string[]> ServerTypeServices = new()
    {
        ["app"] = ["nginx", "php", "supervisor", "redis", "database"],
        ["web"] = ["nginx", "php", "supervisor"],
        ["worker"] = ["php", "supervisor"],
        ["loadbalancer"] = ["nginx"],
        ["database"] = ["database"],
        ["cache"] = ["redis"],
        ["meilisearch"] = [],
        ["openclaw"] = [],
    };

    public static bool ServerRuns(Server? server, string service) =>
        !ServerTypeServices.TryGetValue(server?.Kind ?? "", out var services) || services.Contains(service);

    /// <summary>Forge has two database endpoints; <c>database_type</c> only chooses between them.</summary>
    public static (string Endpoint, string Label)? DatabaseService(Server? server)
    {
        var type = server?.DatabaseType ?? "";
        if (type == "") return null;
        if (type.StartsWith("mysql", StringComparison.Ordinal)) return ("mysql", "MySQL");
        if (type.StartsWith("mariadb", StringComparison.Ordinal)) return ("mysql", "MariaDB");
        if (type.StartsWith("postgres", StringComparison.Ordinal)) return ("postgres", "Postgres");
        return ("", "");
    }

    private static JsonObject Obj(params (string Key, JsonNode? Value)[] pairs)
    {
        var o = new JsonObject();
        foreach (var (k, v) in pairs) o[k] = v;
        return o;
    }

    /// <summary>
    /// Restarts of what the server's type runs, a reboot, and the reads. A
    /// provisioning or revoked server answers 4xx to all of it, so the row says
    /// so instead; reboot stays available when unreachable, as it might fix that.
    /// </summary>
    public static IReadOnlyList<ActionSpec> ServerActions(string org, Server? server)
    {
        var state = server?.State ?? ServerState.Unknown;
        var ready = state == ServerState.Ready;
        var stateReason = server is null ? "not listed" : Status.ServerStateLabel(state);
        var php = PhpPool(server?.PhpVersion);
        var hasPhp = ready && php != "";
        var rebootable = ready || state == ServerState.Unreachable;
        var ssh = ForgeText.SshCommand(server);
        var id = server?.Id ?? "";
        var suffix = php != "" ? $" ({php})" : "";
        var db = DatabaseService(server);
        var dbKnown = db is { Endpoint: not "" };
        var dbName = dbKnown ? db!.Value.Label : "database";
        var dbAvailable = ready && dbKnown;
        var phpReason = hasPhp ? "" : !ready ? stateReason : "no PHP version reported";

        var all = new List<ActionSpec>
        {
            new() { Id = "nginx-restart", Service = "nginx", Label = "Restart nginx",
                Available = ready, Reason = ready ? "" : stateReason, Armable = true,
                ArmedText = "press Y to restart nginx", Done = "nginx restart requested",
                ScopeMessage = ServerScopeMessage, Path = Paths.ServiceAction(org, id, "nginx"),
                Body = Obj(("action", "reboot")) },
            new() { Id = "php-reload", Service = "php", Label = "Reload PHP-FPM" + suffix,
                Available = hasPhp, Reason = phpReason, Armable = true, ArmIntent = php,
                ArmedText = "press Y to reload PHP-FPM", Done = "PHP-FPM reload requested",
                ScopeMessage = ServerScopeMessage, Path = Paths.ServiceAction(org, id, "php"),
                Body = Obj(("action", "reload"), ("version", php)) },
            new() { Id = "php-restart", Service = "php", Label = "Restart PHP-FPM" + suffix,
                Available = hasPhp, Reason = phpReason, Armable = true, ArmIntent = php,
                ArmedText = "press Y to restart PHP-FPM", Done = "PHP-FPM restart requested",
                ScopeMessage = ServerScopeMessage, Path = Paths.ServiceAction(org, id, "php"),
                Body = Obj(("action", "reboot"), ("version", php)) },
            new() { Id = "supervisor-restart", Service = "supervisor", Label = "Restart supervisor",
                Available = ready, Reason = ready ? "" : stateReason, Armable = true,
                ArmedText = "press Y to restart supervisor", Done = "supervisor restart requested",
                ScopeMessage = ServerScopeMessage, Path = Paths.ServiceAction(org, id, "supervisor"),
                Body = Obj(("action", "reboot")) },
            new() { Id = "redis-restart", Service = "redis", Label = "Restart redis",
                Available = ready, Reason = ready ? "" : stateReason, Armable = true,
                ArmedText = "press Y to restart redis", Done = "redis restart requested",
                ScopeMessage = ServerScopeMessage, Path = Paths.ServiceAction(org, id, "redis"),
                Body = Obj(("action", "reboot")) },
            new() { Id = "database-restart", Service = "database", Label = "Restart " + dbName,
                Available = dbAvailable,
                Reason = dbAvailable ? "" : !ready ? stateReason : "unknown database type", Armable = true,
                ArmedText = "press Y to restart " + dbName, Done = dbName + " restart requested",
                ScopeMessage = ServerScopeMessage,
                Path = dbKnown ? Paths.ServiceAction(org, id, db!.Value.Endpoint) : "",
                Body = Obj(("action", "reboot")) },
            new() { Id = "reboot", Label = "Reboot server",
                Available = rebootable, Reason = rebootable ? "" : stateReason, Armable = true,
                ArmedText = "press Y to reboot", Done = "Reboot requested",
                ScopeMessage = ServerScopeMessage, Refetch = Refetch.Org,
                Path = Paths.ServerAction(org, id), Body = Obj(("action", "reboot")) },
            // Each section is one row that opens its list; the list opens on its first item.
            new() { Id = "events", Label = "Events", Hint = "e" },
            new() { Id = "server-logs", Label = "Logs", Available = ServerLogKinds(server).Count > 0, Reason = "not listed" },
            new() { Id = "processes", Label = "Background processes", Available = server is not null, Reason = "not listed" },
            new() { Id = "schedule", Label = "Scheduled jobs", Available = server is not null, Reason = "not listed" },
            new() { Id = "recipes", Label = "Run a recipe…" },
            new() { Id = "monitors", Label = "Monitors" },
            new() { Id = "forge", Label = "Open in Forge", Hint = "f" },
            new() { Id = "ssh", Label = "Copy ssh command", Hint = "s",
                Available = ssh != "", Reason = ssh != "" ? "" : "no public IP" },
            new() { Id = "ssh-terminal", Label = "Open ssh in Windows Terminal", Hint = "t",
                Available = ssh != "", Reason = ssh != "" ? "" : "no public IP" },
        };

        return all.Where(a =>
        {
            if (a.Service == "") return true;
            if (a.Service == "database" && db is null) return false;
            return ServerRuns(server, a.Service);
        }).ToList();
    }

    // --------------------------------------------------------------- sites

    public static IReadOnlyList<ActionSpec> SiteActions(string org, Site? site)
    {
        var deployable = Status.CanDeploy(site);
        var url = ForgeText.ExternalUrl(site?.Url);
        var parked = site?.Maintenance == true;
        var moving = site?.MaintenanceStatus ?? "";
        var pool = SitePhpPool(site);

        var list = new List<ActionSpec>
        {
            new() { Id = "deploy", Label = "Deploy", Hint = "d", Armable = true, ArmsSubject = true,
                ArmedText = "press Y to deploy", Done = "Deployment queued",
                Available = deployable, Reason = deployable ? "" : "no repository",
                Refetch = Refetch.Sites,
                Path = site is null ? "" : Paths.Deploy(org, site.ServerId, site.Id) },
            new() { Id = "maintenance",
                Label = parked ? "Disable maintenance mode" : "Enable maintenance mode",
                Armable = true, ArmIntent = parked ? "off" : "on",
                Available = site is not null && moving == "",
                Reason = site is null ? "not listed" : moving != "" ? moving + " now" : "",
                ArmedText = parked ? "press Y to bring the site back" : "press Y to take the site offline",
                Done = parked ? "Maintenance mode off — requested" : "Maintenance mode on — requested",
                Method = parked ? "DELETE" : "POST",
                ScopeMessage = "Your token can't change maintenance mode — that needs the site:manage-commands scope",
                Refetch = Refetch.Sites, SettleSites = true,
                Path = site is null ? "" : Paths.Maintenance(org, site.ServerId, site.Id),
                Body = parked ? null : Obj(("status", 503)) },
            new() { Id = "site-php-reload", Label = "Reload PHP-FPM" + (pool != "" ? $" ({pool})" : ""),
                Armable = true, ArmIntent = pool, Available = pool != "",
                Reason = site is null ? "not listed" : site.PhpVersion != "" ? "unknown PHP version" : "no PHP version reported",
                ArmedText = "press Y to reload PHP-FPM", Done = "PHP-FPM reload requested",
                ScopeMessage = ServerScopeMessage,
                Path = site is null ? "" : Paths.ServiceAction(org, site.ServerId, "php"),
                Body = Obj(("action", "reload"), ("version", pool)) },
            new() { Id = "command", Label = "Run a command…", Available = site is not null, Reason = "not listed" },
            // Each section is one row that opens its list; the list opens on its first item (the newest deployment).
            new() { Id = "deployments", Label = "Deployments", Available = site is not null, Reason = "not listed" },
            new() { Id = "site-logs", Label = "Logs", Available = site is not null, Reason = "not listed" },
            new() { Id = "site-schedule", Label = "Scheduled jobs", Available = site is not null, Reason = "not listed" },
            // Opens the window's Config tab: the flyout never shows a config file.
            new() { Id = "config", Label = "Config files…", Available = site is not null, Reason = "not listed" },
        };
        list.Add(new() { Id = "heartbeats", Label = "Heartbeats", Available = site is not null, Reason = "not listed" });
        list.Add(new() { Id = "open", Label = "Open site", Hint = "o", Available = url != "", Reason = url != "" ? "" : "no address" });
        list.Add(new() { Id = "forge", Label = "Open in Forge", Hint = "f" });
        list.Add(new() { Id = "ssh", Label = "Copy ssh command", Hint = "s" });
        list.Add(new() { Id = "ssh-terminal", Label = "Open ssh in Windows Terminal", Hint = "t" });
        return list;
    }

    /// <summary>
    /// The row the command prompt arms. The text is trimmed once, here: Forge
    /// stores the command as received and the run is found by exact match.
    /// </summary>
    public static ActionSpec SiteCommand(string org, Site? site, string? command)
    {
        var text = (command ?? "").Trim();
        var ready = site is not null && text != "";
        return new ActionSpec
        {
            Id = "command-run", Label = "Run", Armable = true, ArmIntent = text,
            Available = ready, Reason = site is null ? "not listed" : "type a command first",
            ArmedText = "press Y to run", Done = "Command sent",
            ScopeMessage = "Your token can't run commands — that needs the site:manage-commands scope",
            Path = site is null ? "" : Paths.Commands(org, site.ServerId, site.Id),
            Body = Obj(("command", text)),
        };
    }

    /// <summary>
    /// Replaces one of the site's config files with <paramref name="content"/>, whole. The deploy
    /// script's <c>auto_source</c> is sent back only when Forge reported it, so a save never flips it.
    /// </summary>
    public static ActionSpec SaveConfig(string org, Site? site, ConfigFile file, string content, bool? autoSource = null)
    {
        var info = ConfigFiles.Of(file);
        var body = Obj((info.BodyKey, content));
        if (file == ConfigFile.DeployScript && autoSource is { } auto) body["auto_source"] = auto;
        return new ActionSpec
        {
            Id = "config-save", Label = "Save " + ConfigFiles.Lower(file), Armable = true, ArmIntent = info.Key,
            Available = site is not null, Reason = "not listed",
            ArmedText = "press Y to save the " + ConfigFiles.Lower(file), Done = info.Label + " saved",
            Method = "PUT", ScopeMessage = ConfigFiles.WriteScopeMessage(file),
            Path = site is null ? "" : ConfigFiles.Path(org, site, file),
            Body = body,
        };
    }

    public static ActionSpec RecipeRun(string org, Server? server, Recipe? recipe)
    {
        var ready = server?.State == ServerState.Ready;
        var serverId = long.TryParse(server?.Id, out var n) ? n : (long?)null;
        var addressable = recipe is not null && serverId is not null;
        var available = addressable && ready;
        return new ActionSpec
        {
            Id = "recipe-run", Label = recipe?.Name ?? "", Armable = true,
            Available = available,
            Reason = available ? "" : !addressable ? "not listed" : Status.ServerStateLabel(server!.State),
            ArmedText = "press Y to run on " + (server?.Name ?? "this server"),
            Done = "Recipe run requested",
            ScopeMessage = "Your token can't run recipes — that needs the recipe:manage scope",
            RecipeId = recipe?.Id ?? "",
            Path = addressable ? Paths.RecipeRuns(org, recipe!.Id) : "",
            Body = addressable ? Obj(("servers", new JsonArray(serverId!.Value))) : null,
        };
    }
}
