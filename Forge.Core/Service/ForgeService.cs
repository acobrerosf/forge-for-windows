using System.Text.Json.Nodes;
using Forge.Core.Api;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Setup;

namespace Forge.Core.Service;

/// <summary>
/// One instance per session: polling, the rate ledger, deploy notifications,
/// on-demand reads and writes. Port of omarchy-forge's Service.qml.
///
/// Cost per tick per organization is 2 requests — the server list, then one
/// window of the organization's site list. Past 150 sites the site sweep
/// rotates: each tick walks the next window and the walk wraps around.
/// </summary>
public sealed partial class ForgeService : IAsyncDisposable
{
    public const int MaxPages = 5;
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(25);
    private const long BusyIntervalMs = 15_000;
    private const long ServerSitesDebounceMs = 15_000;

    private readonly IForgeClient _client;
    private readonly ICredentialStore _credentials;
    private readonly StateStore _store;
    private readonly TimeProvider _time;
    private readonly RateBudget _budget = new();

    private readonly object _lock = new();
    private readonly Dictionary<string, OrgState> _orgs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _hasToken = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _serverSitesFetchedMs = new(StringComparer.Ordinal);

    /// <summary>One request on the wire at a time for the whole session.</summary>
    private readonly SemaphoreSlim _wire = new(1, 1);
    private readonly SemaphoreSlim _wake = new(0);
    private int _writing;

    private CancellationTokenSource? _cts;
    private Task? _loop;
    private ForgeSnapshot _snapshot = ForgeSnapshot.Empty;

    public ForgeService(IForgeClient client, ICredentialStore credentials, StateStore store, TimeProvider? time = null)
    {
        _client = client;
        _credentials = credentials;
        _store = store;
        _time = time ?? TimeProvider.System;
        Setup = SetupState.Empty;
    }

    public SetupState Setup { get; private set; }

    public string SetupPath => _store.Path;

    public ForgeSnapshot Snapshot
    {
        get { lock (_lock) return _snapshot; }
    }

    /// <summary>Raised on a background thread whenever the snapshot changes.</summary>
    public event Action? Changed;

    /// <summary>A deployment finished or failed. Raised on a background thread.</summary>
    public event Action<DeploymentNotice>? DeploymentChanged;

    internal long Now => _time.GetUtcNow().ToUnixTimeMilliseconds();

    // ---------------------------------------------------------- lifecycle

    public void Start()
    {
        Reconcile(_store.Load());
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await TickAsync(ct).ConfigureAwait(false);
                await _wake.WaitAsync(TickInterval, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception)
            {
                // A bug in one tick must not stop polling for the rest of the session.
                await Task.Delay(TickInterval, ct).ConfigureAwait(false);
            }
        }
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        List<string> due;
        lock (_lock)
        {
            var now = Now;
            due = Setup.Organizations.Select(o => o.Slug)
                .Where(s => _orgs.TryGetValue(s, out var st) && !st.Refreshing && st.NextDueMs <= now)
                .ToList();
        }
        foreach (var slug in due) await RefreshOrgAsync(slug, ct).ConfigureAwait(false);
    }

    /// <summary>Pulls every organization's next look forward. A rate-limit hold still applies.</summary>
    public void RefreshNow()
    {
        lock (_lock)
            foreach (var st in _orgs.Values) st.NextDueMs = 0;
        _wake.Release();
    }

    private void RefreshSoon(string slug)
    {
        lock (_lock)
            if (_orgs.TryGetValue(slug, out var st)) st.NextDueMs = 0;
        _wake.Release();
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null) return;
        await _cts.CancelAsync().ConfigureAwait(false);
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
        }
        _cts.Dispose();
        lock (_lock) _watch?.Stop();
    }

    // -------------------------------------------------------------- setup

    /// <summary>Which organizations are watched decides which account each is polled through.</summary>
    internal void Reconcile(SetupState setup)
    {
        var accounts = setup.Accounts.Select(a => a.Name)
            .Concat(setup.Organizations.Select(o => o.Account)).Distinct().ToList();
        var tokens = accounts.ToDictionary(a => a, a => ReadToken(a) is not null);

        lock (_lock)
        {
            Setup = setup;
            foreach (var org in setup.Organizations)
            {
                if (!_orgs.TryGetValue(org.Slug, out var st))
                    _orgs[org.Slug] = new OrgState { Account = org.Account };
                else if (st.Account != org.Account)
                    _orgs[org.Slug] = new OrgState { Account = org.Account };
            }
            foreach (var gone in _orgs.Keys.Where(k => setup.Org(k) is null).ToList()) _orgs.Remove(gone);
            _hasToken.Clear();
            foreach (var (account, has) in tokens) _hasToken[account] = has;
        }
        Publish();
    }

    public void ApplySetup(SetupState setup)
    {
        _store.Save(setup);
        Reconcile(setup);
        RefreshNow();
    }

    public void UpdateSettings(AppSettings settings) => ApplySetup(Setup with { Settings = settings.Normalized() });

    /// <summary>Checks a token before it is stored: who it belongs to and which organizations it can see.</summary>
    public async Task<(ForgeUser? User, IReadOnlyList<Organization> Orgs, string? Error)> VerifyTokenAsync(string token, CancellationToken ct = default)
    {
        token = token.Trim();
        if (token == "") return (null, [], "Paste a token first");
        var userEnv = await _client.SendAsync("GET", Paths.User(), token, null, ct).ConfigureAwait(false);
        if (!userEnv.Ok)
            return (null, [], userEnv.Status switch
            {
                401 => "Forge rejected that token",
                403 => Scopes.MissingMessage(Scopes.UserView),
                _ => userEnv.ErrorMessage,
            });
        var user = Parse.User(userEnv.Body);

        var orgs = new List<Organization>();
        string? cursor = null;
        for (var page = 0; page < 10; page++)
        {
            var env = await _client.SendAsync("GET", Paths.Orgs(cursor), token, null, ct).ConfigureAwait(false);
            if (!env.Ok)
                return (user, orgs, env.Status == 403
                    ? Scopes.MissingMessage(Scopes.OrganizationView)
                    : "Could not list organizations: " + env.ErrorMessage);
            orgs.AddRange(Parse.Organizations(env.Body));
            cursor = Parse.NextCursor(env.Body);
            if (cursor == "") break;
        }
        return (user, orgs, null);
    }

    public string SaveAccount(string? name, string token, ForgeUser user, IEnumerable<OrgEntry> watched)
    {
        var next = Setup.WithAccount(name, user, watched, out var account);
        _credentials.Write(account, token.Trim());
        ApplySetup(next);
        return account;
    }

    public void RemoveAccount(string name)
    {
        _credentials.Delete(name);
        ApplySetup(Setup.WithoutAccount(name));
    }

    public bool HasToken(string account)
    {
        lock (_lock) return _hasToken.GetValueOrDefault(account);
    }

    private string? ReadToken(string account)
    {
        try { return _credentials.Read(account); }
        catch (Exception) { return null; }
    }

    // ------------------------------------------------------------ requests

    private string BucketOf(string account)
    {
        lock (_lock) return Setup.BudgetBucket(account);
    }

    /// <summary>
    /// The one road to the wire. Reads the token, charges the bucket, sends,
    /// and turns a 429 into a hold on the whole bucket — every organization
    /// behind that Forge user waits together.
    /// </summary>
    private async Task<ForgeEnvelope> RequestAsync(string account, string method, string path, JsonNode? body, bool force, CancellationToken ct)
    {
        var token = ReadToken(account);
        lock (_lock) _hasToken[account] = token is not null;
        if (token is null) return ForgeEnvelope.NoToken(account);

        var bucket = BucketOf(account);
        var now = Now;
        var held = _budget.HeldUntil(bucket, now);
        if (held > 0) return ForgeEnvelope.Throttle(HeldMessage(held, now));
        if (!_budget.TryCharge(bucket, now, force))
            return ForgeEnvelope.Throttle("Holding back to stay under Forge's rate limit — try again in a moment");

        await _wire.WaitAsync(ct).ConfigureAwait(false);
        ForgeEnvelope env;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            env = await _client.SendAsync(method, path, token, body, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            env = ForgeEnvelope.Failure("Forge did not answer in time");
        }
        finally
        {
            _wire.Release();
        }

        if (env.Status == 429) _budget.Hold(bucket, env.BackoffUntilMs(Now));
        return env;
    }

    private static string HeldMessage(long heldUntil, long now) =>
        $"Rate limited — try again in {Math.Max(1, (heldUntil - now + 999) / 1000)}s";

    private async Task<(List<JsonNode> Pages, ForgeEnvelope? Error, string Cursor)> PagedAsync(
        string account, Func<string?, string> path, string? startCursor, bool forceFirst, CancellationToken ct)
    {
        var pages = new List<JsonNode>();
        var cursor = startCursor;
        var bucket = BucketOf(account);
        for (var i = 0; i < MaxPages; i++)
        {
            // Continuations are discretionary: the ceiling and the page cap both stop them.
            if (i > 0 && _budget.Headroom(bucket, Now) < 1) break;
            var env = await RequestAsync(account, "GET", path(cursor), null, force: i == 0 && forceFirst, ct).ConfigureAwait(false);
            if (!env.Ok || env.Body is null) return (pages, env.Ok ? ForgeEnvelope.Failure("Forge sent an empty answer") : env, "");
            pages.Add(env.Body);
            cursor = Parse.NextCursor(env.Body);
            if (cursor == "") break;
        }
        return (pages, null, cursor ?? "");
    }

    // ------------------------------------------------------------- refresh

    internal async Task RefreshOrgAsync(string slug, CancellationToken ct)
    {
        OrgState st;
        string account;
        lock (_lock)
        {
            if (!_orgs.TryGetValue(slug, out st!) || st.Refreshing) return;
            st.Refreshing = true;
            account = st.Account;
        }
        Publish();

        var notices = new List<DeploymentNotice>();
        try
        {
            var bucket = BucketOf(account);
            var held = _budget.HeldUntil(bucket, Now);
            if (held > 0)
            {
                lock (_lock) { st.LastError = HeldMessage(held, Now); st.NextDueMs = held; }
                return;
            }

            // The server list is never dropped: the tray icon depends on it.
            var (serverPages, serverError, serverCursor) =
                await PagedAsync(account, c => Paths.Servers(slug, c), null, forceFirst: true, ct).ConfigureAwait(false);
            if (serverError is not null)
            {
                lock (_lock)
                {
                    // A missing token is the account's problem, reported once for all its organizations.
                    st.LastError = serverError.MissingToken ? "" : serverError.ErrorMessage;
                    if (serverError.Status == 429) st.NextDueMs = serverError.BackoffUntilMs(Now);
                }
                return;
            }
            var servers = serverPages.SelectMany(Parse.Servers).ToList();
            var notes = new List<string>();
            if (serverCursor != "") notes.Add($"Showing the first {servers.Count} servers");

            await SweepSitesAsync(slug, st, account, servers, notes, notices, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e)
        {
            lock (_lock) st.LastError = e.Message;
        }
        finally
        {
            lock (_lock)
            {
                st.Refreshing = false;
                st.LastRefreshMs = Now;
                var interval = Setup.Settings.RefreshIntervalSec * 1000L;
                // While a deploy runs, look sooner so the notification lands close to the event.
                var busy = st.SitesByServer.Values.SelectMany(l => l).Any(s => Status.SiteTone(s) == Tone.Busy);
                var next = Now + (busy ? Math.Min(interval, BusyIntervalMs) : interval);
                st.NextDueMs = Math.Max(st.NextDueMs, next);
            }
            Publish();
            foreach (var n in notices) DeploymentChanged?.Invoke(n);
        }
    }

    private async Task SweepSitesAsync(string slug, OrgState st, string account, List<Server> servers,
        List<string> notes, List<DeploymentNotice> notices, CancellationToken ct)
    {
        var bucket = BucketOf(account);
        string cursor;
        lock (_lock) cursor = st.SiteCursor;

        var observed = new List<Site>();
        ForgeEnvelope? error = null;
        var wrapped = false;
        var skipped = false;

        for (var page = 0; page < MaxPages; page++)
        {
            if (_budget.Headroom(bucket, Now) < 1)
            {
                skipped = page == 0;
                break;
            }
            var env = await RequestAsync(account, "GET", Paths.Sites(slug, cursor == "" ? null : cursor), null, false, ct)
                .ConfigureAwait(false);
            if (env.Throttled) { skipped = page == 0; break; }
            if (!env.Ok) { error = env; break; }
            var sites = Parse.Sites(env.Body);
            if (Parse.SitesLinkageMissing(env.Body, sites))
            {
                error = ForgeEnvelope.Failure("Forge sent sites without their server — can this token view servers?");
                break;
            }
            observed.AddRange(sites);
            cursor = Parse.NextCursor(env.Body);
            if (cursor == "") { wrapped = true; break; }
        }

        lock (_lock)
        {
            Dictionary<string, IReadOnlyList<Site>> published;
            var complete = false;
            if (error is null && wrapped)
            {
                // The accumulated sweep plus this window is the whole organization.
                var all = SiteGrouping.MergeSweep(st.SweepSites, observed);
                published = SiteGrouping.Group(all, servers);
                st.SiteCursor = "";
                st.SweepSites = [];
                complete = true;
            }
            else
            {
                published = SiteGrouping.Merge(st.SitesByServer, observed, servers);
                if (error is not null)
                {
                    // The cursor may be the very thing that failed.
                    st.SiteCursor = "";
                    st.SweepSites = [];
                }
                else if (!skipped)
                {
                    st.SiteCursor = cursor;
                    st.SweepSites = SiteGrouping.MergeSweep(st.SweepSites, observed);
                    notes.Add("Over 150 sites — checking them in rotation");
                }
            }
            if (skipped) notes.Add("Sites skipped this round to stay under Forge's rate limit");

            Announce(slug, st, observed, published, complete, notices);
            st.Servers = servers;
            st.SitesByServer = published;
            st.Note = string.Join(" · ", notes);
            st.LastError = error is null || error.MissingToken ? "" : error.ErrorMessage;
            if (error?.Status == 429) st.NextDueMs = error.BackoffUntilMs(Now);
        }
    }

    /// <summary>
    /// <see cref="OrgState.LastStatus"/> holds each site's status as of its
    /// last observation. A site with no entry has nothing to differ from, so an
    /// already-failed one stays quiet when the app starts.
    /// </summary>
    private void Announce(string slug, OrgState st, IReadOnlyList<Site> observed,
        IReadOnlyDictionary<string, IReadOnlyList<Site>> published, bool complete, List<DeploymentNotice> notices)
    {
        Dictionary<string, string> next;
        if (complete)
        {
            next = published.Values.SelectMany(l => l).ToDictionary(s => s.Key, s => s.DeploymentStatus, StringComparer.Ordinal);
        }
        else
        {
            next = new Dictionary<string, string>(st.LastStatus, StringComparer.Ordinal);
            foreach (var s in observed) next[s.Key] = s.DeploymentStatus;
        }

        if (Setup.Settings.NotifyDeployments)
        {
            foreach (var site in observed)
            {
                if (!published.ContainsKey(site.ServerId)) continue;
                if (!st.LastStatus.TryGetValue(site.Key, out var before) || before == site.DeploymentStatus) continue;
                if (NoticeFor(slug, site) is { } notice) notices.Add(notice);
            }
        }
        st.LastStatus = next;
    }

    private DeploymentNotice? NoticeFor(string slug, Site site)
    {
        // Two organizations can hold sites with the same name; say which once there is more than one.
        var label = Setup.OrgLabel(slug);
        var where = Setup.Organizations.Count > 1 ? " · " + label : "";
        var tone = Status.DeploymentTone(site.DeploymentStatus);
        if (tone == Tone.Bad)
            return new DeploymentNotice(slug, label, site, true, site.Name,
                "Deployment " + Status.DeploymentLabel(site.DeploymentStatus) + where, ForgeText.ExternalUrl(site.Url));
        if (tone == Tone.Ok && site.DeploymentStatus.Equals("finished", StringComparison.OrdinalIgnoreCase))
            return new DeploymentNotice(slug, label, site, false, site.Name,
                "Deployed" + (site.CommitHash != "" ? " · " + site.CommitHash : "") + where, ForgeText.ExternalUrl(site.Url));
        return null;
    }

    /// <summary>
    /// One server's sites, fresh — for unfolding a row and right after a
    /// deploy. Debounced per server unless forced; refused under a hold or at
    /// the ceiling, because the rotation reaches the server anyway.
    /// </summary>
    public async Task FetchServerSitesAsync(string slug, string serverId, bool force = false, CancellationToken ct = default)
    {
        string account;
        var key = slug + ":" + serverId;
        lock (_lock)
        {
            if (!_orgs.TryGetValue(slug, out var st)) return;
            account = st.Account;
            if (!force && Now - _serverSitesFetchedMs.GetValueOrDefault(key) < ServerSitesDebounceMs) return;
            _serverSitesFetchedMs[key] = Now;
        }
        var bucket = BucketOf(account);
        if (_budget.HeldUntil(bucket, Now) > 0 || _budget.Headroom(bucket, Now) < 1) return;

        var (pages, error, _) = await PagedAsync(account, c => Paths.ServerSites(slug, serverId, c), null, false, ct)
            .ConfigureAwait(false);
        if (error is not null) return;
        var sites = pages.SelectMany(Parse.Sites).ToList();

        var notices = new List<DeploymentNotice>();
        lock (_lock)
        {
            if (!_orgs.TryGetValue(slug, out var st)) return;
            var published = SiteGrouping.Replace(st.SitesByServer, serverId, sites, st.Servers);
            st.SweepSites = st.SiteCursor == "" ? st.SweepSites : SiteGrouping.MergeSweep(st.SweepSites, sites);
            Announce(slug, st, sites, published, complete: false, notices);
            st.SitesByServer = published;
        }
        Publish();
        foreach (var n in notices) DeploymentChanged?.Invoke(n);
    }

    // ------------------------------------------------------------ snapshot

    private void Publish()
    {
        lock (_lock) _snapshot = BuildSnapshot();
        Changed?.Invoke();
    }

    private string AccountErrorLocked(string account)
    {
        if (_hasToken.GetValueOrDefault(account, true)) return "";
        var label = Setup.Account(account)?.Label ?? account;
        return $"No token for {label} — open Settings to add one";
    }

    private ForgeSnapshot BuildSnapshot()
    {
        var views = new List<OrgView>();
        foreach (var org in Setup.Organizations)
        {
            if (!_orgs.TryGetValue(org.Slug, out var st)) continue;
            var accountError = AccountErrorLocked(st.Account);
            var health = Status.HealthOf(st.Servers, st.SitesByServer.Values, accountError != "", st.LastError != "");
            views.Add(new OrgView(org.Slug, org.DisplayName, st.Account, st.Servers, st.SitesByServer,
                st.Refreshing, st.LastError, accountError, st.Note, st.LastRefreshMs, health));
        }

        if (views.Count == 0) return ForgeSnapshot.Empty;
        var overall = views.Aggregate(Health.Ok, (worst, v) => Status.Worse(worst, v.Health));
        return new ForgeSnapshot(views, overall, false, Summarize(views));
    }

    private static string Summarize(IReadOnlyList<OrgView> views)
    {
        var servers = views.SelectMany(v => v.Servers).ToList();
        var sites = views.SelectMany(v => v.SitesByServer.Values.SelectMany(l => l)).ToList();
        var parts = new List<string>();
        var down = servers.Count(s => Status.ServerTone(s.State) == Tone.Bad);
        var failed = sites.Count(s => Status.SiteTone(s) == Tone.Bad);
        var busy = sites.Count(s => Status.SiteTone(s) == Tone.Busy);
        var parked = sites.Count(s => Status.SiteTone(s) == Tone.Warn);
        if (down > 0) parts.Add(ForgeText.Pluralize(down, "server") + " unreachable");
        if (failed > 0) parts.Add(ForgeText.Pluralize(failed, "deploy") + " failed");
        if (busy > 0) parts.Add(ForgeText.Pluralize(busy, "deploy") + " running");
        if (parked > 0) parts.Add(ForgeText.Pluralize(parked, "site") + " in maintenance");
        if (views.Any(v => v.AccountError != "")) parts.Add("token missing");
        else if (views.Any(v => v.LastError != "")) parts.Add("a request failed");
        if (parts.Count == 0)
            parts.Add($"{ForgeText.Pluralize(servers.Count, "server")}, {ForgeText.Pluralize(sites.Count, "site")} — all quiet");
        return "Forge — " + string.Join(" · ", parts);
    }
}

internal sealed class OrgState
{
    public string Account = "";
    public IReadOnlyList<Server> Servers = [];
    public IReadOnlyDictionary<string, IReadOnlyList<Site>> SitesByServer = new Dictionary<string, IReadOnlyList<Site>>();
    public bool Refreshing;
    public string LastError = "";
    public string Note = "";
    public long LastRefreshMs;
    public long NextDueMs;
    /// <summary>Where the site walk resumes next tick ("" = start over).</summary>
    public string SiteCursor = "";
    /// <summary>Everything observed since the walk last started; the wrap rebuilds the organization from it.</summary>
    public List<Site> SweepSites = [];
    public Dictionary<string, string> LastStatus = new(StringComparer.Ordinal);
}
