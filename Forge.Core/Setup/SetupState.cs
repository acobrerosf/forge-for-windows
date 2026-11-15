using System.Text.Json;
using System.Text.Json.Nodes;
using Forge.Core.Model;

namespace Forge.Core.Setup;

/// <summary>One token belongs to an account. <see cref="User"/> is the Forge identity the rate limit is counted against.</summary>
public sealed record AccountInfo(string Name, string Label, string User);

/// <summary>An organization is reached through exactly one account (org slugs are globally unique).</summary>
public sealed record OrgEntry(string Slug, string Account, string Name)
{
    public string DisplayName => Name == "" ? Slug : Name;
}

public sealed record AppSettings
{
    public const int MinRefreshIntervalSec = 30;

    public int RefreshIntervalSec { get; init; } = 60;
    public bool NotifyDeployments { get; init; } = true;
    public bool StartWithWindows { get; init; }
    public string DashboardUrlTemplate { get; init; } = ForgeText.DefaultDashboardTemplate;

    public AppSettings Normalized() => this with
    {
        RefreshIntervalSec = Math.Clamp(RefreshIntervalSec, MinRefreshIntervalSec, 3600),
        DashboardUrlTemplate = string.IsNullOrWhiteSpace(DashboardUrlTemplate)
            ? ForgeText.DefaultDashboardTemplate : DashboardUrlTemplate.Trim(),
    };
}

/// <summary>
/// The non-secret state: which accounts exist, which organizations each one
/// reaches, and the app's settings. Shaped like omarchy-forge's forge.json,
/// plus a <c>settings</c> object. Hand-editable, so nothing in it is trusted
/// to be the type it should be.
/// </summary>
public sealed record SetupState
{
    public string DefaultOrganization { get; init; } = "";
    public IReadOnlyList<AccountInfo> Accounts { get; init; } = [];
    public IReadOnlyList<OrgEntry> Organizations { get; init; } = [];
    public AppSettings Settings { get; init; } = new();

    public static SetupState Empty { get; } = new();

    public AccountInfo? Account(string name) => Accounts.FirstOrDefault(a => a.Name == name);
    public OrgEntry? Org(string slug) => Organizations.FirstOrDefault(o => o.Slug == slug);

    public string AccountForOrg(string slug) => Org(slug)?.Account ?? "default";

    public string OrgLabel(string slug) => Org(slug)?.DisplayName ?? slug;

    /// <summary>Two tokens from the same person share one budget; only an unknown identity falls back to the account name.</summary>
    public string BudgetBucket(string account)
    {
        var user = Account(account)?.User ?? "";
        return user != "" ? "user:" + user : "account:" + account;
    }

    public static SetupState FromJson(string? text)
    {
        JsonNode? root;
        try { root = JsonNode.Parse(string.IsNullOrWhiteSpace(text) ? "{}" : text); }
        catch (JsonException) { root = null; }
        if (root is not JsonObject obj) return Empty;

        var accounts = new List<AccountInfo>();
        if (obj["accounts"] is JsonObject accountsObj)
        {
            foreach (var (name, value) in accountsObj)
            {
                var label = Json.Str(Json.Get(value, "label"));
                accounts.Add(new AccountInfo(name, label == "" ? name : label, Json.Str(Json.Get(value, "user"))));
            }
        }

        var orgs = new List<OrgEntry>();
        if (obj["organizations"] is JsonObject orgsObj)
        {
            foreach (var (slug, value) in orgsObj)
            {
                var account = Json.Str(Json.Get(value, "account"));
                orgs.Add(new OrgEntry(slug, account == "" ? "default" : account, Json.Str(Json.Get(value, "name"))));
            }
        }

        var s = obj["settings"];
        var defaults = new AppSettings();
        var settings = new AppSettings
        {
            RefreshIntervalSec = Json.Get(s, "refreshIntervalSec") is { } r && Json.IsNumber(r)
                ? (int)Json.Num(r) : defaults.RefreshIntervalSec,
            NotifyDeployments = Json.Get(s, "notifyDeployments") is JsonValue n && n.TryGetValue<bool>(out var nb)
                ? nb : defaults.NotifyDeployments,
            StartWithWindows = Json.IsTrue(Json.Get(s, "startWithWindows")),
            DashboardUrlTemplate = Json.Str(Json.Get(s, "dashboardUrlTemplate")),
        }.Normalized();

        var defaultOrg = Json.Str(obj["organization"]);
        if (defaultOrg == "" && orgs.Count > 0) defaultOrg = orgs[0].Slug;

        return new SetupState
        {
            DefaultOrganization = defaultOrg,
            Accounts = accounts,
            Organizations = orgs,
            Settings = settings,
        };
    }

    public string ToJson()
    {
        var accounts = new JsonObject();
        foreach (var a in Accounts) accounts[a.Name] = new JsonObject { ["label"] = a.Label, ["user"] = a.User };
        var orgs = new JsonObject();
        foreach (var o in Organizations) orgs[o.Slug] = new JsonObject { ["account"] = o.Account, ["name"] = o.Name };
        var root = new JsonObject
        {
            ["organization"] = DefaultOrganization,
            ["accounts"] = accounts,
            ["organizations"] = orgs,
            ["settings"] = new JsonObject
            {
                ["refreshIntervalSec"] = Settings.RefreshIntervalSec,
                ["notifyDeployments"] = Settings.NotifyDeployments,
                ["startWithWindows"] = Settings.StartWithWindows,
                ["dashboardUrlTemplate"] = Settings.DashboardUrlTemplate,
            },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    // ------------------------------------------------------------ editing

    /// <summary>
    /// Adds or updates an account and the organizations it watches. A token for
    /// a Forge user already on file updates that account rather than starting a
    /// second one, which would split one rate budget in half.
    /// </summary>
    public SetupState WithAccount(string? requestedName, ForgeUser user, IEnumerable<OrgEntry> watched, out string accountName)
    {
        var existing = user.Email != "" ? Accounts.FirstOrDefault(a => a.User == user.Email) : null;
        var name = existing?.Name ?? ForgeText.Slugify(string.IsNullOrWhiteSpace(requestedName)
            ? (user.Email.Split('@')[0] is { Length: > 0 } local ? local : user.Name)
            : requestedName);
        if (name == "") name = "default";
        accountName = name;

        var accounts = Accounts.Where(a => a.Name != name).ToList();
        var label = string.IsNullOrWhiteSpace(requestedName) ? user.Name : requestedName.Trim();
        accounts.Add(new AccountInfo(name, label, user.Email));

        // Keep the order organizations were first added in. A chosen one moves to
        // this account (a slug is reached through exactly one); one this account
        // no longer watches is dropped; everyone else's are untouched.
        var chosen = watched.ToList();
        var orgs = new List<OrgEntry>();
        foreach (var o in Organizations)
        {
            var pick = chosen.FirstOrDefault(c => c.Slug == o.Slug);
            if (pick is not null) orgs.Add(pick with { Account = name });
            else if (o.Account != name) orgs.Add(o);
        }
        foreach (var c in chosen)
            if (orgs.All(x => x.Slug != c.Slug)) orgs.Add(c with { Account = name });

        var defaultOrg = orgs.Any(o => o.Slug == DefaultOrganization) ? DefaultOrganization : orgs.FirstOrDefault()?.Slug ?? "";
        return this with { Accounts = accounts, Organizations = orgs, DefaultOrganization = defaultOrg };
    }

    public SetupState WithoutAccount(string name)
    {
        var orgs = Organizations.Where(o => o.Account != name).ToList();
        var defaultOrg = orgs.Any(o => o.Slug == DefaultOrganization) ? DefaultOrganization : orgs.FirstOrDefault()?.Slug ?? "";
        return this with
        {
            Accounts = Accounts.Where(a => a.Name != name).ToList(),
            Organizations = orgs,
            DefaultOrganization = defaultOrg,
        };
    }
}
