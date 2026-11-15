namespace Forge.Core.Model;

/// <summary>Every Forge API path the app uses, with segments encoded in one place. Port of Model.js's path builders.</summary>
public static class Paths
{
    /// <summary>Forge caps a page at 30 however large <c>page[size]</c> asks, so ask for 30 and follow the cursor.</summary>
    public const int PageSize = 30;

    private static string E(string value) => Uri.EscapeDataString(value);

    public static string Paged(string path, string? cursor = null)
    {
        var url = path + (path.Contains('?') ? "&" : "?") + "page%5Bsize%5D=" + PageSize;
        return string.IsNullOrEmpty(cursor) ? url : url + "&page%5Bcursor%5D=" + E(cursor);
    }

    public static string User() => "/user";
    public static string Orgs(string? cursor = null) => Paged("/orgs", cursor);

    public static string Servers(string org, string? cursor = null) => Paged($"/orgs/{E(org)}/servers?sort=name", cursor);

    /// <summary>
    /// One request for the whole organization. <c>include=server</c> is what
    /// puts the server linkage on a site at all. <c>sort</c> is ignored here,
    /// so sites are sorted locally.
    /// </summary>
    public static string Sites(string org, string? cursor = null) =>
        Paged($"/orgs/{E(org)}/sites?include=server,latestDeployment", cursor);

    public static string Server(string org, string serverId) => $"/orgs/{E(org)}/servers/{E(serverId)}";

    public static string ServerSites(string org, string serverId, string? cursor = null) =>
        Paged(Server(org, serverId) + "/sites?include=server,latestDeployment", cursor);

    public static string Site(string org, string serverId, string siteId) => Server(org, serverId) + "/sites/" + E(siteId);

    public static string Deploy(string org, string serverId, string siteId) => Site(org, serverId, siteId) + "/deployments";

    /// <summary>Maintenance mode is an integration: POST installs it, DELETE removes it.</summary>
    public static string Maintenance(string org, string serverId, string siteId) =>
        Site(org, serverId, siteId) + "/integrations/laravel-maintenance";

    public static string ServerAction(string org, string serverId) => Server(org, serverId) + "/actions";

    /// <summary><paramref name="service"/> is never API data — always a literal from the action tables.</summary>
    public static string ServiceAction(string org, string serverId, string service) =>
        Server(org, serverId) + "/services/" + E(service) + "/actions";

    /// <summary>Newest first has to be asked for; <c>include=site</c> is what fills the site relationship.</summary>
    public static string ServerEvents(string org, string serverId, string? cursor = null) =>
        Paged(Server(org, serverId) + "/events?sort=-created_at&include=site", cursor);

    public static string EventOutput(string org, string serverId, string eventId) =>
        Server(org, serverId) + "/events/" + E(eventId) + "/output";

    public static string Monitors(string org, string serverId, string? cursor = null) =>
        Paged(Server(org, serverId) + "/monitors", cursor);

    public static string Heartbeats(string org, string serverId, string siteId, string? cursor = null) =>
        Paged(Site(org, serverId, siteId) + "/heartbeats", cursor);

    public static string DeploymentLog(string org, string serverId, string siteId, string deploymentId) =>
        Deploy(org, serverId, siteId) + "/" + E(deploymentId) + "/log";

    public static string SiteLog(string org, string serverId, string siteId, string kind) =>
        Site(org, serverId, siteId) + "/logs/" + E(kind);

    public static string Commands(string org, string serverId, string siteId) => Site(org, serverId, siteId) + "/commands";

    public static string CommandList(string org, string serverId, string siteId) =>
        Paged(Commands(org, serverId, siteId) + "?sort=-created_at");

    public static string Command(string org, string serverId, string siteId, string commandId) =>
        Commands(org, serverId, siteId) + "/" + E(commandId);

    public static string CommandOutput(string org, string serverId, string siteId, string commandId) =>
        Command(org, serverId, siteId, commandId) + "/output";

    public static string Recipes(string org, string? cursor = null) => Paged($"/orgs/{E(org)}/recipes", cursor);

    public static string RecipeRuns(string org, string recipeId) => $"/orgs/{E(org)}/recipes/{E(recipeId)}/runs";

    /// <summary>This list takes no <c>sort</c>, so the reader picks the highest matching id rather than the first row.</summary>
    public static string RecipeRunList(string org, string recipeId, string? cursor = null) =>
        Paged(RecipeRuns(org, recipeId), cursor);

    public static string RecipeRunLog(string org, string recipeId, string logId) => RecipeRuns(org, recipeId) + "/" + E(logId);
}
