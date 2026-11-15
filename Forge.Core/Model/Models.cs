namespace Forge.Core.Model;

public enum ServerState { Unknown, Ready, Provisioning, Unreachable, Revoked }

/// <summary>Three tones and a fourth drawn by shape: quiet, fine, happening, parked, broken.</summary>
public enum Tone { Idle, Ok, Busy, Warn, Bad }

/// <summary>
/// One precedence, inside an organization and across them. Ordered by rank:
/// a broken server or deploy is a reason to act; a token or request problem is
/// a reason to look; maintenance is not a thing that has gone wrong.
/// </summary>
public enum Health { Ok = 0, Maintenance = 1, Busy = 2, Setup = 3, Error = 4, Bad = 5 }

/// <summary>How a health rank is drawn on the tray icon.</summary>
public enum Badge { None, Maintenance, Busy, Warn, Bad }

public sealed record Server
{
    public required string Id { get; init; }
    public string Key => Id;
    public string Name { get; init; } = "server";
    /// <summary>Assigned at creation and not changed by a rename — the dashboard addresses servers by it.</summary>
    public string Slug { get; init; } = "";
    /// <summary>Forge's server type: app, web, worker, loadbalancer, database, cache…</summary>
    public string Kind { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Region { get; init; } = "";
    public string Ip { get; init; } = "";
    public int SshPort { get; init; } = 22;
    /// <summary>The enum word, e.g. <c>php84</c>.</summary>
    public string PhpVersion { get; init; } = "";
    /// <summary><c>mysql8</c>, <c>mariadb1011</c>, <c>postgres17</c> or empty.</summary>
    public string DatabaseType { get; init; } = "";
    public ServerState State { get; init; } = ServerState.Unknown;
}

public sealed record Site
{
    public required string Id { get; init; }
    public required string ServerId { get; init; }
    public string Key => ServerId + ":" + Id;
    public string Name { get; init; } = "site";
    public string Url { get; init; } = "";
    public bool Https { get; init; }
    public string SiteStatus { get; init; } = "";
    /// <summary>The live status when there is one, else the latest deployment record's.</summary>
    public string DeploymentStatus { get; init; } = "";
    public string DeploymentId { get; init; } = "";
    public string Branch { get; init; } = "";
    public string RepoUrl { get; init; } = "";
    public bool QuickDeploy { get; init; }
    public string DeployedAt { get; init; } = "";
    public string CommitHash { get; init; } = "";
    public string CommitMessage { get; init; } = "";
    /// <summary>A display string, e.g. <c>PHP 8.4</c> — not the server's enum word.</summary>
    public string PhpVersion { get; init; } = "";
    public string AppType { get; init; } = "";
    public bool Isolated { get; init; }
    public bool ZeroDowntime { get; init; }
    public bool UsesEnvoyer { get; init; }
    public bool Wildcards { get; init; }
    public int DeploymentRetention { get; init; }
    public string HealthcheckUrl { get; init; } = "";
    public IReadOnlyList<string> Aliases { get; init; } = [];
    public bool Maintenance { get; init; }
    /// <summary><c>enabling</c>/<c>disabling</c> while Forge is flipping maintenance mode on the box.</summary>
    public string MaintenanceStatus { get; init; } = "";
}

public sealed record ForgeEvent(string Id, string Description, string RanAs, string SiteName, string CreatedAt);

public sealed record Recipe(string Id, string Name, string User, string FirstLine, string UpdatedAt);

public sealed record ServerMonitor
{
    public required string Id { get; init; }
    public string Type { get; init; } = "";
    public string Operator { get; init; } = "";
    public double Threshold { get; init; }
    public int Minutes { get; init; }
    public string Notify { get; init; } = "";
    public string Status { get; init; } = "";
    public string State { get; init; } = "";
    public string StateChangedAt { get; init; } = "";
}

/// <summary>A site's dead-man switch. <c>ping_url</c> is deliberately not kept: it is the token that marks the site alive.</summary>
public sealed record Heartbeat
{
    public required string Id { get; init; }
    public string Name { get; init; } = "heartbeat";
    public string Status { get; init; } = "";
    public int GracePeriod { get; init; }
    public int Frequency { get; init; }
    public string CustomFrequency { get; init; } = "";
}

public sealed record Organization(string Slug, string Name);

public sealed record ForgeUser(string Email, string Name);

public readonly record struct StatusText(string Label, Tone Tone, bool Timed);
