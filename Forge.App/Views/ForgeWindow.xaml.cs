using System.Runtime.InteropServices;
using Forge.App.Services;
using Forge.Core.Model;
using Forge.Core.Service;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Foundation;
using Windows.Graphics;
using Windows.System;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;

namespace Forge.App.Views;

/// <summary>
/// The expanded window: everything the flyout shows, with room to read. Servers
/// and sites on the left; the chosen one on the right, with its logs in tabs.
/// Writes ask in the same dialog the flyout uses.
/// </summary>
public sealed partial class ForgeWindow : Window
{
    private readonly ForgeService _service;
    private readonly Action _openSettings;
    private readonly DispatcherQueueTimer _flashTimer;
    private readonly DispatcherQueueTimer _clock;

    // What is chosen: a site when _siteKey is set, otherwise a server when _serverId is.
    private string _org = "";
    private string _siteKey = "";
    private string _serverId = "";
    private string _siteTab = "overview";
    private string _serverTab = "services";
    private string _query = "";
    private string _orgFilter = "";

    private int _loadGen;
    private long _logLoadedMs;
    private Tone? _followTone;
    private bool _syncing;
    private bool _sending;
    private bool _closed;
    private bool _closeAnyway;
    private bool _choseOnce;
    private RunWatch? _commandWatch;
    private readonly System.Collections.ObjectModel.ObservableCollection<ListEntry> _navItems = [];
    // What the facts card and the service cards last drew, so a report with nothing new leaves them alone.
    private string _factsShown = "";
    private string _servicesShown = "";

    public ForgeWindow(ForgeService service, Action openSettings)
    {
        _service = service;
        _openSettings = openSettings;
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        Title = AppTitle.Text = "Forge for Windows" + Forge.Core.Setup.AppProfile.Label;
        SystemBackdrop = new MicaBackdrop();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "forge.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
            TitleIcon.Source = new BitmapImage(new Uri(icon));
        }

        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = Math.Min((int)(1280 * scale), work.Width - 48);
        var h = Math.Min((int)(820 * scale), work.Height - 48);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - w) / 2, work.Y + (work.Height - h) / 2, w, h));
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.PreferredMinimumWidth = (int)(900 * scale);
            presenter.PreferredMinimumHeight = (int)(560 * scale);
        }

        TitleBarArea.SizeChanged += (_, _) => UpdateTitleBarRegions();
        Root.Loaded += (_, _) => UpdateTitleBarRegions();

        CommandOutput.CanReload = false;
        CommandOutput.Reported += Flash;

        // Every list-and-output section works the same way: a fresh read opens an item, picking one
        // reads its output, Reload re-reads the output, "Load more" pages on.
        SiteDeployments.Picked += e => { if (e.Payload is Deployment d) LoadDeploymentLog(d); };
        SiteDeployments.ListReloadRequested += () => LoadDeployments(null);
        SiteDeployments.MoreRequested += () => LoadDeployments(SiteDeployments.Next);
        SiteLogs.Picked += e => { if (e.Payload is string kind) LoadSiteLog(kind); };
        SiteLogs.ListReloadRequested += LoadSiteLogKinds;
        SiteLogs.Log.ClearRequested += ClearSiteLog;
        SiteJobs.Picked += e => { if (e.Payload is ScheduledJob j) LoadJobOutput(SiteJobs, j, site: true); };
        SiteJobs.ListReloadRequested += () => LoadJobs(SiteJobs, null, site: true);
        SiteJobs.MoreRequested += () => LoadJobs(SiteJobs, SiteJobs.Next, site: true);
        ServerEvents.Picked += e => { if (e.Payload is ForgeEvent ev) LoadEventOutput(ev); };
        ServerEvents.ListReloadRequested += () => LoadEvents(null);
        ServerEvents.MoreRequested += () => LoadEvents(ServerEvents.Next);
        ServerLogs.Picked += e => { if (e.Payload is string key) LoadServerLog(key); };
        ServerLogs.ListReloadRequested += LoadServerLogKinds;
        ServerLogs.Log.ClearRequested += ClearServerLog;
        ServerProcesses.Picked += e => { if (e.Payload is BackgroundProcess p) LoadProcessLog(p); };
        ServerProcesses.ListReloadRequested += () => LoadProcesses(null);
        ServerProcesses.MoreRequested += () => LoadProcesses(ServerProcesses.Next);
        ServerProcesses.ActionRequested += RestartProcess;
        ServerJobs.Picked += e => { if (e.Payload is ScheduledJob j) LoadJobOutput(ServerJobs, j, site: false); };
        ServerJobs.ListReloadRequested += () => LoadJobs(ServerJobs, null, site: false);
        ServerJobs.MoreRequested += () => LoadJobs(ServerJobs, ServerJobs.Next, site: false);
        foreach (var pane in new[] { SiteDeployments, SiteLogs, SiteJobs, ServerEvents, ServerLogs, ServerProcesses, ServerJobs })
            pane.Log.Reported += Flash;
        SiteConfig.Picked += e => { if (e.Payload is ConfigFile f) LoadConfig(f); };
        SiteConfig.Editor.ReloadRequested += SiteConfig.Reopen;
        SiteConfig.Editor.SaveRequested += SaveConfig;
        SiteConfig.Editor.Reported += Flash;
        // Only a close from the title bar or Alt+F4 comes here; quitting from the tray closes regardless.
        AppWindow.Closing += async (_, args) =>
        {
            if (_closeAnyway || !SiteConfig.IsDirty) return;
            args.Cancel = true;
            if (!await MayLeaveConfigAsync()) return;
            _closeAnyway = true;
            Close();
        };

        _flashTimer = DispatcherQueue.CreateTimer();
        _flashTimer.Interval = TimeSpan.FromSeconds(6);
        _flashTimer.IsRepeating = false;
        _flashTimer.Tick += (_, _) => FlashBar.IsOpen = false;
        _clock = DispatcherQueue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(30);
        _clock.Tick += (_, _) => Render();
        _clock.Start();

        Closed += (_, _) =>
        {
            _closed = true;
            _clock.Stop();
            _flashTimer.Stop();
            _commandWatch?.Stop();
            SiteConfig.Reset();
        };

        _syncing = true;
        SiteTabs.SelectedItem = SiteTabs.Items[0];
        ServerTabs.SelectedItem = ServerTabs.Items[0];
        NavList.ItemsSource = _navItems;
        _syncing = false;
        Render();
    }

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// Shows the window, on a given site when one is named (from the flyout's current site, say),
    /// and on one of its tabs when <paramref name="tab"/> names one (the flyout's "Config files…").
    /// </summary>
    public void ShowAndFocus(string? org = null, string? siteKey = null, string? tab = null)
    {
        AppWindow.Show();
        Activate();
        if (!string.IsNullOrEmpty(org) && !string.IsNullOrEmpty(siteKey)) GoToSite(org, siteKey, tab);
        else if (!_choseOnce) ChooseSomethingUseful();
        _choseOnce = true;
    }

    /// <summary>Moves to a site, asking first when that would drop an unsaved config edit.</summary>
    private async void GoToSite(string org, string siteKey, string? tab = null)
    {
        var same = _org == org && _siteKey == siteKey;
        if (!same && !await MayLeaveConfigAsync()) return;
        if (!same) SelectSite(org, siteKey, tab);
        else if (tab is not null && tab != _siteTab) ShowSiteTab(tab);
    }

    /// <summary>True when nothing unsaved would be lost, or the reader chose to drop it.</summary>
    private async Task<bool> MayLeaveConfigAsync()
    {
        if (!SiteConfig.IsDirty) return true;
        if (!await WriteConfirm.DiscardAsync(Root.XamlRoot, Root.ActualTheme, SiteConfig.Editor.What)) return false;
        SiteConfig.Editor.EndEdit();
        return true;
    }

    public void OnServiceChanged()
    {
        if (_closed) return;
        Render();
        FollowDeployLog();
    }

    /// <summary>Opening on nothing wastes the space: start on whatever needs looking at, if anything does.</summary>
    private void ChooseSomethingUseful()
    {
        foreach (var tone in new[] { Tone.Bad, Tone.Busy })
            foreach (var org in _service.Snapshot.Orgs)
                foreach (var site in org.Servers.SelectMany(s => org.SitesOf(s.Id)))
                    if (Status.SiteTone(site) == tone)
                    {
                        SelectSite(org.Slug, site.Key);
                        return;
                    }
    }

    // ------------------------------------------------------------ title bar

    /// <summary>The search box sits in the title bar, so it must be carved out of the drag area to take clicks.</summary>
    private void UpdateTitleBarRegions()
    {
        if (Root.XamlRoot is null) return;
        var scale = Root.XamlRoot.RasterizationScale;
        CaptionInset.Width = new GridLength(AppWindow.TitleBar.RightInset / scale);
        var box = SearchBox.TransformToVisual(null).TransformBounds(new Rect(0, 0, SearchBox.ActualWidth, SearchBox.ActualHeight));
        var rect = new RectInt32((int)(box.X * scale), (int)(box.Y * scale), (int)(box.Width * scale), (int)(box.Height * scale));
        InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, [rect]);
    }

    // --------------------------------------------------------------- render

    private void Render()
    {
        var snap = _service.Snapshot;
        UpdatedText.Text = UpdatedLine(snap);
        RenderNav(snap);
        RenderPage();
    }

    private static string UpdatedLine(ForgeSnapshot snap)
    {
        if (snap.NeedsSetup) return "Not set up yet";
        if (snap.Orgs.Any(o => o.Refreshing)) return "Refreshing…";
        var last = snap.Orgs.Count == 0 ? 0 : snap.Orgs.Max(o => o.LastRefreshMs);
        return last > 0 ? "Updated " + ForgeText.RelativeMs(last, NowMs) : "Loading…";
    }

    private static string Capitalize(string s) => s.Length < 2 ? s.ToUpperInvariant() : char.ToUpperInvariant(s[0]) + s[1..];

    private static string SiteLine(Site site)
    {
        var parts = new List<string>();
        if (site.Branch != "") parts.Add(site.Branch);
        if (site.CommitHash != "") parts.Add((site.CommitHash + " " + site.CommitMessage).Trim());
        if (parts.Count == 0 && site.RepoUrl == "") parts.Add("no repository");
        return string.Join(" · ", parts);
    }

    private static string SiteEntryKey(string org, string key) => $"site|{org}|{key}";
    private static string ServerEntryKey(string org, string id) => $"server|{org}|{id}";

    private void RenderNav(ForgeSnapshot snap)
    {
        SyncOrgBar(snap);
        var entries = new List<ListEntry>();
        var now = NowMs;
        var query = _query.Trim();
        bool Matches(string name) => query == "" || name.Contains(query, StringComparison.OrdinalIgnoreCase);
        var orgs = snap.Orgs.Where(o => _orgFilter == "" || o.Slug == _orgFilter).ToList();

        foreach (var org in orgs)
        {
            var orgEntries = new List<ListEntry>();
            foreach (var server in org.Servers)
            {
                var sites = org.SitesOf(server.Id);
                if (query != "" && !Matches(server.Name))
                {
                    sites = sites.Where(s => Matches(s.Name)).ToList();
                    if (sites.Count == 0) continue;
                }
                orgEntries.Add(new ListEntry
                {
                    Key = ServerEntryKey(org.Slug, server.Id), Kind = EntryKind.Server, Org = org.Slug, Title = server.Name,
                    Detail = Status.ServerMeta(server),
                    Status = server.State == ServerState.Ready ? "" : Capitalize(Status.ServerStateLabel(server.State)),
                    Tone = Status.ServerTone(server.State), Payload = server,
                });
                foreach (var site in sites)
                {
                    var st = Status.SiteStatus(site);
                    orgEntries.Add(new ListEntry
                    {
                        Key = SiteEntryKey(org.Slug, site.Key), Kind = EntryKind.Site, Org = org.Slug, Title = site.Name,
                        Detail = SiteLine(site), Status = Capitalize(st.Label),
                        Time = st.Timed ? ForgeText.RelativeTime(site.DeployedAt, now) : "", Tone = st.Tone, Payload = site,
                    });
                }
            }
            if (orgEntries.Count == 0) continue;
            if (orgs.Count > 1) entries.Add(new ListEntry { Key = "org|" + org.Slug, Kind = EntryKind.Org, Org = org.Slug, Title = org.Label.ToUpperInvariant() });
            entries.AddRange(orgEntries);
        }

        _syncing = true;
        try
        {
            ListSync.Apply(_navItems, entries, e => e.Key, Looks);
            var current = _siteKey != "" ? SiteEntryKey(_org, _siteKey) : _serverId != "" ? ServerEntryKey(_org, _serverId) : "";
            var selected = _navItems.FirstOrDefault(e => e.Key == current);
            if (NavList.SelectedItem != selected) NavList.SelectedItem = selected;
        }
        finally
        {
            _syncing = false;
        }
    }

    private static string Looks(ListEntry e) => $"{e.Kind}|{e.Title}|{e.Detail}|{e.Status}|{e.Time}|{e.Tone}";

    private void SyncOrgBar(ForgeSnapshot snap)
    {
        var wanted = new List<(string Slug, string Label)> { ("", "All") };
        wanted.AddRange(snap.Orgs.Select(o => (o.Slug, o.Label)));
        OrgScroll.Visibility = snap.Orgs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_orgFilter != "" && snap.Org(_orgFilter) is null) _orgFilter = "";
        var current = OrgBar.Items.Select(i => (i.Tag as string ?? "", i.Text)).ToList();
        _syncing = true;
        try
        {
            if (!current.SequenceEqual(wanted))
            {
                OrgBar.Items.Clear();
                foreach (var (slug, label) in wanted) OrgBar.Items.Add(new SelectorBarItem { Text = label, Tag = slug });
            }
            var selected = OrgBar.Items.FirstOrDefault(i => (i.Tag as string ?? "") == _orgFilter);
            if (selected is not null && OrgBar.SelectedItem != selected) OrgBar.SelectedItem = selected;
        }
        finally
        {
            _syncing = false;
        }
    }

    private Site? CurrentSite() => _siteKey == "" ? null : _service.Snapshot.Org(_org)?.SiteByKey(_siteKey);

    private Server? CurrentServer()
    {
        var org = _service.Snapshot.Org(_org);
        if (org is null) return null;
        if (_siteKey != "") return org.SiteByKey(_siteKey) is { } site ? org.ServerById(site.ServerId) : null;
        return _serverId == "" ? null : org.ServerById(_serverId);
    }

    private void RenderPage()
    {
        var site = CurrentSite();
        var server = CurrentServer();
        SitePage.Visibility = site is not null ? Visibility.Visible : Visibility.Collapsed;
        ServerPage.Visibility = site is null && server is not null ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Visibility = site is null && server is null ? Visibility.Visible : Visibility.Collapsed;
        if (site is not null) RenderSite(site, server);
        else if (server is not null) RenderServer(server);
        else if (_siteKey != "" || _serverId != "")
        {
            EmptyTitle.Text = "No longer listed";
            EmptyText.Text = "It may have been removed in Forge, or its organization is no longer watched.";
        }
        else if (_service.Snapshot.NeedsSetup)
        {
            EmptyTitle.Text = "Not set up yet";
            EmptyText.Text = "Add a Forge API token in Settings to start watching your servers.";
        }
        else
        {
            EmptyTitle.Text = "Pick a site or a server";
            EmptyText.Text = "Its logs, actions and details open here.";
        }
    }

    private static void SetDot(Microsoft.UI.Xaml.Shapes.Ellipse dot, Tone tone)
    {
        dot.Fill = tone == Tone.Warn ? Palette.Transparent : Palette.For(tone);
        dot.Stroke = tone == Tone.Warn ? Palette.For(tone) : Palette.Transparent;
    }

    // ----------------------------------------------------------------- site

    private void SelectSite(string org, string siteKey, string? tab = null)
    {
        if (_org == org && _siteKey == siteKey) return;
        _org = org;
        _siteKey = siteKey;
        _serverId = "";
        _commandWatch?.Stop();
        _commandWatch = null;
        CommandOutput.Clear();
        CommandOutput.Visibility = Visibility.Collapsed;
        CommandBox.Text = "";
        SiteLogs.Reset();
        SiteDeployments.Reset();
        SiteJobs.Reset();
        SiteConfig.Reset();
        ResetDeploy();
        _followTone = null;
        // A site with a failed deploy opens on the log that says why, unless a tab was asked for.
        _siteTab = tab is not null && SiteTabs.Items.Any(i => (string)i.Tag == tab) ? tab
            : Status.SiteTone(CurrentSite()) == Tone.Bad ? "deployments" : "overview";
        _syncing = true;
        SiteTabs.SelectedItem = SiteTabs.Items.First(i => (string)i.Tag == _siteTab);
        _syncing = false;
        Render();
        LoadSiteTab();
    }

    private void RenderSite(Site site, Server? server)
    {
        var org = _service.Snapshot.Org(_org);
        var st = Status.SiteStatus(site);
        SiteCrumb.Text = $"{org?.Label ?? _org} › {server?.Name ?? "server"}";
        SiteTitle.Text = site.Name;
        SetDot(SiteDot, st.Tone);
        SiteStatus.Text = Capitalize(st.Label);
        SiteStatus.Foreground = st.Tone is Tone.Bad or Tone.Busy or Tone.Warn ? Palette.Text(st.Tone) : Palette.Secondary;
        var detail = string.Join(" · ", new[]
        {
            st.Timed && site.DeployedAt != "" ? ForgeText.RelativeTime(site.DeployedAt, NowMs) : "",
            SiteLine(site),
        }.Where(s => s != ""));
        SiteStatusDetail.Text = detail == "" ? "" : "· " + detail;

        var deploying = Status.SiteTone(site) == Tone.Busy && site.MaintenanceStatus == "";
        DeployButton.IsEnabled = Status.CanDeploy(site) && !deploying && !_sending;
        DeployText.Text = deploying ? "Deploying…" : "Deploy";
        ToolTipService.SetToolTip(DeployButton, Status.CanDeploy(site) ? "Deploy" : "Can't deploy: no repository");
        OpenSiteButton.IsEnabled = ForgeText.ExternalUrl(site.Url) != "";

        if (deploying)
        {
            SiteInfo.Severity = InfoBarSeverity.Informational;
            SiteInfo.Title = "Deploying";
            SiteInfo.Message = "Checking every 15 seconds. The deployment log follows along while it is open.";
            // A new bar each report would restart its animation; keep the one that is running.
            if (SiteInfo.Content is not ProgressBar)
                SiteInfo.Content = new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 0, 0, 12) };
            SiteInfoLink.Content = "Follow the log";
            SiteInfo.IsOpen = true;
        }
        else if (st.Tone == Tone.Bad)
        {
            SiteInfo.Severity = InfoBarSeverity.Error;
            SiteInfo.Title = "The last deploy failed";
            SiteInfo.Message = "The site still serves the release before it.";
            SiteInfo.Content = null;
            SiteInfoLink.Content = "View log";
            SiteInfo.IsOpen = true;
        }
        else SiteInfo.IsOpen = false;

        var facts = new List<(string, string)>();
        if (server is not null) facts.Add(("Server", string.Join(" · ", new[] { server.Name, server.Region }.Where(s => s != ""))));
        if (site.Branch != "") facts.Add(("Branch", site.Branch));
        if (site.CommitHash != "") facts.Add(("Commit", (site.CommitHash + " " + site.CommitMessage).Trim()));
        facts.AddRange(Status.SiteDetails(site).Where(f => f.Label != "Maintenance"));
        SetFacts(facts);

        var actions = Actions.SiteActions(_org, site);
        var maintenance = actions.First(a => a.Id == "maintenance");
        _syncing = true;
        MaintenanceSwitch.IsOn = site.Maintenance;
        _syncing = false;
        MaintenanceSwitch.IsEnabled = maintenance.Available && !_sending;
        MaintenanceText.Text = !maintenance.Available ? Capitalize(maintenance.Reason)
            : site.Maintenance ? "On: visitors get a 503 page" : "Off: visitors see the site";
        var php = actions.First(a => a.Id == "site-php-reload");
        PhpReloadText.Text = php.Label;
        PhpReloadButton.IsEnabled = php.Available && !_sending;
        ToolTipService.SetToolTip(PhpReloadButton, php.Available ? null : Capitalize(php.Reason));

        OverviewPanel.Visibility = _siteTab == "overview" ? Visibility.Visible : Visibility.Collapsed;
        SiteLogs.Visibility = _siteTab == "logs" ? Visibility.Visible : Visibility.Collapsed;
        SiteDeployments.Visibility = _siteTab == "deployments" ? Visibility.Visible : Visibility.Collapsed;
        SiteJobs.Visibility = _siteTab == "schedule" ? Visibility.Visible : Visibility.Collapsed;
        SiteConfig.Visibility = _siteTab == "config" ? Visibility.Visible : Visibility.Collapsed;
        DeployPanel.Visibility = _siteTab == "deploy" ? Visibility.Visible : Visibility.Collapsed;
        RenderDeploy(site);
        HeartbeatsPanel.Visibility = _siteTab == "heartbeats" ? Visibility.Visible : Visibility.Collapsed;
        CommandPanel.Visibility = _siteTab == "command" ? Visibility.Visible : Visibility.Collapsed;
        RunButton.IsEnabled = CommandBox.Text.Trim() != "" && !_sending;
    }

    /// <summary>
    /// Words Forge reports in lower case ("custom", "zero downtime") read better capitalized; names,
    /// branches, commits and addresses are shown exactly as they are.
    /// </summary>
    internal static string FactValue(string label, string value) =>
        label is "Type" or "Status" or "Deploys" or "Isolation" or "Maintenance" or "Quick deploy" ? Capitalize(value) : value;

    private void SetFacts(IReadOnlyList<(string Label, string Value)> facts)
    {
        var looks = string.Join("\n", facts.Select(f => f.Label + "=" + f.Value));
        if (looks == _factsShown) return;
        _factsShown = looks;
        FactsGrid.Children.Clear();
        FactsGrid.RowDefinitions.Clear();
        for (var i = 0; i < facts.Count; i++)
        {
            if (i % 2 == 0) FactsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var cell = new StackPanel();
            cell.Children.Add(new TextBlock
            {
                Text = facts[i].Label,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = Palette.Secondary,
            });
            cell.Children.Add(new TextBlock { Text = FactValue(facts[i].Label, facts[i].Value), TextTrimming = TextTrimming.CharacterEllipsis, IsTextSelectionEnabled = true });
            ToolTipService.SetToolTip(cell, facts[i].Value);
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            FactsGrid.Children.Add(cell);
        }
    }

    private void SiteTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncing) return;
        _siteTab = sender.SelectedItem?.Tag as string ?? "overview";
        RenderPage();
        LoadSiteTab();
    }

    /// <summary>Deployments, on the newest one whatever was being read before: after a deploy, or from the failed-deploy banner.</summary>
    private void ShowLatestDeployment()
    {
        SiteDeployments.Reset();
        ShowSiteTab("deployments");
    }

    private void ShowSiteTab(string tab)
    {
        _siteTab = tab;
        _syncing = true;
        SiteTabs.SelectedItem = SiteTabs.Items.First(i => (string)i.Tag == tab);
        _syncing = false;
        RenderPage();
        LoadSiteTab();
    }

    /// <summary>Fetches what the open tab shows. A newer load (or another site) makes an older answer stale, and it is dropped.</summary>
    private async void LoadSiteTab()
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        var gen = ++_loadGen;
        _logLoadedMs = NowMs;
        switch (_siteTab)
        {
            case "deployments":
                LoadDeployments(null);
                break;
            case "schedule":
                LoadJobs(SiteJobs, null, site: true);
                break;
            case "logs":
                LoadSiteLogKinds();
                break;
            case "config":
                // Coming back to the tab keeps an edit in progress; it is only re-read when not editing.
                if (SiteConfig.Editor.IsEditing) break;
                SiteConfig.ShowFiles();
                break;
            case "deploy":
                LoadDeploy(gen);
                break;
            case "heartbeats":
            {
                HeartbeatsText.Text = "Loading…";
                HeartbeatList.ItemsSource = null;
                var result = await _service.HeartbeatsAsync(org, site);
                if (gen != _loadGen) return;
                if (!result.Ok)
                {
                    HeartbeatsText.Text = result.Error ?? "Could not read the heartbeats";
                    return;
                }
                var beats = result.Value!;
                HeartbeatsText.Text = beats.Count == 0 ? "No heartbeats for this site." : ForgeText.Pluralize(beats.Count, "heartbeat");
                HeartbeatList.ItemsSource = beats.Select(h =>
                {
                    var (label, tone) = Status.HeartbeatStatus(h);
                    return new ListEntry { Key = h.Id, Title = h.Name, Detail = Status.HeartbeatDetail(h), Status = Capitalize(label), Tone = tone, Payload = h };
                }).ToList();
                break;
            }
        }
    }


    // ------------------------------------------- lists beside their output

    /// <summary>
    /// The history, newest first, opened on the latest deployment's log. A reload keeps an older
    /// deployment the reader chose; one who was reading the latest moves on to the new latest.
    /// </summary>
    private async void LoadDeployments(string? cursor)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        var gen = ++_loadGen;
        SiteDeployments.BeginList(cursor is not null, "Loading the site's deployments…");
        var result = await _service.DeploymentsAsync(org, site, cursor);
        if (gen != _loadGen) return;
        if (!result.Ok) { SiteDeployments.ShowListError(result.Error ?? "Could not read the deployments"); return; }
        var now = NowMs;
        SiteDeployments.ShowList(result.Value!.Select(d => new ListEntry
        {
            Key = d.Id,
            Title = d.CommitMessage != "" ? d.CommitMessage : "Deployment " + d.Id,
            Detail = Status.DeploymentDetail(d),
            Status = Capitalize(Status.DeploymentLabel(d.Status)),
            Time = ForgeText.RelativeTime(Status.DeploymentTime(d), now),
            Tone = Status.DeploymentTone(d.Status), Payload = d,
        }), cursor is not null, result.NextCursor, "This site has never been deployed");
    }

    private async void LoadDeploymentLog(Deployment d)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        var gen = ++_loadGen;
        _logLoadedMs = NowMs;
        // The latest deployment's row was read with the list; the site's status is the live one.
        var latest = d.Id == site.DeploymentId;
        var label = latest ? Status.SiteStatus(site).Label : Status.DeploymentLabel(d.Status);
        var bad = latest ? Status.SiteTone(site) == Tone.Bad : Status.DeploymentTone(d.Status) == Tone.Bad;
        var header = Capitalize(label) + (d.CommitHash == "" ? "" : " · " + d.CommitHash);
        SiteDeployments.Log.ShowLoading(header);
        var result = await _service.DeploymentLogAsync(org, site, d.Id);
        if (gen != _loadGen) return;
        if (result.Ok)
            SiteDeployments.Log.Show(result.Value!, header,
                $"forge-{ForgeText.SafeFileName(site.Name)}-{ForgeText.SafeFileName(d.Id)}.log",
                "The deployment printed nothing", bad);
        else SiteDeployments.Log.ShowError(result.Error ?? "Could not read the log");
    }

    /// <summary>A site's jobs or its server's; both lists and their outputs read the same way.</summary>
    private async void LoadJobs(ListLogPane pane, string? cursor, bool site)
    {
        var current = site ? CurrentSite() : null;
        if (CurrentServer() is not { } server || (site && current is null)) return;
        var org = _org;
        var gen = ++_loadGen;
        pane.BeginList(cursor is not null, "Loading the scheduled jobs…");
        var result = await _service.ScheduledJobsAsync(org, server.Id, current, cursor);
        if (gen != _loadGen) return;
        if (!result.Ok) { pane.ShowListError(result.Error ?? "Could not read the scheduled jobs"); return; }
        var now = NowMs;
        pane.ShowList(result.Value!.Select(j =>
        {
            var (label, tone) = Status.ScheduledJobStatus(j);
            return new ListEntry
            {
                Key = j.Id, Title = Status.ScheduledJobTitle(j), Detail = Status.ScheduledJobDetail(j),
                Status = Capitalize(label),
                Time = Status.ScheduledJobNext(j, now),
                Tone = tone, Payload = j,
            };
        }), cursor is not null, result.NextCursor,
            site ? "This site has no scheduled jobs" : "This server has no scheduled jobs");
    }

    private async void LoadJobOutput(ListLogPane pane, ScheduledJob job, bool site)
    {
        var current = site ? CurrentSite() : null;
        if (CurrentServer() is not { } server || (site && current is null)) return;
        var org = _org;
        var gen = ++_loadGen;
        var title = Status.ScheduledJobTitle(job);
        pane.Log.ShowLoading(title);
        var result = await _service.ScheduledJobOutputAsync(org, server.Id, current, job.Id);
        if (gen != _loadGen) return;
        if (result.Ok)
            pane.Log.Show(result.Value!, title,
                $"forge-{ForgeText.SafeFileName(current?.Name ?? server.Name)}-cron-{ForgeText.SafeFileName(job.Id)}.log",
                "The last run printed nothing");
        else pane.Log.ShowError(result.Error ?? "Could not read the job's output");
    }

    /// <summary>A site's three logs: the list is known without asking, so only the opened log costs a request.</summary>
    private void LoadSiteLogKinds()
    {
        if (CurrentSite() is null) return;
        ++_loadGen;
        SiteLogs.BeginList(false, "");
        SiteLogs.ShowList(Actions.SiteLogKinds.Select(k => new ListEntry
        {
            Key = k.Kind, Title = k.Label, Detail = k.Kind, Payload = k.Kind,
        }), false, "", "This site has no logs to read");
    }

    private async void LoadSiteLog(string kind)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        var gen = ++_loadGen;
        SiteLogs.Log.CanClear = true;
        SiteLogs.Log.ShowLoading(Actions.SiteLogLabel(kind));
        var result = await _service.SiteLogAsync(org, site, kind);
        if (gen != _loadGen) return;
        if (result.Ok)
            SiteLogs.Log.Show(result.Value!, Actions.SiteLogLabel(kind),
                $"forge-{ForgeText.SafeFileName(site.Name)}-{ForgeText.SafeFileName(kind)}.log", "The log is empty");
        else SiteLogs.Log.ShowError(result.Error ?? "Could not read the log");
    }

    // -------------------------------------------------------- config files

    /// <summary>One config file, read whole. A newer load (or another site) makes the answer stale, and it is dropped.</summary>
    private async void LoadConfig(ConfigFile file)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        var gen = ++_loadGen;
        SiteConfig.Editor.ShowLoading(file);
        var result = await _service.ConfigFileAsync(org, site, file);
        if (gen != _loadGen || _closed) return;
        if (result.Ok) SiteConfig.Editor.Show(file, result.Value!);
        else SiteConfig.Editor.ShowError(file, result.Error ?? "Could not read the " + ConfigFiles.Lower(file));
    }

    /// <summary>
    /// Shows what the edit changes and asks; then the service checks the file is still what was
    /// opened, saves it and reads it back. Nothing here logs or keeps the file's text.
    /// </summary>
    private async void SaveConfig()
    {
        var editor = SiteConfig.Editor;
        if (_sending || CurrentSite() is not { } site || editor.File is not { } file || editor.Document is not { } baseline) return;
        var org = _org;
        var key = site.Key;
        var edited = editor.EditedText;
        if (edited == baseline.Content) { Flash("Nothing changed"); return; }

        var what = ConfigFiles.Lower(file);
        var diff = ConfigText.Diff(baseline.Content, edited, maskContext: file == ConfigFile.Environment);
        var body = $"This replaces the {what} of {site.Name} with your edit. Forge's copy is read again first; if it changed since you opened it, nothing is saved."
            + (file switch
            {
                ConfigFile.Nginx => "\n\nA broken nginx config can stop the site (and others on the server) from responding. Check it before you save.",
                ConfigFile.Environment => "\n\nUnchanged values are hidden below. The app may need a deploy or a queue restart to pick up the change.",
                _ => "\n\nIt runs on the next deploy.",
            });
        if (!await WriteConfirm.AskAsync(Root.XamlRoot, Root.ActualTheme, $"Save the {what}?", body, "Save",
                detail: WriteConfirm.DiffBlock(diff)))
        {
            Flash("Not saved");
            return;
        }
        if (_closed || _org != org || _siteKey != key || editor.File != file || editor.Document != baseline
            || !editor.IsEditing || editor.EditedText != edited
            || _service.Snapshot.Org(org)?.SiteByKey(key) is not { } latest)
        {
            Flash("That changed while you were deciding. Nothing was sent.", bad: true);
            return;
        }

        _sending = true;
        editor.Busy = true;
        RenderPage();
        try
        {
            var outcome = await _service.SaveConfigAsync(org, latest, file, baseline, edited);
            if (_closed) return;
            Flash(outcome.Message, !outcome.Ok);
            var stillOpen = _org == org && _siteKey == key && editor.File == file;
            if (!stillOpen) return;
            if (outcome.Conflict)
                editor.Note($"Forge's copy changed since you opened it, so nothing was saved. Copy your edit if you need it, then Discard and Reload to see the new version.", bad: true);
            else if (outcome.Ok && outcome.Now is { } now)
                editor.Show(file, now, "Saved. This is what Forge holds now.");
            else if (outcome.Ok)
                editor.ShowError(file, outcome.Message + ". Reload to see what Forge holds.");
        }
        catch (Exception ex)
        {
            Flash(ex.Message, bad: true);
        }
        finally
        {
            _sending = false;
            editor.Busy = false;
            if (!_closed) RenderPage();
        }
    }

    /// <summary>The logs this server can have, from what it already reports: only the opened log costs a request.</summary>
    private void LoadServerLogKinds()
    {
        if (CurrentServer() is not { } server) return;
        ++_loadGen;
        ServerLogs.BeginList(false, "");
        ServerLogs.ShowList(Actions.ServerLogKinds(server).Select(k => new ListEntry
        {
            Key = k.Key, Title = k.Label, Detail = k.Key, Payload = k.Key,
        }), false, "", "This server has no logs to read");
    }

    private async void LoadServerLog(string key)
    {
        if (CurrentServer() is not { } server) return;
        var org = _org;
        var gen = ++_loadGen;
        ServerLogs.Log.CanClear = true;
        ServerLogs.Log.ShowLoading(Actions.ServerLogLabel(key));
        var result = await _service.ServerLogAsync(org, server.Id, key);
        if (gen != _loadGen) return;
        if (result.Ok)
            ServerLogs.Log.Show(result.Value!, Actions.ServerLogLabel(key),
                $"forge-{ForgeText.SafeFileName(server.Name)}-{ForgeText.SafeFileName(key)}.log", "The log is empty");
        else ServerLogs.Log.ShowError(result.Error ?? "Could not read the log");
    }

    private async void LoadProcesses(string? cursor)
    {
        if (CurrentServer() is not { } server) return;
        var org = _org;
        var gen = ++_loadGen;
        ServerProcesses.BeginList(cursor is not null, "Loading the background processes…");
        var result = await _service.BackgroundProcessesAsync(org, server.Id, cursor);
        if (gen != _loadGen) return;
        if (!result.Ok) { ServerProcesses.ShowListError(result.Error ?? "Could not read the background processes"); return; }
        ServerProcesses.ShowList(result.Value!.Select(p =>
        {
            var (label, tone) = Status.ProcessStatus(p);
            return new ListEntry
            {
                Key = p.Id, Title = p.Command == "" ? "Process " + p.Id : p.Command, Detail = Status.ProcessDetail(p),
                Status = Capitalize(label), Tone = tone, Payload = p,
            };
        }), cursor is not null, result.NextCursor, "This server runs no background processes");
    }

    private async void LoadProcessLog(BackgroundProcess p)
    {
        if (CurrentServer() is not { } server) return;
        var org = _org;
        var gen = ++_loadGen;
        var restart = Actions.ProcessRestart(org, server, p);
        ServerProcesses.SetAction("Restart", restart.Available && !_sending,
            restart.Available ? "Restart asks before it sends" : "Can't restart now: " + restart.Reason);
        var header = Capitalize(Status.ProcessStatus(p).Label) + " · " + p.Command;
        ServerProcesses.Log.ShowLoading(header);
        var result = await _service.BackgroundProcessLogAsync(org, server.Id, p.Id);
        if (gen != _loadGen) return;
        if (result.Ok)
            ServerProcesses.Log.Show(result.Value!, header,
                $"forge-{ForgeText.SafeFileName(server.Name)}-process-{ForgeText.SafeFileName(p.Id)}.log",
                "The process has logged nothing", Status.ProcessStatus(p).Tone == Tone.Bad);
        else ServerProcesses.Log.ShowError(result.Error ?? "Could not read the process's log");
    }

    // ------------------------------------------------- clearing, restarting

    private void ClearSiteLog()
    {
        if (CurrentSite() is not { } site || SiteLogs.Selected is not { Payload: string kind }) return;
        var org = _org;
        var key = site.Key;
        SendAction(org, site.ServerId, Actions.ClearSiteLog(org, site, kind), site.Name,
            () => _siteKey == key && SiteLogs.Selected is { Payload: string now } && now == kind
                ? Actions.ClearSiteLog(org, _service.Snapshot.Org(org)?.SiteByKey(key), kind) : null,
            () => { if (_org == org && _siteKey == key && _siteTab == "logs") LoadSiteLog(kind); });
    }

    private void ClearServerLog()
    {
        if (CurrentServer() is not { } server || ServerLogs.Selected is not { Payload: string logKey }) return;
        var org = _org;
        var id = server.Id;
        SendAction(org, id, Actions.ClearServerLog(org, server, logKey), server.Name,
            () => ServerLogs.Selected is { Payload: string now } && now == logKey
                ? Actions.ClearServerLog(org, _service.Snapshot.Org(org)?.ServerById(id), logKey) : null,
            () => { if (_org == org && _serverId == id && _serverTab == "logs") LoadServerLog(logKey); });
    }

    private void RestartProcess()
    {
        if (CurrentServer() is not { } server || ServerProcesses.Selected is not { Payload: BackgroundProcess p }) return;
        var org = _org;
        var id = server.Id;
        SendAction(org, id, Actions.ProcessRestart(org, server, p), server.Name,
            () => ServerProcesses.Selected is { Payload: BackgroundProcess now } && now.Id == p.Id
                ? Actions.ProcessRestart(org, _service.Snapshot.Org(org)?.ServerById(id), now) : null,
            () => { if (_org == org && _serverId == id && _serverTab == "processes") LoadProcesses(null); });
    }

    /// <summary>
    /// While a deploy runs and the latest deployment's log is open, read it again as the service's
    /// checks come in. When the site reports a deployment the list doesn't have yet, or the deploy
    /// ends, read the list again too, which moves the reader on to the newest one.
    /// </summary>
    private void FollowDeployLog()
    {
        var site = CurrentSite();
        var tone = Status.SiteTone(site);
        var was = _followTone;
        _followTone = tone;
        if (site is null || _siteTab != "deployments") return;
        var onLatest = SiteDeployments.Selected is null || SiteDeployments.Selected.Key == SiteDeployments.FirstKey;
        if (!onLatest) return;
        if (was == Tone.Busy && tone != Tone.Busy) { LoadDeployments(null); return; }
        // Only while a deploy runs, and no faster than the service checks: outside one, nothing here costs a request.
        if (tone != Tone.Busy || NowMs - _logLoadedMs <= 12_000) return;
        var newer = site.DeploymentId != "" && site.DeploymentId != SiteDeployments.FirstKey;
        if (newer) { _logLoadedMs = NowMs; LoadDeployments(null); }
        else if (SiteDeployments.Selected is { Payload: Deployment d }) LoadDeploymentLog(d);
    }

    // --------------------------------------------------------------- server

    private void SelectServer(string org, string serverId)
    {
        if (_org == org && _siteKey == "" && _serverId == serverId) return;
        _org = org;
        _siteKey = "";
        _serverId = serverId;
        _serverTab = "services";
        _syncing = true;
        ServerTabs.SelectedItem = ServerTabs.Items[0];
        _syncing = false;
        SiteConfig.Reset();
        ServerEvents.Reset();
        ServerLogs.Reset();
        ServerProcesses.Reset();
        ServerJobs.Reset();
        Render();
    }

    private void RenderServer(Server server)
    {
        var org = _service.Snapshot.Org(_org);
        ServerCrumb.Text = org?.Label ?? _org;
        ServerTitle.Text = server.Name;
        var tone = Status.ServerTone(server.State);
        SetDot(ServerDot, tone);
        ServerStatus.Text = string.Join(" · ", new[]
        {
            Capitalize(Status.ServerStateLabel(server.State)), server.Kind, Status.ServerMeta(server),
            ForgeText.Pluralize(org?.SitesOf(server.Id).Count ?? 0, "site"),
        }.Where(s => s != ""));

        ServicesPanel.Visibility = _serverTab == "services" ? Visibility.Visible : Visibility.Collapsed;
        ServerEvents.Visibility = _serverTab == "events" ? Visibility.Visible : Visibility.Collapsed;
        ServerLogs.Visibility = _serverTab == "logs" ? Visibility.Visible : Visibility.Collapsed;
        ServerProcesses.Visibility = _serverTab == "processes" ? Visibility.Visible : Visibility.Collapsed;
        ServerJobs.Visibility = _serverTab == "schedule" ? Visibility.Visible : Visibility.Collapsed;
        if (_serverTab == "services") RenderServices(server);
    }

    /// <summary>One card per write the server type allows: what it does, why not when it can't, and its button.</summary>
    private void RenderServices(Server server)
    {
        var writes = Actions.ServerActions(_org, server).Where(a => a.Armable).ToList();
        var looks = $"{_org}|{server.Id}|{server.Name}|{_sending}|" + string.Join("\n", writes.Select(a => $"{a.Id}|{a.Label}|{a.Available}|{a.Reason}|{a.ArmIntent}"));
        if (looks == _servicesShown) return;
        _servicesShown = looks;
        ServiceCards.Children.Clear();
        void Heading(string text) => ServiceCards.Children.Add(new TextBlock
        {
            Text = text,
            Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            Margin = new Thickness(2, ServiceCards.Children.Count == 0 ? 0 : 16, 0, 4),
        });
        foreach (var spec in writes.Where(a => a.Id != "reboot")) ServiceCards.Children.Add(ServiceCard(server, spec));
        if (writes.FirstOrDefault(a => a.Id == "reboot") is { } reboot)
        {
            Heading("Server");
            ServiceCards.Children.Add(ServiceCard(server, reboot));
        }
    }

    private Border ServiceCard(Server server, ActionSpec spec)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = spec.Label });
        text.Children.Add(new TextBlock
        {
            Text = spec.Available ? "Asks before it sends" : "Not now: " + spec.Reason,
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Palette.Secondary,
        });
        grid.Children.Add(text);
        var button = new Button
        {
            Content = WriteConfirm.Wording(spec, server.Name).Cta,
            IsEnabled = spec.Available && !_sending,
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 96,
        };
        var org = _org;
        var serverId = server.Id;
        button.Click += (_, _) => SendAction(org, serverId, spec, server.Name,
            () => Actions.ServerActions(org, _service.Snapshot.Org(org)?.ServerById(serverId)).FirstOrDefault(a => a.Id == spec.Id));
        Grid.SetColumn(button, 1);
        grid.Children.Add(button);
        return new Border
        {
            Child = grid,
            Padding = new Thickness(16, 12, 16, 12),
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
    }

    private void ServerTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncing) return;
        _serverTab = sender.SelectedItem?.Tag as string ?? "services";
        RenderPage();
        LoadServerTab();
    }

    /// <summary>Reads what the open server tab shows — on demand, never on the poll.</summary>
    private void LoadServerTab()
    {
        switch (_serverTab)
        {
            case "events": LoadEvents(null); break;
            case "logs": LoadServerLogKinds(); break;
            case "processes": LoadProcesses(null); break;
            case "schedule": LoadJobs(ServerJobs, null, site: false); break;
        }
    }

    private async void LoadEvents(string? cursor)
    {
        if (CurrentServer() is not { } server) return;
        var org = _org;
        var gen = ++_loadGen;
        ServerEvents.BeginList(cursor is not null, "Loading the server's events…");
        var result = await _service.EventsAsync(org, server.Id, cursor);
        if (gen != _loadGen) return;
        if (!result.Ok) { ServerEvents.ShowListError(result.Error ?? "Could not read the events"); return; }
        var now = NowMs;
        ServerEvents.ShowList(result.Value!.Select(e => new ListEntry
        {
            Key = e.Id, Title = e.Description,
            Detail = string.Join(" · ", new[] { e.SiteName, e.RanAs == "" ? "" : "as " + e.RanAs }.Where(s => s != "")),
            Time = ForgeText.RelativeTime(e.CreatedAt, now), Tone = Tone.Idle, Payload = e,
        }), cursor is not null, result.NextCursor, "No events on this server");
    }

    private async void LoadEventOutput(ForgeEvent ev)
    {
        if (CurrentServer() is not { } server) return;
        var org = _org;
        var gen = ++_loadGen;
        ServerEvents.Log.ShowLoading(ev.Description);
        var result = await _service.EventOutputAsync(org, server.Id, ev.Id);
        if (gen != _loadGen) return;
        if (result.Ok)
            ServerEvents.Log.Show(result.Value!, ev.Description, $"forge-{ForgeText.SafeFileName(server.Name)}-event-{ForgeText.SafeFileName(ev.Id)}.log",
                "The event printed nothing");
        else ServerEvents.Log.ShowError(result.Error ?? "Could not read the event's output");
    }

    // --------------------------------------------------------------- writes

    /// <summary>
    /// Asks, then sends — but only if what was asked about still holds: a refresh
    /// that lands while the dialog is up can change what the action means.
    /// </summary>
    private async void SendAction(string org, string serverId, ActionSpec spec, string subject, Func<ActionSpec?> fresh, Action? done = null)
    {
        if (!spec.Available) { Flash(Capitalize(spec.Reason)); return; }
        var (title, body, cta) = WriteConfirm.Wording(spec, subject);
        if (!await WriteConfirm.AskAsync(Root.XamlRoot, Root.ActualTheme, title, body, cta))
        {
            Flash("Not sent");
            return;
        }
        var now = fresh();
        if (now is null || !now.Available || now.ArmIntent != spec.ArmIntent)
        {
            Flash("That changed while you were deciding. Nothing was sent.", bad: true);
            return;
        }
        _sending = true;
        RenderPage();
        try
        {
            var outcome = await _service.SendActionAsync(org, serverId, now);
            Flash(outcome.Message, !outcome.Ok);
            if (outcome.Ok && !_closed) done?.Invoke();
        }
        catch (Exception e)
        {
            Flash(e.Message, bad: true);
        }
        finally
        {
            _sending = false;
            if (!_closed) RenderPage();
        }
    }

    private async void Deploy_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSite() is not { } site) return;
        if (!Status.CanDeploy(site)) { Flash(site.Name + " has no repository to deploy"); return; }
        var org = _org;
        var key = site.Key;
        var serverName = CurrentServer()?.Name ?? "its server";
        if (!await WriteConfirm.AskAsync(Root.XamlRoot, Root.ActualTheme, $"Deploy {site.Name}?",
                $"Runs the deploy script on {serverName}. You can follow the log while it runs.", "Deploy"))
        {
            Flash("Not sent");
            return;
        }
        if (_service.Snapshot.Org(org)?.SiteByKey(key) is not { } latest || !Status.CanDeploy(latest))
        {
            Flash("That changed while you were deciding. Nothing was sent.", bad: true);
            return;
        }
        _sending = true;
        RenderPage();
        try
        {
            var outcome = await _service.DeployAsync(org, latest);
            Flash(outcome.Ok ? $"{site.Name}: {outcome.Message}" : outcome.Message, !outcome.Ok);
            if (outcome.Ok && _org == org && _siteKey == key) ShowLatestDeployment();
        }
        catch (Exception ex)
        {
            Flash(ex.Message, bad: true);
        }
        finally
        {
            _sending = false;
            if (!_closed) RenderPage();
        }
    }

    private void MaintenanceSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        // The switch only asks; it keeps showing the site's real state until Forge reports the change.
        _syncing = true;
        MaintenanceSwitch.IsOn = !MaintenanceSwitch.IsOn;
        _syncing = false;
        SiteWrite("maintenance", CurrentSite()?.Name ?? "this site");
    }

    private void PhpReload_Click(object sender, RoutedEventArgs e) => SiteWrite("site-php-reload", CurrentServer()?.Name ?? "this server");

    private void SiteWrite(string id, string subject)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        var key = site.Key;
        var spec = Actions.SiteActions(org, site).First(a => a.Id == id);
        SendAction(org, site.ServerId, spec, subject,
            () => Actions.SiteActions(org, _service.Snapshot.Org(org)?.SiteByKey(key)).FirstOrDefault(a => a.Id == id));
    }

    private async void Run_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        var key = site.Key;
        var text = CommandBox.Text.Trim();
        var spec = Actions.SiteCommand(org, site, text);
        if (!spec.Available) { Flash(Capitalize(spec.Reason)); return; }
        if (!await WriteConfirm.AskAsync(Root.XamlRoot, Root.ActualTheme, "Run this command?",
                $"{text}\n\nRuns on {site.Name} as the site's user, in its directory.", "Run"))
        {
            Flash("Not sent");
            return;
        }
        if (_org != org || _siteKey != key || CommandBox.Text.Trim() != text)
        {
            Flash("That changed while you were deciding. Nothing was sent.", bad: true);
            return;
        }
        _sending = true;
        RenderPage();
        try
        {
            var (outcome, watch) = await _service.RunCommandAsync(org, site, text);
            Flash(outcome.Message, !outcome.Ok);
            if (watch is null || _closed) return;
            _commandWatch?.Stop();
            _commandWatch = watch;
            var file = $"forge-{ForgeText.SafeFileName(site.Name)}-command.out";
            CommandOutput.Visibility = Visibility.Visible;
            CommandOutput.ShowLoading("$ " + text);
            watch.Updated += update => DispatcherQueue.TryEnqueue(() =>
            {
                if (_commandWatch != watch || _closed) return;
                var header = "$ " + text + " · " + update.Header + (update.Message is null ? "" : " — " + update.Message);
                if (update.Lines is not null) CommandOutput.Show(update.Lines, header, file, "No output", update.Bad);
                else if (update.Running) CommandOutput.ShowLoading(header);
                else CommandOutput.Show([], header, file, update.Message ?? "No output", update.Bad);
            });
        }
        catch (Exception ex)
        {
            Flash(ex.Message, bad: true);
        }
        finally
        {
            _sending = false;
            if (!_closed) RenderPage();
        }
    }

    private void CommandBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        Run_Click(sender, e);
    }

    private void CommandBox_TextChanged(object sender, TextChangedEventArgs e) =>
        RunButton.IsEnabled = CommandBox.Text.Trim() != "" && !_sending;

    // ------------------------------------------------------------ open/copy

    private void OpenSite_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSite() is { } site && !Shell.OpenUrl(site.Url)) Flash(site.Name + " has no address to open");
    }

    private void OpenForge_Click(object sender, RoutedEventArgs e)
    {
        var template = _service.Setup.Settings.DashboardUrlTemplate;
        var server = CurrentServer();
        var url = server is null ? "" : ForgeText.DashboardUrl(template, _org, server, CurrentSite()?.Id);
        Shell.OpenUrl(url == "" ? ForgeText.OrgDashboardUrl(_org) : url);
    }

    private void CopySsh_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentServer() is not { } server) return;
        var command = ForgeText.SshCommand(server);
        if (command == "") { Flash(server.Name + " has no public IP"); return; }
        try
        {
            Shell.CopyText(command);
            Flash("Copied: " + command);
        }
        catch (Exception ex)
        {
            Flash("Could not copy: " + ex.Message, bad: true);
        }
    }

    private void Terminal_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentServer() is { } server && !Shell.OpenSsh(server)) Flash(server.Name + " has no address ssh can use", bad: true);
    }

    private void GoToServer_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSite() is { } site) SelectServer(_org, site.ServerId);
    }

    private void SiteInfoLink_Click(object sender, RoutedEventArgs e) => ShowLatestDeployment();

    private void RunCommandTab_Click(object sender, RoutedEventArgs e)
    {
        ShowSiteTab("command");
        CommandBox.Focus(FocusState.Programmatic);
    }

    // ----------------------------------------------------------- navigation

    private async void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        var picked = NavList.SelectedItem;
        if (picked is ListEntry { Kind: EntryKind.Site or EntryKind.Server } && SiteConfig.IsDirty)
        {
            // The list shows the edited site again until the reader says to drop the edit.
            RenderNav(_service.Snapshot);
            if (!await MayLeaveConfigAsync()) return;
        }
        switch (picked)
        {
            case ListEntry { Kind: EntryKind.Site, Payload: Site site } entry:
                SelectSite(entry.Org, site.Key);
                break;
            case ListEntry { Kind: EntryKind.Server, Payload: Server server } entry:
                SelectServer(entry.Org, server.Id);
                break;
            default:
                // An organization heading isn't a page; keep what was chosen.
                RenderNav(_service.Snapshot);
                break;
        }
    }

    private void OrgBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncing) return;
        _orgFilter = sender.SelectedItem?.Tag as string ?? "";
        RenderNav(_service.Snapshot);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_query == sender.Text) return;
        _query = sender.Text;
        RenderNav(_service.Snapshot);
    }

    /// <summary>Enter in the search opens the first site that matches.</summary>
    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        if (NavList.Items.OfType<ListEntry>().FirstOrDefault(e => e.Kind == EntryKind.Site) is { Payload: Site site } first)
            GoToSite(first.Org, site.Key);
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Escape || SearchBox.Text == "") return;
        e.Handled = true;
        SearchBox.Text = "";
    }

    private void FocusSearch_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        SearchBox.Focus(FocusState.Keyboard);
    }

    private void Reload_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        Reload();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Reload();

    /// <summary>F5 reloads what is being read (a log, heartbeats, events) and checks Forge again.</summary>
    private void Reload()
    {
        if (CurrentSite() is not null && _siteTab == "config" && SiteConfig.Editor.IsEditing)
            Flash("Save or discard your edit first; the file isn't read again while you edit it.");
        else if (CurrentSite() is not null && _siteTab == "config" && SiteConfig.HasFiles) SiteConfig.Reopen();
        else if (CurrentSite() is not null && _siteTab is not ("overview" or "command")) LoadSiteTab();
        else if (CurrentSite() is null && CurrentServer() is not null) LoadServerTab();
        _service.RefreshNow();
        UpdatedText.Text = "Refreshing…";
    }

    private void Settings_Click(object sender, RoutedEventArgs e) => _openSettings();

    private void Flash(string message, bool bad)
    {
        FlashBar.Severity = bad ? InfoBarSeverity.Error : InfoBarSeverity.Informational;
        FlashBar.Title = "";
        FlashBar.Message = message;
        FlashBar.IsOpen = message != "";
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    private void Flash(string message) => Flash(message, false);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);
}
