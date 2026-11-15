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

        SiteLog.ReloadRequested += LoadSiteTab;
        SiteLog.Reported += Flash;
        EventLog.ReloadRequested += () => { if (EventList.SelectedItem is ListEntry { Payload: ForgeEvent ev }) LoadEventOutput(ev); };
        EventLog.Reported += Flash;
        CommandOutput.CanReload = false;
        CommandOutput.Reported += Flash;

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
        };

        _syncing = true;
        SiteTabs.SelectedItem = SiteTabs.Items[0];
        ServerTabs.SelectedItem = ServerTabs.Items[0];
        NavList.ItemsSource = _navItems;
        _syncing = false;
        Render();
    }

    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>Shows the window, on a given site when one is named (from the flyout's current site, say).</summary>
    public void ShowAndFocus(string? org = null, string? siteKey = null)
    {
        if (!string.IsNullOrEmpty(org) && !string.IsNullOrEmpty(siteKey)) SelectSite(org, siteKey);
        else if (!_choseOnce) ChooseSomethingUseful();
        _choseOnce = true;
        AppWindow.Show();
        Activate();
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

    private void SelectSite(string org, string siteKey)
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
        SiteLog.Clear();
        _followTone = null;
        // A site with a failed deploy opens on the log that says why.
        _siteTab = Status.SiteTone(CurrentSite()) == Tone.Bad ? "log" : "overview";
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
        SiteLog.Visibility = _siteTab is "log" or "application" or "nginx-error" or "nginx-access" ? Visibility.Visible : Visibility.Collapsed;
        HeartbeatsPanel.Visibility = _siteTab == "heartbeats" ? Visibility.Visible : Visibility.Collapsed;
        CommandPanel.Visibility = _siteTab == "command" ? Visibility.Visible : Visibility.Collapsed;
        RunButton.IsEnabled = CommandBox.Text.Trim() != "" && !_sending;
    }

    /// <summary>
    /// Words Forge reports in lower case ("custom", "zero downtime") read better capitalized; names,
    /// branches, commits and addresses are shown exactly as they are.
    /// </summary>
    internal static string FactValue(string label, string value) =>
        label is "Type" or "Status" or "Deploys" or "Isolation" or "Maintenance" ? Capitalize(value) : value;

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
            case "log":
            {
                if (site.DeploymentId == "")
                {
                    SiteLog.Show([], "", "forge.log", "This site has never been deployed");
                    return;
                }
                SiteLog.ShowLoading();
                var result = await _service.DeploymentLogAsync(org, site);
                if (gen != _loadGen) return;
                var st = Status.SiteStatus(site);
                if (result.Ok)
                    SiteLog.Show(result.Value!, Capitalize(st.Label) + (site.CommitHash == "" ? "" : " · " + site.CommitHash),
                        $"forge-{ForgeText.SafeFileName(site.Name)}-{ForgeText.SafeFileName(site.DeploymentId)}.log",
                        "The deployment printed nothing", st.Tone == Tone.Bad);
                else SiteLog.ShowError(result.Error ?? "Could not read the log");
                break;
            }
            case "application" or "nginx-error" or "nginx-access":
            {
                var kind = _siteTab;
                SiteLog.ShowLoading();
                var result = await _service.SiteLogAsync(org, site, kind);
                if (gen != _loadGen) return;
                if (result.Ok)
                    SiteLog.Show(result.Value!, "", $"forge-{ForgeText.SafeFileName(site.Name)}-{ForgeText.SafeFileName(kind)}.log", "The log is empty");
                else SiteLog.ShowError(result.Error ?? "Could not read the log");
                break;
            }
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

    /// <summary>While a deploy runs and its log is open, read it again as the service's checks come in, and once more when it ends.</summary>
    private void FollowDeployLog()
    {
        var site = CurrentSite();
        var tone = Status.SiteTone(site);
        var was = _followTone;
        _followTone = tone;
        if (site is null || _siteTab != "log") return;
        var ended = was == Tone.Busy && tone != Tone.Busy;
        var running = tone == Tone.Busy && NowMs - _logLoadedMs > 12_000;
        if (ended || running) LoadSiteTab();
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
        EventList.ItemsSource = null;
        EventLog.Clear();
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
        EventsPanel.Visibility = _serverTab == "events" ? Visibility.Visible : Visibility.Collapsed;
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
        if (_serverTab == "events") LoadEvents();
    }

    private async void LoadEvents()
    {
        if (CurrentServer() is not { } server) return;
        var org = _org;
        var gen = ++_loadGen;
        EventList.ItemsSource = null;
        EventLog.Show([], "", "forge.log", "Loading the server's events…");
        var result = await _service.EventsAsync(org, server.Id);
        if (gen != _loadGen) return;
        if (!result.Ok)
        {
            EventLog.ShowError(result.Error ?? "Could not read the events");
            return;
        }
        var now = NowMs;
        var events = result.Value!;
        EventList.ItemsSource = events.Select(e => new ListEntry
        {
            Key = e.Id, Title = e.Description,
            Detail = string.Join(" · ", new[] { e.SiteName, e.RanAs == "" ? "" : "as " + e.RanAs }.Where(s => s != "")),
            Time = ForgeText.RelativeTime(e.CreatedAt, now), Tone = Tone.Idle, Payload = e,
        }).ToList();
        EventLog.Show([], "", "forge.log", events.Count == 0 ? "No events on this server" : "Pick an event to see what it printed");
    }

    private void EventList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (EventList.SelectedItem is ListEntry { Payload: ForgeEvent ev }) LoadEventOutput(ev);
    }

    private async void LoadEventOutput(ForgeEvent ev)
    {
        if (CurrentServer() is not { } server) return;
        var org = _org;
        var gen = ++_loadGen;
        EventLog.ShowLoading(ev.Description);
        var result = await _service.EventOutputAsync(org, server.Id, ev.Id);
        if (gen != _loadGen) return;
        if (result.Ok)
            EventLog.Show(result.Value!, ev.Description, $"forge-{ForgeText.SafeFileName(server.Name)}-event-{ForgeText.SafeFileName(ev.Id)}.log",
                "The event printed nothing");
        else EventLog.ShowError(result.Error ?? "Could not read the event's output");
    }

    // --------------------------------------------------------------- writes

    /// <summary>
    /// Asks, then sends — but only if what was asked about still holds: a refresh
    /// that lands while the dialog is up can change what the action means.
    /// </summary>
    private async void SendAction(string org, string serverId, ActionSpec spec, string subject, Func<ActionSpec?> fresh)
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
            if (outcome.Ok && _org == org && _siteKey == key) ShowSiteTab("log");
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

    private void SiteInfoLink_Click(object sender, RoutedEventArgs e) => ShowSiteTab("log");

    private void RunCommandTab_Click(object sender, RoutedEventArgs e)
    {
        ShowSiteTab("command");
        CommandBox.Focus(FocusState.Programmatic);
    }

    // ----------------------------------------------------------- navigation

    private void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        switch (NavList.SelectedItem)
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
            SelectSite(first.Org, site.Key);
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
        if (CurrentSite() is not null && _siteTab is not ("overview" or "command")) LoadSiteTab();
        else if (CurrentSite() is null && CurrentServer() is not null && _serverTab == "events") LoadEvents();
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
