using Forge.Core.Api;
using Forge.Core.Model;

namespace Forge.Core.Service;

public sealed partial class ForgeService
{
    private const string ServerViewScope = "Your token can't view this server — that needs the server:view scope";
    private const string DeployLogScope = "Your token can't read deployment logs — that needs the site:manage-deploys scope";
    private const string SiteLogScope = "Your token can't read site logs — that needs the server:manage-logs scope";
    private const string RecipeViewScope = "Your token can't list recipes — that needs the recipe:view scope";
    private const string ServerLogScope = "Your token can't read server logs — that needs the server:manage-logs scope";
    private const string ProcessLogScope = "Your token can't read background process logs — that needs the server:create-daemons scope";

    // --------------------------------------------------------------- reads

    /// <summary>
    /// Every on-demand read: refused when the organization is gone, the bucket
    /// is held or the ceiling is close. Refusals are reported to whoever asked,
    /// never across the organization's rows.
    /// </summary>
    private async Task<(ForgeEnvelope? Env, string? Error, bool Fatal)> ReadAsync(string slug, string path, string scope403, CancellationToken ct,
        bool force = false)
    {
        string account;
        lock (_lock)
        {
            if (!_orgs.TryGetValue(slug, out var st)) return (null, "That organization is no longer watched", true);
            account = st.Account;
        }
        var env = await RequestAsync(account, "GET", path, null, force, ct).ConfigureAwait(false);
        if (env.Ok) return (env, null, false);
        if (env.MissingToken) { Publish(); return (null, env.ErrorMessage, true); }
        if (env.Status == 401) return (null, "Forge rejected the token", true);
        if (env.Status == 403) return (null, scope403, true);
        return (null, env.ErrorMessage, false);
    }

    private async Task<ReadResult<IReadOnlyList<string>>> DocumentAsync(string slug, string path, string field, string scope,
        Func<string, string>? shape, CancellationToken ct)
    {
        var (env, error, _) = await ReadAsync(slug, path, scope, ct).ConfigureAwait(false);
        if (error is not null) return new(null, error);
        var text = Parse.ResourceText(env!.Body, field);
        if (text is null) return new(null, "Forge's answer had no " + field + " in it");
        return new(ForgeText.LogLines(shape is null ? text : shape(text)), null);
    }

    public Task<ReadResult<IReadOnlyList<string>>> DeploymentLogAsync(string slug, Site site, CancellationToken ct = default) =>
        site.DeploymentId == ""
            ? Task.FromResult(new ReadResult<IReadOnlyList<string>>(null, "This site has never been deployed"))
            : DocumentAsync(slug, Paths.DeploymentLog(slug, site.ServerId, site.Id, site.DeploymentId), "output", DeployLogScope, null, ct);

    public Task<ReadResult<IReadOnlyList<string>>> SiteLogAsync(string slug, Site site, string kind, CancellationToken ct = default) =>
        !Actions.IsSiteLogKind(kind)
            ? Task.FromResult(new ReadResult<IReadOnlyList<string>>(null, "Unknown log"))
            : DocumentAsync(slug, Paths.SiteLog(slug, site.ServerId, site.Id, kind), "content", SiteLogScope, ForgeText.SiteLogContent, ct);

    /// <summary>Any deployment's log, not only the latest one.</summary>
    public Task<ReadResult<IReadOnlyList<string>>> DeploymentLogAsync(string slug, Site site, string deploymentId, CancellationToken ct = default) =>
        deploymentId == ""
            ? Task.FromResult(new ReadResult<IReadOnlyList<string>>(null, "That deployment has no id"))
            : DocumentAsync(slug, Paths.DeploymentLog(slug, site.ServerId, site.Id, deploymentId), "output", DeployLogScope, null, ct);

    public Task<ReadResult<IReadOnlyList<string>>> ServerLogAsync(string slug, string serverId, string key, CancellationToken ct = default) =>
        !Actions.IsServerLogKind(key)
            ? Task.FromResult(new ReadResult<IReadOnlyList<string>>(null, "Unknown log"))
            : DocumentAsync(slug, Paths.ServerLog(slug, serverId, key), "content", ServerLogScope, ForgeText.SiteLogContent, ct);

    public Task<ReadResult<IReadOnlyList<string>>> BackgroundProcessLogAsync(string slug, string serverId, string processId, CancellationToken ct = default) =>
        DocumentAsync(slug, Paths.BackgroundProcessLog(slug, serverId, processId), "content", ProcessLogScope, ForgeText.SiteLogContent, ct);

    /// <summary>The last run's output. A site's job lives under the site; <paramref name="site"/> chooses which.</summary>
    public Task<ReadResult<IReadOnlyList<string>>> ScheduledJobOutputAsync(string slug, string serverId, Site? site, string jobId, CancellationToken ct = default) =>
        DocumentAsync(slug,
            site is null ? Paths.ScheduledJobOutput(slug, serverId, jobId) : Paths.SiteScheduledJobOutput(slug, site.ServerId, site.Id, jobId),
            "output", ServerViewScope, null, ct);

    public Task<ReadResult<IReadOnlyList<string>>> EventOutputAsync(string slug, string serverId, string eventId, CancellationToken ct = default) =>
        DocumentAsync(slug, Paths.EventOutput(slug, serverId, eventId), "output", ServerViewScope, null, ct);

    private async Task<ReadResult<List<T>>> ListAsync<T>(string slug, string path, string scope, Func<System.Text.Json.Nodes.JsonNode?, List<T>> parse, CancellationToken ct)
    {
        var (env, error, _) = await ReadAsync(slug, path, scope, ct).ConfigureAwait(false);
        if (error is not null) return new(null, error);
        return new(parse(env!.Body), null, Parse.NextCursor(env.Body));
    }

    /// <summary>A page, not a chain: the next page is a row the reader presses.</summary>
    public Task<ReadResult<List<ForgeEvent>>> EventsAsync(string slug, string serverId, string? cursor = null, CancellationToken ct = default) =>
        ListAsync(slug, Paths.ServerEvents(slug, serverId, cursor), ServerViewScope, Parse.Events, ct);

    public Task<ReadResult<List<ServerMonitor>>> MonitorsAsync(string slug, string serverId, string? cursor = null, CancellationToken ct = default) =>
        ListAsync(slug, Paths.Monitors(slug, serverId, cursor), ServerViewScope, Parse.Monitors, ct);

    public Task<ReadResult<List<Heartbeat>>> HeartbeatsAsync(string slug, Site site, string? cursor = null, CancellationToken ct = default) =>
        ListAsync(slug, Paths.Heartbeats(slug, site.ServerId, site.Id, cursor), ServerViewScope, Parse.Heartbeats, ct);

    public Task<ReadResult<List<Deployment>>> DeploymentsAsync(string slug, Site site, string? cursor = null, CancellationToken ct = default) =>
        ListAsync(slug, Paths.Deployments(slug, site.ServerId, site.Id, cursor), ServerViewScope, Parse.Deployments, ct);

    public Task<ReadResult<List<BackgroundProcess>>> BackgroundProcessesAsync(string slug, string serverId, string? cursor = null, CancellationToken ct = default) =>
        ListAsync(slug, Paths.BackgroundProcesses(slug, serverId, cursor), ServerViewScope, Parse.BackgroundProcesses, ct);

    /// <summary>The server's own jobs, or one site's when <paramref name="site"/> is given.</summary>
    public Task<ReadResult<List<ScheduledJob>>> ScheduledJobsAsync(string slug, string serverId, Site? site, string? cursor = null, CancellationToken ct = default) =>
        ListAsync(slug,
            site is null ? Paths.ScheduledJobs(slug, serverId, cursor) : Paths.SiteScheduledJobs(slug, site.ServerId, site.Id, cursor),
            ServerViewScope, Parse.ScheduledJobs, ct);

    public Task<ReadResult<List<Recipe>>> RecipesAsync(string slug, string? cursor = null, CancellationToken ct = default) =>
        ListAsync(slug, Paths.Recipes(slug, cursor), RecipeViewScope, Parse.Recipes, ct);

    // -------------------------------------------------------------- writes

    public Task<ActionOutcome> DeployAsync(string slug, Site site, CancellationToken ct = default)
    {
        var spec = Actions.SiteActions(slug, site).First(a => a.Id == "deploy");
        return SendActionAsync(slug, site.ServerId, spec, ct);
    }

    /// <summary>
    /// Every write, whatever its subject. Single-flight: one press cannot
    /// become two requests. Forge answers 202 to all of them, so the message
    /// says "requested", not "done".
    /// </summary>
    public async Task<ActionOutcome> SendActionAsync(string slug, string serverId, ActionSpec action, CancellationToken ct = default)
    {
        if (action.Path == "") return new(false, "That is no longer listed");
        if (!action.Available) return new(false, action.Reason);
        if (Interlocked.Exchange(ref _writing, 1) == 1) return new(false, "Another action is still being sent");
        try
        {
            string account;
            lock (_lock)
            {
                if (!_orgs.TryGetValue(slug, out var st)) return new(false, "That organization is no longer watched");
                account = st.Account;
            }
            // A user pressed Y: the ceiling does not stop it, a hold does.
            var env = await RequestAsync(account, action.Method, action.Path, action.Body?.DeepClone(), force: true, ct)
                .ConfigureAwait(false);
            if (env.Ok)
            {
                AfterWrite(slug, serverId, action);
                return new(true, action.Done);
            }

            var message = env.Status switch
            {
                403 when action.ScopeMessage is not null => action.ScopeMessage,
                422 => Json.Str(Json.Get(env.Body, "message")) is { Length: > 0 } m ? m : "Forge refused that (422)",
                _ => env.ErrorMessage,
            };
            // Only a write with no scope message of its own (the deploy) reports on the organization.
            if (action.ScopeMessage is null && !env.Throttled && !env.MissingToken)
            {
                lock (_lock) if (_orgs.TryGetValue(slug, out var st)) st.LastError = message;
                Publish();
            }
            return new(false, message);
        }
        finally
        {
            Interlocked.Exchange(ref _writing, 0);
        }
    }

    private void AfterWrite(string slug, string serverId, ActionSpec action)
    {
        switch (action.Refetch)
        {
            case Refetch.Org:
                RefreshSoon(slug);
                break;
            case Refetch.Sites:
                // What a deploy or a maintenance flip changed rides the sites payload,
                // and under rotation the next sweep is usually looking elsewhere.
                _ = Task.Run(async () =>
                {
                    await FetchServerSitesAsync(slug, serverId, force: true).ConfigureAwait(false);
                    if (action.SettleSites) await SettleAsync(slug, serverId).ConfigureAwait(false);
                });
                lock (_lock)
                    if (_orgs.TryGetValue(slug, out var st))
                        st.NextDueMs = Math.Min(st.NextDueMs, Now + BusyIntervalMs);
                break;
        }
    }

    /// <summary>
    /// <c>maintenance_mode.enabled</c> moves only once Forge has finished on the
    /// box. Look twice more, 8s apart, and stop the moment nothing is flipping.
    /// </summary>
    private async Task SettleAsync(string slug, string serverId)
    {
        for (var i = 0; i < 2; i++)
        {
            await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
            await FetchServerSitesAsync(slug, serverId, force: true).ConfigureAwait(false);
            var flipping = Snapshot.Org(slug)?.SitesOf(serverId).Any(s => s.MaintenanceStatus != "") ?? false;
            if (!flipping) return;
        }
    }

    // ---------------------------------------------------------------- runs

    private RunWatch? _watch;
    private readonly Dictionary<string, long> _lastCommandRunMs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _lastRecipeRunId = new(StringComparer.Ordinal);

    public async Task<(ActionOutcome Outcome, RunWatch? Watch)> RunCommandAsync(string slug, Site site, string command, CancellationToken ct = default)
    {
        var spec = Actions.SiteCommand(slug, site, command);
        if (!spec.Available) return (new(false, spec.Reason), null);
        var sentAt = Now;
        var outcome = await SendActionAsync(slug, site.ServerId, spec, ct).ConfigureAwait(false);
        if (!outcome.Ok) return (outcome, null);
        var text = spec.ArmIntent;
        return (outcome, StartWatch(new RunWatch(this, RunKind.Command, slug, site.ServerId, site.Id, text, "", sentAt)));
    }

    public async Task<(ActionOutcome Outcome, RunWatch? Watch)> RunRecipeAsync(string slug, Server server, Recipe recipe, CancellationToken ct = default)
    {
        var spec = Actions.RecipeRun(slug, server, recipe);
        if (!spec.Available) return (new(false, spec.Reason), null);
        var sentAt = Now;
        var outcome = await SendActionAsync(slug, server.Id, spec, ct).ConfigureAwait(false);
        if (!outcome.Ok) return (outcome, null);
        return (outcome, StartWatch(new RunWatch(this, RunKind.Recipe, slug, server.Id, "", "", recipe.Id, sentAt)));
    }

    /// <summary>One watch slot for the session: the next run displaces the last.</summary>
    private RunWatch StartWatch(RunWatch watch)
    {
        RunWatch? old;
        lock (_lock) { old = _watch; _watch = watch; }
        old?.Stop("A newer run replaced this one");
        watch.Restart();
        return watch;
    }

    internal enum Look { Again, Done, Stop }

    internal async Task<Look> LookAsync(RunWatch w, CancellationToken ct)
    {
        return w.Kind == RunKind.Command ? await LookCommandAsync(w, ct).ConfigureAwait(false)
                                          : await LookRecipeAsync(w, ct).ConfigureAwait(false);
    }

    private async Task<Look> LookCommandAsync(RunWatch w, CancellationToken ct)
    {
        System.Text.Json.Nodes.JsonNode? attrs;
        if (w.RunId is null)
        {
            var (env, error, fatal) = await ReadAsync(w.Org, Paths.CommandList(w.Org, w.ServerId, w.SiteId), ServerViewScope, ct).ConfigureAwait(false);
            if (error is not null) return w.Fail(error, fatal);
            var memo = w.SiteId + "\n" + w.Command;
            long after;
            lock (_lock) after = _lastCommandRunMs.GetValueOrDefault(memo);
            var found = Runs.CommandFrom(env!.Body, w.Command, w.SentAtMs, after);
            if (found is null) { w.Emit(new("waiting for Forge to list the run…", true, false, null, null)); return Look.Again; }
            w.RunId = found.Id;
            lock (_lock) _lastCommandRunMs[memo] = found.MadeMs;
            attrs = found.Attributes;
        }
        else
        {
            var (env, error, fatal) = await ReadAsync(w.Org, Paths.Command(w.Org, w.ServerId, w.SiteId, w.RunId), ServerViewScope, ct).ConfigureAwait(false);
            if (error is not null) return w.Fail(error, fatal);
            attrs = Parse.Attributes(env!.Body);
        }

        var state = Runs.CommandState(attrs);
        if (state.Running) { w.Emit(new(state.Header, true, false, null, null)); return Look.Again; }

        var (outEnv, outError, outFatal) = await ReadAsync(w.Org, Paths.CommandOutput(w.Org, w.ServerId, w.SiteId, w.RunId), ServerViewScope, ct).ConfigureAwait(false);
        if (outError is not null) return w.Fail(outError, outFatal, state.Header);
        var text = Parse.ResourceText(outEnv!.Body, "output") ?? "";
        w.Emit(new(state.Header, false, state.Bad, ForgeText.LogLines(text), null));
        return Look.Done;
    }

    private async Task<Look> LookRecipeAsync(RunWatch w, CancellationToken ct)
    {
        var memo = w.RecipeId + "\n" + w.ServerId;
        if (w.RunId is null)
        {
            long after;
            lock (_lock) after = _lastRecipeRunId.GetValueOrDefault(memo);
            string? cursor = null;
            FoundRun? found = null;
            for (var page = 0; page < Runs.RecipeFindPages && found is null; page++)
            {
                var (env, error, fatal) = await ReadAsync(w.Org, Paths.RecipeRunList(w.Org, w.RecipeId, cursor), RecipeViewScope, ct).ConfigureAwait(false);
                if (error is not null) return w.Fail(error, fatal);
                found = Runs.RecipeRunFrom(env!.Body, w.ServerId, w.SentAtMs, after);
                cursor = Parse.NextCursor(env.Body);
                if (cursor == "") break;
            }
            if (found is null) { w.Emit(new("waiting for Forge to list the run…", true, false, null, null)); return Look.Again; }
            w.RunId = found.Id;
            lock (_lock) _lastRecipeRunId[memo] = long.Parse(found.Id);
        }

        var (showEnv, showError, showFatal) = await ReadAsync(w.Org, Paths.RecipeRunLog(w.Org, w.RecipeId, w.RunId), RecipeViewScope, ct).ConfigureAwait(false);
        if (showError is not null) return w.Fail(showError, showFatal);
        var attrs = Parse.Attributes(showEnv!.Body);
        var state = Runs.RecipeState(attrs);
        if (state.Running) { w.Emit(new(state.Header, true, false, null, null)); return Look.Again; }
        w.Emit(new(state.Header, false, state.Bad, ForgeText.LogLines(Json.Str(Json.Get(attrs, "output"))), null));
        return Look.Done;
    }
}

public enum RunKind { Command, Recipe }

/// <summary>
/// Follows one command or recipe run on a ladder of looks. A refusal does
/// not end it — the run is on the box whatever a look came back with — but
/// a 401/403 stops the ladder, since it would say the same thing every time.
/// <see cref="Restart"/> starts the ladder over.
/// </summary>
public sealed class RunWatch
{
    private readonly ForgeService _service;
    private readonly object _lock = new();
    private CancellationTokenSource? _cts;
    private RunUpdate _latest = new("sent — waiting for Forge…", true, false, null, null);

    internal RunWatch(ForgeService service, RunKind kind, string org, string serverId, string siteId,
        string command, string recipeId, long sentAtMs)
    {
        _service = service;
        Kind = kind;
        Org = org;
        ServerId = serverId;
        SiteId = siteId;
        Command = command;
        RecipeId = recipeId;
        SentAtMs = sentAtMs;
    }

    public RunKind Kind { get; }
    public string Org { get; }
    public string ServerId { get; }
    public string SiteId { get; }
    public string Command { get; }
    public string RecipeId { get; }
    public long SentAtMs { get; }
    internal string? RunId { get; set; }

    public RunUpdate Latest { get { lock (_lock) return _latest; } }

    /// <summary>Raised on a background thread.</summary>
    public event Action<RunUpdate>? Updated;

    internal void Emit(RunUpdate update)
    {
        lock (_lock) _latest = update;
        Updated?.Invoke(update);
    }

    internal ForgeService.Look Fail(string message, bool fatal, string? header = null)
    {
        var last = Latest;
        Emit(last with { Header = header ?? last.Header, Message = message });
        return fatal ? ForgeService.Look.Stop : ForgeService.Look.Again;
    }

    public void Restart()
    {
        CancellationTokenSource cts;
        lock (_lock)
        {
            _cts?.Cancel();
            _cts = cts = new CancellationTokenSource();
        }
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    public void Stop(string? why = null)
    {
        lock (_lock) _cts?.Cancel();
        if (why is not null) Emit(Latest with { Running = false, Message = why });
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        try
        {
            foreach (var seconds in Runs.PollLadder)
            {
                await Task.Delay(TimeSpan.FromSeconds(seconds), ct).ConfigureAwait(false);
                var look = await _service.LookAsync(this, ct).ConfigureAwait(false);
                if (look != ForgeService.Look.Again) return;
            }
            Emit(Latest with { Message = "Stopped looking after ten tries — press R to look again" });
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e)
        {
            Emit(Latest with { Message = e.Message });
        }
    }
}
