using Forge.Core.Model;

namespace Forge.Core.Service;

public sealed partial class ForgeService
{
    private const string DeployReadScope = "Your token can't read deploy settings — that needs the site:manage-deploys scope";

    /// <summary>
    /// The deploy hook URL, read when asked for. It is a secret — anyone holding it can deploy —
    /// so it is handed to the caller and kept nowhere here.
    /// </summary>
    public async Task<ReadResult<string>> DeployHookUrlAsync(string slug, Site site, CancellationToken ct = default)
    {
        var (env, error, _) = await ReadAsync(slug, Paths.DeployHook(slug, site.ServerId, site.Id), DeployReadScope, ct)
            .ConfigureAwait(false);
        if (error is not null) return new(null, error);
        return Parse.ResourceText(env!.Body, "url") is { Length: > 0 } url
            ? new(url, null)
            : new(null, "Forge's answer had no url in it");
    }

    public Task<ReadResult<List<Webhook>>> WebhooksAsync(string slug, Site site, string? cursor = null, CancellationToken ct = default) =>
        ListAsync(slug, Paths.Webhooks(slug, site.ServerId, site.Id, cursor), DeployReadScope, Parse.Webhooks, ct);

    /// <summary>The site's public deploy key, or "" when it has none.</summary>
    public async Task<ReadResult<string>> DeployKeyAsync(string slug, Site site, CancellationToken ct = default)
    {
        var (env, error, _) = await ReadAsync(slug, Paths.DeployKey(slug, site.ServerId, site.Id), DeployReadScope, ct)
            .ConfigureAwait(false);
        if (error is not null) return new(null, error);
        return Parse.DeployKey(env!.Body) is { } key
            ? new(key, null)
            : new(null, "Forge's answer had no key in it");
    }
}
