using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Forge.Core.Model;

/// <summary>JSON:API → flat records. Port of the <c>*From</c> functions in omarchy-forge's Model.js.</summary>
public static partial class Parse
{
    // ------------------------------------------------------------- servers

    /// <summary>
    /// Readiness is spread across three fields that can disagree. Known
    /// connection failures are matched rather than known successes, so a value
    /// nobody has seen yet reads as healthy instead of lighting up the tray.
    /// </summary>
    public static ServerState ServerStateOf(JsonNode? attributes)
    {
        if (attributes is null) return ServerState.Unknown;
        if (Json.IsTrue(Json.Get(attributes, "revoked"))) return ServerState.Revoked;
        if (!Json.IsTrue(Json.Get(attributes, "is_ready"))) return ServerState.Provisioning;
        var connection = Json.Str(Json.Get(attributes, "connection_status")).ToLowerInvariant();
        if (connection == "") return ServerState.Ready;
        if (ConnectionFailed().IsMatch(connection)) return ServerState.Unreachable;
        if (ConnectionPending().IsMatch(connection)) return ServerState.Provisioning;
        return ServerState.Ready;
    }

    [GeneratedRegex("fail|error|refus|timeout|timed.?out|unreachable|denied|disconnect|lost")]
    private static partial Regex ConnectionFailed();

    [GeneratedRegex("pending|connecting|checking|queued")]
    private static partial Regex ConnectionPending();

    public static List<Server> Servers(JsonNode? body)
    {
        var list = new List<Server>();
        foreach (var r in Json.Items(body, "data"))
        {
            var a = Json.Get(r, "attributes");
            var port = (int)Json.Num(Json.Get(a, "ssh_port"));
            list.Add(new Server
            {
                Id = Json.Str(Json.Get(r, "id")),
                Name = Json.Str(Json.Get(a, "name"), "server"),
                Slug = Json.Str(Json.Get(a, "slug")),
                Kind = Json.Str(Json.Get(a, "type")),
                Provider = Json.Str(Json.Get(a, "provider")),
                Region = Json.Str(Json.Get(a, "region")),
                Ip = Json.Str(Json.Get(a, "ip_address")),
                SshPort = port > 0 ? port : 22,
                PhpVersion = Json.Str(Json.Get(a, "php_version")),
                DatabaseType = Json.Str(Json.Get(a, "database_type")),
                State = ServerStateOf(a),
            });
        }
        return list;
    }

    // --------------------------------------------------------------- sites

    private static Dictionary<string, JsonNode> IncludedIndex(JsonNode? body)
    {
        var index = new Dictionary<string, JsonNode>(StringComparer.Ordinal);
        foreach (var r in Json.Items(body, "included"))
        {
            var type = Json.Str(Json.Get(r, "type"));
            if (type == "") continue;
            index[type + ":" + Json.Str(Json.Get(r, "id"))] = r;
        }
        return index;
    }

    private static JsonNode? Related(JsonNode resource, string name, Dictionary<string, JsonNode> index)
    {
        var identifier = Json.Get(resource, "relationships", name, "data");
        var type = Json.Str(Json.Get(identifier, "type"));
        if (type == "") return null;
        return index.GetValueOrDefault(type + ":" + Json.Str(Json.Get(identifier, "id")));
    }

    /// <summary>
    /// Which server a site belongs to, read off its relationship linkage. Only
    /// <c>include=server</c> puts that linkage there at all.
    /// </summary>
    public static string ServerIdOf(JsonNode? resource) =>
        Json.Str(Json.Get(resource, "relationships", "server", "data", "id"));

    public static List<Site> Sites(JsonNode? body)
    {
        var list = new List<Site>();
        var index = IncludedIndex(body);
        foreach (var r in Json.Items(body, "data"))
        {
            var serverId = ServerIdOf(r);
            if (serverId == "") continue;

            var a = Json.Get(r, "attributes");
            var repository = Json.Get(a, "repository");
            var deployment = Related(r, "latestDeployment", index);
            var d = deployment is null ? null : Json.Get(deployment, "attributes");
            var commit = Json.Get(d, "commit");
            var maintenance = Json.Get(a, "maintenance_mode");

            // The live status beats the last record: a running deploy has not produced one yet.
            var status = Json.Str(Json.Get(a, "deployment_status"));
            if (status == "" && d is not null) status = Json.Str(Json.Get(d, "status"));

            var hash = Json.Str(Json.Get(commit, "hash"));
            var deployedAt = Json.Str(Json.Get(d, "ended_at"));
            if (deployedAt == "") deployedAt = Json.Str(Json.Get(d, "started_at"));

            list.Add(new Site
            {
                Id = Json.Str(Json.Get(r, "id")),
                ServerId = serverId,
                Name = Json.Str(Json.Get(a, "name"), "site"),
                Url = Json.Str(Json.Get(a, "url")),
                Https = Json.IsTrue(Json.Get(a, "https")),
                SiteStatus = Json.Str(Json.Get(a, "status")),
                DeploymentStatus = status,
                DeploymentId = deployment is null ? "" : Json.Str(Json.Get(deployment, "id")),
                Branch = Json.Str(Json.Get(repository, "branch")),
                RepoUrl = Json.Str(Json.Get(repository, "url")),
                QuickDeploy = Json.IsTrue(Json.Get(a, "quick_deploy")),
                DeployedAt = deployedAt,
                CommitHash = hash.Length > 7 ? hash[..7] : hash,
                CommitMessage = Json.Str(Json.Get(commit, "message")).Split('\n')[0],
                PhpVersion = Json.Str(Json.Get(a, "php_version")),
                AppType = Json.Str(Json.Get(a, "app_type")),
                Isolated = Json.IsTrue(Json.Get(a, "isolated")),
                ZeroDowntime = Json.IsTrue(Json.Get(a, "zero_downtime_deployments")),
                UsesEnvoyer = Json.IsTrue(Json.Get(a, "uses_envoyer")),
                Wildcards = Json.IsTrue(Json.Get(a, "wildcards")),
                DeploymentRetention = (int)Json.Num(Json.Get(a, "deployment_retention")),
                HealthcheckUrl = Json.Str(Json.Get(a, "healthcheck_url")),
                Aliases = Json.Get(a, "aliases") is JsonArray aliases
                    ? aliases.Select(Json.Str).Where(s => s != "").ToList()
                    : [],
                Maintenance = Json.IsTrue(Json.Get(maintenance, "enabled")),
                MaintenanceStatus = Json.Str(Json.Get(maintenance, "status")),
            });
        }
        return list;
    }

    /// <summary>
    /// A page whose rows all lack the server linkage is not an empty
    /// organization: it is the API ignoring <c>include=server</c>, or a token
    /// that can see sites but not servers. The caller reports it as an error.
    /// </summary>
    public static bool SitesLinkageMissing(JsonNode? body, IReadOnlyCollection<Site> sites) =>
        Json.Items(body, "data").Any() && sites.Count == 0;

    // -------------------------------------------------------------- others

    public static List<ForgeEvent> Events(JsonNode? body)
    {
        var index = IncludedIndex(body);
        return Json.Items(body, "data")
            .Select(r =>
            {
                var a = Json.Get(r, "attributes");
                var site = Related(r, "site", index);
                return new ForgeEvent(
                    Json.Str(Json.Get(r, "id")),
                    Json.Str(Json.Get(a, "description"), "event"),
                    Json.Str(Json.Get(a, "ran_as")),
                    Json.Str(Json.Get(site, "attributes", "name")),
                    Json.Str(Json.Get(a, "created_at")));
            })
            .OrderByDescending(e => Json.ParseIsoMs(e.CreatedAt) ?? 0)
            .ToList();
    }

    /// <summary>The script itself is not kept — only its first line, which is what a row shows.</summary>
    public static List<Recipe> Recipes(JsonNode? body) =>
        Json.Items(body, "data")
            .Select(r =>
            {
                var a = Json.Get(r, "attributes");
                return new Recipe(
                    Json.Str(Json.Get(r, "id")),
                    Json.Str(Json.Get(a, "name"), "recipe"),
                    Json.Str(Json.Get(a, "user")),
                    Json.Str(Json.Get(a, "script")).Split('\n')[0].Trim(),
                    Json.Str(Json.Get(a, "updated_at")));
            })
            .OrderBy(r => r.Name, ForgeText.Collation)
            .ToList();

    public static List<ServerMonitor> Monitors(JsonNode? body) =>
        Json.Items(body, "data")
            .Select(r =>
            {
                var a = Json.Get(r, "attributes");
                return new ServerMonitor
                {
                    Id = Json.Str(Json.Get(r, "id")),
                    Type = Json.Str(Json.Get(a, "type")),
                    Operator = Json.Str(Json.Get(a, "operator")),
                    Threshold = Json.Num(Json.Get(a, "threshold")),
                    Minutes = (int)Json.Num(Json.Get(a, "minutes")),
                    Notify = Json.Str(Json.Get(a, "notify")),
                    Status = Json.Str(Json.Get(a, "status")),
                    State = Json.Str(Json.Get(a, "state")),
                    StateChangedAt = Json.Str(Json.Get(a, "state_changed_at")),
                };
            })
            .OrderBy(Status.MonitorRank)
            .ThenBy(Status.MonitorLabel, ForgeText.Collation)
            .ToList();

    public static List<Heartbeat> Heartbeats(JsonNode? body) =>
        Json.Items(body, "data")
            .Select(r =>
            {
                var a = Json.Get(r, "attributes");
                return new Heartbeat
                {
                    Id = Json.Str(Json.Get(r, "id")),
                    Name = Json.Str(Json.Get(a, "name"), "heartbeat"),
                    Status = Json.Str(Json.Get(a, "status")),
                    GracePeriod = (int)Json.Num(Json.Get(a, "grace_period")),
                    Frequency = (int)Json.Num(Json.Get(a, "frequency")),
                    CustomFrequency = Json.Str(Json.Get(a, "custom_frequency")),
                };
            })
            .OrderBy(Status.HeartbeatRank)
            .ThenBy(h => h.Name, ForgeText.Collation)
            .ToList();

    /// <summary>Kept in Forge's order (newest first, as asked for); a row with no id cannot open its log and is dropped.</summary>
    public static List<Deployment> Deployments(JsonNode? body) =>
        Json.Items(body, "data")
            .Select(r =>
            {
                var a = Json.Get(r, "attributes");
                var commit = Json.Get(a, "commit");
                var hash = Json.Str(Json.Get(commit, "hash"));
                return new Deployment
                {
                    Id = Json.Str(Json.Get(r, "id")),
                    Status = Json.Str(Json.Get(a, "status")),
                    Type = Json.Str(Json.Get(a, "type")),
                    CommitHash = hash.Length > 7 ? hash[..7] : hash,
                    CommitMessage = Json.Str(Json.Get(commit, "message")).Split('\n')[0].Trim(),
                    CommitAuthor = Json.Str(Json.Get(commit, "author")),
                    Branch = Json.Str(Json.Get(commit, "branch")),
                    StartedAt = Json.Str(Json.Get(a, "started_at")),
                    EndedAt = Json.Str(Json.Get(a, "ended_at")),
                    CreatedAt = Json.Str(Json.Get(a, "created_at")),
                };
            })
            .Where(d => d.Id != "")
            .ToList();

    /// <summary>Oldest first, as Forge lists them; a row with no id cannot be deleted and is dropped.</summary>
    public static List<Webhook> Webhooks(JsonNode? body) =>
        Json.Items(body, "data")
            .Select(r =>
            {
                var a = Json.Get(r, "attributes");
                return new Webhook(Json.Str(Json.Get(r, "id")), Json.Str(Json.Get(a, "url")), Json.Str(Json.Get(a, "created_at")));
            })
            .Where(w => w.Id != "" && w.Url != "")
            .ToList();

    /// <summary>The site's public deploy key: "" when the site has none (Forge answers <c>key: null</c>), null when the answer is malformed.</summary>
    public static string? DeployKey(JsonNode? body) =>
        Attributes(body) is JsonObject attrs && attrs.ContainsKey("key") ? ResourceText(body, "key") ?? "" : null;

    // -------------------------------------------------------------- access

    /// <summary>As Forge lists them; a row with no id cannot be deleted and is dropped.</summary>
    public static List<SshKey> SshKeys(JsonNode? body) =>
        Json.Items(body, "data")
            .Select(r =>
            {
                var a = Json.Get(r, "attributes");
                return new SshKey(Json.Str(Json.Get(r, "id")), Json.Str(Json.Get(a, "name")), Json.Str(Json.Get(a, "user")),
                    Json.Str(Json.Get(a, "status")), Json.Str(Json.Get(a, "created_by")), Json.Str(Json.Get(a, "created_at")));
            })
            .Where(k => k.Id != "")
            .ToList();

    /// <summary>Ordered by port, as a reader looks them up; a row with no id is dropped.</summary>
    public static List<FirewallRule> FirewallRules(JsonNode? body) =>
        Json.Items(body, "data")
            .Select(r =>
            {
                var a = Json.Get(r, "attributes");
                return new FirewallRule(Json.Str(Json.Get(r, "id")), Json.Str(Json.Get(a, "name")), Json.Str(Json.Get(a, "port")),
                    Json.Str(Json.Get(a, "ip_address")), Json.Str(Json.Get(a, "type")).ToLowerInvariant(),
                    Json.Str(Json.Get(a, "status")), Json.Str(Json.Get(a, "created_at")));
            })
            .Where(f => f.Id != "")
            .OrderBy(f => int.TryParse(f.Port.Split(':')[0], out var n) ? n : int.MaxValue)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The primary domain first, then aliases by name.</summary>
    public static List<SiteDomain> Domains(JsonNode? body) =>
        Json.Items(body, "data")
            .Select(r =>
            {
                var a = Json.Get(r, "attributes");
                return new SiteDomain(Json.Str(Json.Get(r, "id")), Json.Str(Json.Get(a, "name")),
                    Json.Str(Json.Get(a, "status")), Json.Str(Json.Get(a, "type")));
            })
            .Where(d => d.Id != "" && d.Name != "")
            .OrderBy(d => d.Type == "primary" ? 0 : 1)
            .ThenBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>The domain's active certificate, or null when the answer carries none.</summary>
    public static Certificate? ActiveCertificate(JsonNode? body)
    {
        if (Attributes(body) is not JsonObject a) return null;
        return new Certificate(Json.Str(Json.Get(body, "data", "id")), Json.Str(Json.Get(a, "type")), Json.Str(Json.Get(a, "status")),
            Json.Str(Json.Get(a, "request_status")), Json.Str(Json.Get(a, "key_type")),
            Json.IsTrue(Json.Get(a, "active")), Json.Str(Json.Get(a, "expires_at")));
    }

    /// <summary>Broken ones first, so a stopped or failed process is at the top of the list.</summary>
    public static List<BackgroundProcess> BackgroundProcesses(JsonNode? body) =>
        Json.Items(body, "data")
            .Select(r =>
            {
                var a = Json.Get(r, "attributes");
                return new BackgroundProcess
                {
                    Id = Json.Str(Json.Get(r, "id")),
                    Command = Json.Str(Json.Get(a, "command")),
                    User = Json.Str(Json.Get(a, "user")),
                    Directory = Json.Str(Json.Get(a, "directory")),
                    Processes = (int)Json.Num(Json.Get(a, "processes")),
                    Status = Json.Str(Json.Get(a, "status")),
                };
            })
            .Where(p => p.Id != "")
            .OrderBy(Status.ProcessRank)
            .ThenBy(p => p.Command, ForgeText.Collation)
            .ToList();

    public static List<ScheduledJob> ScheduledJobs(JsonNode? body) =>
        Json.Items(body, "data")
            .Select(r =>
            {
                var a = Json.Get(r, "attributes");
                return new ScheduledJob
                {
                    Id = Json.Str(Json.Get(r, "id")),
                    Name = Json.Str(Json.Get(a, "name")),
                    Command = Json.Str(Json.Get(a, "command")),
                    User = Json.Str(Json.Get(a, "user")),
                    Frequency = Json.Str(Json.Get(a, "frequency")),
                    Cron = Json.Str(Json.Get(a, "cron")),
                    NextRunTime = Json.Str(Json.Get(a, "next_run_time")),
                    Status = Json.Str(Json.Get(a, "status")),
                };
            })
            .Where(j => j.Id != "")
            .OrderBy(Status.ScheduledJobRank)
            .ThenBy(Status.ScheduledJobTitle, ForgeText.Collation)
            .ToList();

    public static List<Organization> Organizations(JsonNode? body) =>
        Json.Items(body, "data")
            .Select(r =>
            {
                var a = Json.Get(r, "attributes");
                var slug = Json.Str(Json.Get(a, "slug"));
                return new Organization(slug, Json.Str(Json.Get(a, "name"), slug));
            })
            .Where(o => o.Slug != "")
            .ToList();

    public static ForgeUser User(JsonNode? body)
    {
        var a = Json.Get(body, "data", "attributes");
        var email = Json.Str(Json.Get(a, "email"));
        var name = Json.Str(Json.Get(a, "name"));
        return new ForgeUser(email, name == "" ? (email == "" ? "Forge" : email) : name);
    }

    // ---------------------------------------------------------- pagination

    /// <summary><c>meta.next_cursor</c> is the only reliable signal: <c>links</c> is an empty array on the last page.</summary>
    public static string NextCursor(JsonNode? body) => Json.Str(Json.Get(body, "meta", "next_cursor"));

    public static JsonNode? Attributes(JsonNode? body) => Json.Get(body, "data", "attributes");

    /// <summary>
    /// A config file exactly as Forge sent it: no trimming, no line-ending changes. Null
    /// when the answer has no <c>content</c>. <c>auto_source</c> rides along when present.
    /// </summary>
    public static ConfigDocument? ConfigDocument(JsonNode? body)
    {
        // A null content is an empty file; a missing one is a malformed answer.
        if (Attributes(body) is not JsonObject attrs || !attrs.ContainsKey("content")) return null;
        var content = ResourceText(body, "content") ?? "";
        var auto = Json.Get(Attributes(body), "auto_source") is JsonValue v && v.TryGetValue<bool>(out var b) ? b : (bool?)null;
        return new ConfigDocument(content, auto);
    }

    /// <summary>A single resource's string field, or null when it is missing (a malformed answer, unlike an empty one).</summary>
    public static string? ResourceText(JsonNode? body, string field) =>
        Json.Get(Attributes(body), field) is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
}
