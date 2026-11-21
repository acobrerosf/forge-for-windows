namespace Forge.Core.Model;

/// <summary>Tones, labels and the health precedence. Port of Model.js's status section and Service.qml's healthFor.</summary>
public static class Status
{
    public static string ServerStateLabel(ServerState state) => state switch
    {
        ServerState.Ready => "ready",
        ServerState.Provisioning => "provisioning",
        ServerState.Unreachable => "unreachable",
        ServerState.Revoked => "revoked",
        _ => "unknown",
    };

    public static Tone ServerTone(ServerState state) => state switch
    {
        ServerState.Unreachable or ServerState.Revoked => Tone.Bad,
        ServerState.Provisioning => Tone.Busy,
        ServerState.Ready => Tone.Ok,
        _ => Tone.Idle,
    };

    public static Tone DeploymentTone(string? status) => (status ?? "").ToLowerInvariant() switch
    {
        "failed" or "failed-build" => Tone.Bad,
        "deploying" or "pending" or "queued" or "installing" or "creating" => Tone.Busy,
        "finished" or "deployed" or "installed" => Tone.Ok,
        _ => Tone.Idle,
    };

    public static string DeploymentLabel(string? status)
    {
        var value = (status ?? "").ToLowerInvariant();
        if (value == "") return "never deployed";
        if (value == "failed-build") return "build failed";
        return value.Replace('-', ' ');
    }

    private enum SiteFact { Idle, Ok, Busy, Bad, Flip, Parked }

    /// <summary>
    /// The one place the precedence lives: a running or failed deploy outranks
    /// maintenance mode, which outranks a settled deploy.
    /// </summary>
    private static SiteFact FactOf(Site? site)
    {
        if (site is null) return SiteFact.Idle;
        var tone = DeploymentTone(site.DeploymentStatus);
        if (tone == Tone.Busy) return SiteFact.Busy;
        if (tone == Tone.Bad) return SiteFact.Bad;
        if (site.MaintenanceStatus != "") return SiteFact.Flip;
        if (site.Maintenance) return SiteFact.Parked;
        return tone == Tone.Ok ? SiteFact.Ok : SiteFact.Idle;
    }

    /// <summary>
    /// What a site reports. <see cref="StatusText.Timed"/> says whether the label
    /// is the deployment's, which is the only one its timestamp belongs beside.
    /// </summary>
    public static StatusText SiteStatus(Site? site) => FactOf(site) switch
    {
        SiteFact.Flip => new(site!.MaintenanceStatus + "…", Tone.Busy, false),
        SiteFact.Parked => new("maintenance", Tone.Warn, false),
        // A running deploy has no timestamp of its own yet; the latest record's belongs to the previous one.
        var fact => new(site is null ? "" : DeploymentLabel(site.DeploymentStatus), ToneOf(fact), site is not null && fact != SiteFact.Busy),
    };

    public static Tone SiteTone(Site? site) => ToneOf(FactOf(site));

    private static Tone ToneOf(SiteFact fact) => fact switch
    {
        SiteFact.Flip or SiteFact.Busy => Tone.Busy,
        SiteFact.Parked => Tone.Warn,
        SiteFact.Bad => Tone.Bad,
        SiteFact.Ok => Tone.Ok,
        _ => Tone.Idle,
    };

    public static bool CanDeploy(Site? site) => site is not null && site.RepoUrl != "";

    public static Health Worse(Health a, Health b) => b > a ? b : a;

    public static Badge BadgeFor(Health health) => health switch
    {
        Health.Bad => Badge.Bad,
        Health.Busy => Badge.Busy,
        Health.Maintenance => Badge.Maintenance,
        Health.Setup or Health.Error => Badge.Warn,
        _ => Badge.None,
    };

    /// <summary>
    /// An organization's health from what it can see. A request or credential
    /// problem is folded in, but a broken server or deploy still wins.
    /// </summary>
    public static Health HealthOf(IEnumerable<Server> servers,
        IEnumerable<IReadOnlyList<Site>> sitesByServer, bool accountProblem, bool requestProblem)
    {
        var worst = Health.Ok;
        if (accountProblem) worst = Worse(worst, Health.Setup);
        if (requestProblem) worst = Worse(worst, Health.Error);
        if (servers.Any(s => ServerTone(s.State) == Tone.Bad)) return Health.Bad;
        foreach (var sites in sitesByServer)
        {
            foreach (var site in sites)
            {
                var tone = SiteTone(site);
                if (tone == Tone.Bad) return Health.Bad;
                if (tone == Tone.Busy) worst = Worse(worst, Health.Busy);
                else if (tone == Tone.Warn) worst = Worse(worst, Health.Maintenance);
            }
        }
        return worst;
    }

    // ------------------------------------------------------------ monitors

    private static readonly Dictionary<string, string> MonitorMetrics = new()
    {
        ["cpu_load"] = "CPU load",
        ["disk"] = "Disk",
        ["free_memory"] = "Free memory",
        ["used_memory"] = "Used memory",
    };

    /// <summary>A monitor has no name; its identity is type + operator + threshold.</summary>
    public static string MonitorLabel(ServerMonitor m)
    {
        var metric = MonitorMetrics.GetValueOrDefault(m.Type) ?? (m.Type == "" ? "monitor" : m.Type);
        var op = m.Operator == "lte" ? "≤" : m.Operator == "gte" ? "≥" : "";
        return op == "" ? metric : $"{metric} {op} {ForgeText.Number(m.Threshold)}%";
    }

    public static string MonitorDetail(ServerMonitor m)
    {
        var parts = new List<string>();
        if (m.Minutes == 1) parts.Add("every minute");
        else if (m.Minutes > 0) parts.Add($"every {m.Minutes} min");
        if (m.Notify != "") parts.Add("notifies " + m.Notify);
        return string.Join(" · ", parts);
    }

    /// <summary>The install lifecycle wins until it settles on <c>installed</c>; then the alert state is the point.</summary>
    public static (string Label, Tone Tone) MonitorStatus(ServerMonitor m)
    {
        if (m.Status != "" && m.Status != "installed")
        {
            var tone = m.Status.EndsWith("ing", StringComparison.Ordinal) ? Tone.Busy
                : m.Status.StartsWith("failed", StringComparison.Ordinal) ? Tone.Bad : Tone.Idle;
            return (m.Status.Replace('-', ' '), tone);
        }
        return (m.State.ToLowerInvariant(), m.State == "ALERT" ? Tone.Bad : m.State == "OK" ? Tone.Ok : Tone.Idle);
    }

    internal static int MonitorRank(ServerMonitor m) => MonitorStatus(m).Tone switch
    {
        Tone.Bad => 0,
        Tone.Busy => 1,
        Tone.Idle => 2,
        _ => 3,
    };

    // ---------------------------------------------------------- heartbeats

    public static string HeartbeatFrequency(Heartbeat h)
    {
        var minutes = h.Frequency;
        if (minutes == -1) return h.CustomFrequency == "" ? "on a schedule" : h.CustomFrequency;
        switch (minutes)
        {
            case 1: return "every minute";
            case 60: return "hourly";
            case 1440: return "daily";
            case 10080: return "weekly";
        }
        if (minutes <= 0) return "";
        if (minutes % 10080 == 0) return $"every {minutes / 10080} weeks";
        if (minutes % 1440 == 0) return $"every {minutes / 1440} days";
        if (minutes % 60 == 0) return $"every {minutes / 60} hours";
        return $"every {minutes} min";
    }

    public static string HeartbeatDetail(Heartbeat h)
    {
        var parts = new List<string>();
        var frequency = HeartbeatFrequency(h);
        if (frequency != "") parts.Add(frequency);
        if (h.GracePeriod > 0) parts.Add($"grace {h.GracePeriod} min");
        return string.Join(" · ", parts);
    }

    public static (string Label, Tone Tone) HeartbeatStatus(Heartbeat h) =>
        h.Status == "" ? ("unknown", Tone.Idle)
            : (h.Status, h.Status == "missing" ? Tone.Bad : h.Status == "beating" ? Tone.Ok : Tone.Idle);

    internal static int HeartbeatRank(Heartbeat h) => h.Status switch
    {
        "missing" => 0,
        "pending" => 1,
        "" => 2,
        _ => 3,
    };

    // ----------------------------------------------- background processes

    /// <summary>
    /// Supervisor's words. A process that is meant to run and isn't is the
    /// point of the list: fatal and backoff are broken, stopped and exited are parked.
    /// </summary>
    public static (string Label, Tone Tone) ProcessStatus(BackgroundProcess p)
    {
        var s = p.Status.ToLowerInvariant();
        if (s == "") return ("unknown", Tone.Idle);
        var tone = s switch
        {
            "running" => Tone.Ok,
            "fatal" or "backoff" or "failed" => Tone.Bad,
            "stopped" or "exited" => Tone.Warn,
            _ when s.EndsWith("ing", StringComparison.Ordinal) => Tone.Busy,
            _ => Tone.Idle,
        };
        return (s.Replace('-', ' '), tone);
    }

    internal static int ProcessRank(BackgroundProcess p) => ProcessStatus(p).Tone switch
    {
        Tone.Bad => 0,
        Tone.Warn => 1,
        Tone.Busy => 2,
        Tone.Idle => 3,
        _ => 4,
    };

    public static string ProcessDetail(BackgroundProcess p)
    {
        var parts = new List<string>();
        if (p.User != "") parts.Add("as " + p.User);
        if (p.Processes > 1) parts.Add($"{p.Processes} processes");
        if (p.Directory != "") parts.Add(p.Directory);
        return string.Join(" · ", parts);
    }

    // ------------------------------------------------------ scheduled jobs

    /// <summary>A job is named by its name when it has one, else by its command.</summary>
    public static string ScheduledJobTitle(ScheduledJob j) =>
        j.Name != "" ? j.Name : j.Command != "" ? j.Command : "scheduled job";

    public static (string Label, Tone Tone) ScheduledJobStatus(ScheduledJob j)
    {
        var s = j.Status.ToLowerInvariant();
        if (s is "" or "installed") return ("", Tone.Idle);
        var tone = s.StartsWith("failed", StringComparison.Ordinal) ? Tone.Bad
            : s.EndsWith("ing", StringComparison.Ordinal) ? Tone.Busy : Tone.Idle;
        return (s.Replace('-', ' '), tone);
    }

    internal static int ScheduledJobRank(ScheduledJob j) => ScheduledJobStatus(j).Tone switch
    {
        Tone.Bad => 0,
        Tone.Busy => 1,
        _ => 2,
    };

    /// <summary>When the job runs next — a time still to come, unlike every other time on a row.</summary>
    public static string ScheduledJobNext(ScheduledJob j, long nowMs) =>
        ForgeText.Until(j.NextRunTime, nowMs) switch
        {
            "" => "",
            "now" => "due now",
            var until => "next " + until,
        };

    public static string ScheduledJobDetail(ScheduledJob j)
    {
        var parts = new List<string>();
        var when = j.Frequency is "" or "custom" ? j.Cron : j.Frequency;
        if (when != "") parts.Add(when);
        if (j.User != "") parts.Add("as " + j.User);
        if (j.Name != "" && j.Command != "") parts.Add(j.Command);
        return string.Join(" · ", parts);
    }

    // --------------------------------------------------------- deployments

    public static string DeploymentDetail(Deployment d)
    {
        var parts = new List<string>();
        if (d.CommitHash != "") parts.Add(d.CommitHash);
        if (d.CommitAuthor != "") parts.Add(d.CommitAuthor);
        if (d.Type != "") parts.Add(d.Type);
        return string.Join(" · ", parts);
    }

    /// <summary>When the deployment ended, else when it started, else when it was queued.</summary>
    public static string DeploymentTime(Deployment d) =>
        d.EndedAt != "" ? d.EndedAt : d.StartedAt != "" ? d.StartedAt : d.CreatedAt;

    // ------------------------------------------------------------- details

    /// <summary>The rest of the site payload, for the detail block. Empty values are dropped.</summary>
    public static IReadOnlyList<(string Label, string Value)> SiteDetails(Site? site)
    {
        var list = new List<(string, string)>();
        if (site is null) return list;
        void Add(string label, string value) { if (value != "") list.Add((label, value)); }

        Add("PHP", site.PhpVersion);
        Add("Type", site.AppType);
        Add("Status", site.SiteStatus);
        if (site.MaintenanceStatus != "") Add("Maintenance", site.MaintenanceStatus);
        else if (site.Maintenance) Add("Maintenance", "on");
        if (site.Isolated) Add("Isolation", "isolated user");
        if (site.ZeroDowntime) Add("Deploys", "zero downtime");
        if (site.UsesEnvoyer) Add("Deploys", "via Envoyer");
        if (site.DeploymentRetention > 0) Add("Keeps", ForgeText.Pluralize(site.DeploymentRetention, "release"));
        if (site.Aliases.Count > 0) Add("Aliases", string.Join(" · ", site.Aliases));
        Add("Healthcheck", site.HealthcheckUrl);
        return list;
    }

    public static string ServerMeta(Server server)
    {
        var location = string.Join(" · ", new[] { server.Provider, server.Region }.Where(s => s != ""));
        if (server.Ip == "") return location;
        return location == "" ? server.Ip : location + " · " + server.Ip;
    }
}
