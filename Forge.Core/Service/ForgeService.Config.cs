using Forge.Core.Model;

namespace Forge.Core.Service;

/// <summary>What a config save came to. <see cref="Now"/> is what Forge holds afterwards, when it could be read.</summary>
public sealed record ConfigSaveResult(bool Ok, bool Conflict, string Message, ConfigDocument? Now);

public sealed partial class ForgeService
{
    /// <summary>
    /// One of a site's config files, read on demand and kept whole: unlike a log it is
    /// not trimmed, capped or cleaned, because it may be saved back. Nothing here keeps it.
    /// </summary>
    public async Task<ReadResult<ConfigDocument>> ConfigFileAsync(string slug, Site site, ConfigFile file, CancellationToken ct = default,
        bool force = false)
    {
        var (env, error, _) = await ReadAsync(slug, ConfigFiles.Path(slug, site, file), ConfigFiles.ReadScopeMessage(file), ct, force)
            .ConfigureAwait(false);
        if (error is not null) return new(null, error);
        return Parse.ConfigDocument(env!.Body) is { } doc
            ? new(doc, null)
            : new(null, "Forge's answer had no content in it");
    }

    /// <summary>
    /// Saves <paramref name="edited"/> over a file that was <paramref name="baseline"/> when it was
    /// opened. It reads the file again first and refuses when it changed meanwhile (someone saved it
    /// on the website, say), then reads it once more so the caller shows what Forge now holds.
    /// Three requests, all of them asked for by a press.
    /// </summary>
    public async Task<ConfigSaveResult> SaveConfigAsync(string slug, Site site, ConfigFile file, ConfigDocument baseline, string edited,
        CancellationToken ct = default)
    {
        if (edited == baseline.Content) return new(false, false, "Nothing changed", baseline);

        // The user pressed Y: the ceiling does not stop the check, as it does not stop the write.
        var check = await ConfigFileAsync(slug, site, file, ct, force: true).ConfigureAwait(false);
        if (!check.Ok) return new(false, false, "Couldn't check the file before saving: " + check.Error + ". Nothing was saved.", null);
        if (check.Value!.Content != baseline.Content)
            return new(false, true, $"The {ConfigFiles.Lower(file)} changed in Forge since you opened it. Nothing was saved.", check.Value);

        var spec = Actions.SaveConfig(slug, site, file, edited, check.Value.AutoSource ?? baseline.AutoSource);
        var outcome = await SendActionAsync(slug, site.ServerId, spec, ct).ConfigureAwait(false);
        if (!outcome.Ok) return new(false, false, outcome.Message, null);

        var after = await ConfigFileAsync(slug, site, file, ct, force: true).ConfigureAwait(false);
        return after.Ok
            ? new(true, false, outcome.Message, after.Value)
            : new(true, false, outcome.Message + ", but couldn't read it back: " + after.Error, null);
    }
}
