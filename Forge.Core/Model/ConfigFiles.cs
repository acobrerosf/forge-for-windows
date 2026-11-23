namespace Forge.Core.Model;

/// <summary>The three files a site's Config tab reads and edits.</summary>
public enum ConfigFile { Environment, DeployScript, Nginx }

/// <summary>One file as Forge holds it. <see cref="AutoSource"/> is the deploy script's flag, when Forge reports one.</summary>
public sealed record ConfigDocument(string Content, bool? AutoSource = null);

/// <summary>
/// Everything that differs between the three files, in one table: the label,
/// the body key a PUT takes, and the scope a 403 names. Forge has no separate
/// view scope for any of them, so a refused read names the same scope as a refused save.
/// </summary>
public static class ConfigFiles
{
    public sealed record Info(ConfigFile File, string Key, string Label, string Detail, string Scope, string BodyKey);

    public static readonly IReadOnlyList<Info> All =
    [
        new(ConfigFile.Environment, "environment", "Environment file", ".env", "site:manage-environment", "environment"),
        new(ConfigFile.DeployScript, "deploy-script", "Deploy script", "runs on every deploy", "site:manage-deploys", "content"),
        new(ConfigFile.Nginx, "nginx", "Nginx config", "the site's server block", "site:manage-nginx", "config"),
    ];

    public static Info Of(ConfigFile file) => All.First(i => i.File == file);

    public static ConfigFile? FromKey(string key) => All.FirstOrDefault(i => i.Key == key)?.File;

    /// <summary>"Environment file" mid-sentence: "the environment file".</summary>
    public static string Lower(ConfigFile file)
    {
        var label = Of(file).Label;
        return char.ToLowerInvariant(label[0]) + label[1..];
    }

    public static string ReadScopeMessage(ConfigFile file) =>
        $"Your token can't read the {Lower(file)} — that needs the {Of(file).Scope} scope";

    public static string WriteScopeMessage(ConfigFile file) =>
        $"Your token can't change the {Lower(file)} — that needs the {Of(file).Scope} scope";

    public static string Path(string org, Site site, ConfigFile file) => file switch
    {
        ConfigFile.Environment => Paths.SiteEnvironment(org, site.ServerId, site.Id),
        ConfigFile.DeployScript => Paths.DeploymentScript(org, site.ServerId, site.Id),
        _ => Paths.SiteNginx(org, site.ServerId, site.Id),
    };
}
