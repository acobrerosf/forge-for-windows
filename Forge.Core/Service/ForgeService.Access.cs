using Forge.Core.Model;

namespace Forge.Core.Service;

public sealed partial class ForgeService
{
    /// <summary>A site's tab reads at most this many certificates at a time: each domain costs a request.</summary>
    public const int CertificateBatch = 10;

    public Task<ReadResult<List<SshKey>>> SshKeysAsync(string slug, string serverId, string? cursor = null, CancellationToken ct = default) =>
        ListAsync(slug, Paths.SshKeys(slug, serverId, cursor), Actions.SshKeyReadScopeMessage, Parse.SshKeys, ct);

    /// <summary>The server's own public key, the one it signs in elsewhere with (to pull a repository, say).</summary>
    public async Task<ReadResult<string>> ServerKeyAsync(string slug, string serverId, CancellationToken ct = default)
    {
        var (env, error, fatal, throttled) = await ReadFullAsync(slug, Paths.ServerKey(slug, serverId), Actions.SshKeyReadScopeMessage, ct)
            .ConfigureAwait(false);
        if (error is not null) return Failed<string>(error, fatal, throttled);
        return Parse.ResourceText(env!.Body, "public_key") is { } key
            ? new(key.Trim(), null)
            : new(null, "Forge's answer had no public_key in it");
    }

    public Task<ReadResult<List<FirewallRule>>> FirewallRulesAsync(string slug, string serverId, string? cursor = null, CancellationToken ct = default) =>
        ListAsync(slug, Paths.FirewallRules(slug, serverId, cursor), Actions.FirewallReadScopeMessage, Parse.FirewallRules, ct);

    public Task<ReadResult<List<SiteDomain>>> DomainsAsync(string slug, Site site, string? cursor = null, CancellationToken ct = default) =>
        ListAsync(slug, Paths.Domains(slug, site.ServerId, site.Id, cursor), Actions.CertificateReadScopeMessage, Parse.Domains, ct);

    /// <summary>
    /// The domain's active certificate. A domain with none (a 404, or no data) answers null, not an error.
    /// A <see cref="ReadResult{T}.Fatal"/> or <see cref="ReadResult{T}.Throttled"/> answer means the rest of
    /// the batch would fail the same way.
    /// </summary>
    public async Task<ReadResult<Certificate?>> ActiveCertificateAsync(string slug, Site site, SiteDomain domain, CancellationToken ct = default)
    {
        var (env, error, fatal, throttled) = await ReadFullAsync(slug, Paths.ActiveCertificate(slug, site.ServerId, site.Id, domain.Id),
            Actions.CertificateReadScopeMessage, ct, notFoundOk: true).ConfigureAwait(false);
        if (error is not null) return Failed<Certificate?>(error, fatal, throttled);
        return new(env!.Status == 404 ? null : Parse.ActiveCertificate(env.Body), null);
    }
}
