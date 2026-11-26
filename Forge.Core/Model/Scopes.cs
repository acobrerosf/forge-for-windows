namespace Forge.Core.Model;

/// <summary>One token scope the app can use, and what it turns on.</summary>
public sealed record ScopeInfo(string Name, string Use);

/// <summary>
/// The Forge token scopes the app uses, as forge.laravel.com/profile/api names them. Three are needed
/// to watch anything; every other one turns on one kind of write and can be left out. Anything not
/// listed here the app never uses, so a token should not have it.
/// </summary>
public static class Scopes
{
    public const string UserView = "user:view";
    public const string OrganizationView = "organization:view";
    public const string ServerView = "server:view";

    /// <summary>Without these the token check fails, or there are no servers or sites to show.</summary>
    public static readonly IReadOnlyList<ScopeInfo> Required =
    [
        new(UserView, "Check the token: who it belongs to"),
        new(OrganizationView, "List your organizations"),
        new(ServerView, "Watch servers and sites, and read events, logs, processes, jobs, SSH keys and firewall rules"),
    ];

    /// <summary>Each is opt-in; a view that needs a missing one says which.</summary>
    public static readonly IReadOnlyList<(string Group, IReadOnlyList<ScopeInfo> Scopes)> Optional =
    [
        ("Sites", [
            new("site:manage-deploys", "Deploy, read deployment logs, edit the deploy script, quick deploy, the deploy hook, webhooks and the deploy key"),
            new("site:manage-commands", "Run commands and turn maintenance mode on or off"),
            new("site:manage-environment", "Read and edit the environment file"),
            new("site:manage-nginx", "Read and edit the site's nginx config"),
            new("site:manage-ssl", "Read certificates on the SSL tab, if Forge asks for it"),
        ]),
        ("Servers", [
            new("server:manage-services", "Restart nginx, PHP-FPM, supervisor, redis and the database, and reboot"),
            new("server:manage-logs", "Read and clear site and server logs"),
            new("server:create-daemons", "Read a background process's log and restart it"),
            new("server:create-keys", "Add SSH keys"),
            new("server:delete-keys", "Remove SSH keys"),
            new("server:manage-network", "Add and remove firewall rules"),
        ]),
        ("Recipes", [
            new("recipe:view", "List recipes"),
            new("recipe:manage", "Run recipes"),
        ]),
    ];

    /// <summary>Every scope the app uses, required ones first.</summary>
    public static IEnumerable<ScopeInfo> All => Required.Concat(Optional.SelectMany(g => g.Scopes));

    /// <summary>The token check's own reads: a 403 there means one of the required scopes is missing.</summary>
    public static string MissingMessage(string scope) =>
        $"The token is missing the {scope} scope, which the app needs to start. See \"Which scopes?\" for the full list.";
}
