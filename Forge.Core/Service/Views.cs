using Forge.Core.Model;

namespace Forge.Core.Service;

/// <summary>An immutable picture of one organization, safe to hand to the UI thread.</summary>
public sealed record OrgView(
    string Slug,
    string Label,
    string Account,
    IReadOnlyList<Server> Servers,
    IReadOnlyDictionary<string, IReadOnlyList<Site>> SitesByServer,
    bool Refreshing,
    string LastError,
    string AccountError,
    string Note,
    long LastRefreshMs,
    Health Health)
{
    public IReadOnlyList<Site> SitesOf(string serverId) =>
        SitesByServer.TryGetValue(serverId, out var list) ? list : [];

    public Server? ServerById(string id) => Servers.FirstOrDefault(s => s.Id == id);

    public Site? SiteByKey(string key) =>
        SitesByServer.Values.SelectMany(l => l).FirstOrDefault(s => s.Key == key);
}

public sealed record ForgeSnapshot(IReadOnlyList<OrgView> Orgs, Health Health, bool NeedsSetup, string Summary)
{
    public static ForgeSnapshot Empty { get; } = new([], Health.Setup, true, "Forge — not set up yet");

    public OrgView? Org(string slug) => Orgs.FirstOrDefault(o => o.Slug == slug);
}

public sealed record DeploymentNotice(string Org, string OrgLabel, Site Site, bool Failed, string Headline, string Description, string Url);

public sealed record ActionOutcome(bool Ok, string Message);

public sealed record ReadResult<T>(T? Value, string? Error, string NextCursor = "")
{
    public bool Ok => Error is null;
}

/// <summary>One look at a command or recipe run.</summary>
public sealed record RunUpdate(string Header, bool Running, bool Bad, IReadOnlyList<string>? Lines, string? Message);
