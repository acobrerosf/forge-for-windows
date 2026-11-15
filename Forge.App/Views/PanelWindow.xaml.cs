using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Forge.App.Services;
using Forge.Core.Model;
using Forge.Core.Service;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;
using DispatcherQueueTimer = Microsoft.UI.Dispatching.DispatcherQueueTimer;
using DispatcherQueuePriority = Microsoft.UI.Dispatching.DispatcherQueuePriority;

namespace Forge.App.Views;

/// <summary>
/// The flyout above the tray. One surface, one cursor: the tree, a server's
/// or site's actions, a feed, a log — all are a different set of rows or a
/// pane pushed over the tree. Every write arms first and asks in a dialog;
/// only its button, or a capital Y, sends it.
/// </summary>
public sealed partial class PanelWindow : Window
{
    private const int WidthDip = 380;
    private const int HeightDip = 640;

    private readonly ForgeService _service;
    private readonly Action _openSettings;
    /// <summary>Opens the expanded window, on a site when given one (organization slug, site key).</summary>
    private readonly Action<string?, string?> _openWindow;
    private readonly List<Nav> _nav = [new NavTree()];
    private readonly HashSet<string> _collapsed = new(StringComparer.Ordinal);
    private readonly DispatcherQueueTimer _flashTimer;
    private readonly DispatcherQueueTimer _clock;

    private List<RowItem> _rows = [];
    private int _cursor;
    private string? _preferKey;

    private string _armedKey = "";
    private string _armedText = "";
    private Func<bool>? _armedValid;
    private Func<Task>? _armedSend;
    private string _busyKey = "";
    private ContentDialog? _confirmDialog;

    /// <summary>The tree's search text and organization filter ("" = every organization).</summary>
    private string _query = "";
    private string _orgFilter = "";
    private bool _syncingOrgBar;
    private bool _syncingSwitch;

    private bool _visible;
    private bool _closing;
    private long _hiddenAtMs;
    private IReadOnlyList<string>? _paneShown;

    public PanelWindow(ForgeService service, Action openSettings, Action<string?, string?> openWindow)
    {
        _service = service;
        _openSettings = openSettings;
        _openWindow = openWindow;
        InitializeComponent();

        var queue = DispatcherQueue;
        _flashTimer = queue.CreateTimer();
        _flashTimer.Interval = TimeSpan.FromSeconds(6);
        _flashTimer.IsRepeating = false;
        _flashTimer.Tick += (_, _) => FlashText.Visibility = Visibility.Collapsed;
        _clock = queue.CreateTimer();
        _clock.Interval = TimeSpan.FromSeconds(30);
        _clock.Tick += (_, _) => { if (_visible) Render(); };
        _clock.Start();

        CommandBox.TextChanged += (_, _) => { if (Top is NavCommand) RenderCommand(); };
        BuildKeysHelp();
        ConfigureWindow();
    }

    private static readonly (string Keys, string Does)[] KeyHelp =
    [
        ("↑ ↓  j k", "Move"),
        ("Enter", "Fold a server, open a site, choose an action"),
        ("→  l", "A server's actions"),
        ("←  h  Backspace", "Back"),
        ("Esc", "Back, then close"),
        ("Ctrl+F", "Search"),
        ("Ctrl+O", "Open in a window"),
        ("d", "Deploy the current site"),
        ("m", "Maintenance mode (on a site)"),
        ("Shift+Y", "Confirm what a dialog asks"),
        ("o  f", "Open site · open in Forge"),
        ("s  t", "Copy ssh · ssh in Windows Terminal"),
        ("e", "Server events"),
        ("r", "Refresh, or reload the open log"),
        ("c  w", "In a log: copy · save to Downloads"),
        (",", "Settings"),
    ];

    private void BuildKeysHelp()
    {
        KeysGrid.Children.Add(new TextBlock { Text = "Keyboard shortcuts", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 0, 0, 4) });
        Grid.SetColumnSpan((FrameworkElement)KeysGrid.Children[0], 2);
        KeysGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var i = 0; i < KeyHelp.Length; i++)
        {
            KeysGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var keys = new TextBlock { Text = KeyHelp[i].Keys, FontFamily = (FontFamily)Application.Current.Resources["MonoFont"], FontSize = 12 };
            var does = new TextBlock { Text = KeyHelp[i].Does, FontSize = 12, Foreground = Palette.Secondary, TextWrapping = TextWrapping.Wrap, MaxWidth = 220 };
            Grid.SetRow(keys, i + 1);
            Grid.SetRow(does, i + 1);
            Grid.SetColumn(does, 1);
            KeysGrid.Children.Add(keys);
            KeysGrid.Children.Add(does);
        }
    }

    private Nav Top => _nav[^1];
    private RowItem? CursorRow => _cursor >= 0 && _cursor < _rows.Count ? _rows[_cursor] : null;
    private static long NowMs => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    // ------------------------------------------------------------- window

    private void ConfigureWindow()
    {
        ExtendsContentIntoTitleBar = true;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        AppWindow.IsShownInSwitchers = false;
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "forge.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.IsResizable = false;
            p.IsMaximizable = false;
            p.IsMinimizable = false;
            p.IsAlwaysOnTop = true;
            p.SetBorderAndTitleBar(true, false);
        }
        AppWindow.Closing += (_, e) =>
        {
            if (_closing) return;
            e.Cancel = true;
            HidePanel();
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && _visible) HidePanel();
        };
    }

    public void Toggle()
    {
        if (_visible) HidePanel();
        // A click on the tray icon deactivates the panel first; that hide is the toggle.
        else if (NowMs - _hiddenAtMs > 400) ShowPanel();
    }

    public void ShowPanel()
    {
        PlaceNearTray();
        AppWindow.Show();
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
        _visible = true;
        Render();
        FocusContent();
        // Activation hands focus to the first tab stop, the search box; the keyboard belongs on the list.
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, FocusContent);
    }

    /// <summary>From a toast's View log: the flyout opened on that site's deployment log, Back leading to the site.</summary>
    public void ShowDeploymentLog(string org, string siteKey)
    {
        Disarm();
        while (_nav.Count > 1) Pop();
        if (!_visible) ShowPanel();
        if (_service.Snapshot.Org(org)?.SiteByKey(siteKey) is not { } site)
        {
            Flash("That site is no longer listed");
            return;
        }
        SelectRow(SiteRowKey(org, site.Key));
        Push(new NavSite { Org = org, SiteKey = site.Key });
        OpenDeployLog();
    }

    public void HidePanel()
    {
        if (!_visible) return;
        Disarm();
        _visible = false;
        _hiddenAtMs = NowMs;
        AppWindow.Hide();
    }

    public void CloseForGood()
    {
        _closing = true;
        _clock.Stop();
        Close();
    }

    public void OnServiceChanged()
    {
        if (_visible) Render();
    }

    /// <summary>Bottom-right of the work area (or whichever corner the taskbar is in), on the display under the pointer.</summary>
    private void PlaceNearTray()
    {
        GetCursorPos(out var pt);
        var display = DisplayArea.GetFromPoint(new PointInt32(pt.X, pt.Y), DisplayAreaFallback.Primary);
        var work = display.WorkArea;
        var outer = display.OuterBounds;
        AppWindow.Move(new PointInt32(work.X + 8, work.Y + 8));
        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        var w = (int)(WidthDip * scale);
        var h = (int)Math.Min(HeightDip * scale, work.Height - 24 * scale);
        var margin = (int)(12 * scale);
        var x = work.X > outer.X ? work.X + margin : work.X + work.Width - w - margin;
        var y = work.Y > outer.Y ? work.Y + margin : work.Y + work.Height - h - margin;
        AppWindow.MoveAndResize(new RectInt32(x, y, w, h));
    }

    private void FocusContent()
    {
        switch (Top)
        {
            case NavCommand:
                CommandBox.Focus(FocusState.Programmatic);
                break;
            case NavPane:
                PaneScroll.Focus(FocusState.Programmatic);
                break;
            default:
                RowList.Focus(FocusState.Programmatic);
                break;
        }
    }

    // ------------------------------------------------------------- render

    private void Render()
    {
        var snap = _service.Snapshot;
        DetailPanel.Visibility = Visibility.Collapsed;
        TreeTools.Visibility = Top is NavTree && !snap.NeedsSetup ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Visibility = _nav.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        TitleBlock.Padding = new Thickness(_nav.Count > 1 ? 0 : 8, 0, 0, 0);
        UpdatedText.Text = UpdatedLine(snap);
        List<RowItem> rows;
        switch (Top)
        {
            case NavServer s: rows = ServerRows(snap, s); break;
            case NavSite s: rows = SiteRows(snap, s); break;
            case NavList l: rows = ListRows(snap, l); break;
            case NavPane p: rows = []; RenderPane(p); break;
            case NavCommand: rows = []; RenderCommand(); break;
            default: rows = TreeRows(snap); break;
        }

        foreach (var row in rows)
        {
            row.Armed = row.ArmKey != "" && row.ArmKey == _armedKey;
            row.Busy = row.ArmKey != "" && row.ArmKey == _busyKey;
            if (row.Armed) row.Status = _armedText;
        }

        RowList.Visibility = Top is NavPane or NavCommand ? Visibility.Collapsed : Visibility.Visible;
        PanePanel.Visibility = Top is NavPane ? Visibility.Visible : Visibility.Collapsed;
        CommandPanel.Visibility = Top is NavCommand ? Visibility.Visible : Visibility.Collapsed;
        ApplyRows(rows);
        QueueSnapshot();
    }

    private static string UpdatedLine(ForgeSnapshot snap)
    {
        if (snap.NeedsSetup) return "Not set up yet";
        if (snap.Orgs.Any(o => o.Refreshing)) return "Refreshing…";
        var last = snap.Orgs.Count == 0 ? 0 : snap.Orgs.Max(o => o.LastRefreshMs);
        var orgs = snap.Orgs.Count > 1 ? " · " + ForgeText.Pluralize(snap.Orgs.Count, "organization") : "";
        return (last > 0 ? "Updated " + ForgeText.RelativeMs(last, NowMs) : "Loading…") + orgs;
    }

    // Screen capture APIs see a WinUI window as black, so a development build can
    // render its own XAML to a PNG instead. Off unless FORGE_SNAPSHOT_DIR is set.
    private static readonly string? SnapshotDir = Environment.GetEnvironmentVariable("FORGE_SNAPSHOT_DIR");
    private bool _snapshotQueued;
    private DispatcherQueueTimer? _snapshotTimer;

    private void QueueSnapshot()
    {
        if (string.IsNullOrEmpty(SnapshotDir) || _snapshotQueued || !_visible) return;
        _snapshotQueued = true;
        // Held in a field: a timer only a local refers to can be collected before it ever ticks.
        _snapshotTimer ??= DispatcherQueue.CreateTimer();
        var timer = _snapshotTimer;
        timer.Stop();
        timer.Interval = TimeSpan.FromMilliseconds(700);
        timer.IsRepeating = false;
        timer.Tick -= SnapshotTick;
        timer.Tick += SnapshotTick;
        timer.Start();
    }

    private async void SnapshotTick(DispatcherQueueTimer sender, object args)
    {
        _snapshotQueued = false;
        try
        {
            var target = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
            await target.RenderAsync(Root);
            var pixels = await target.GetPixelsAsync();
            var path = Path.Combine(SnapshotDir!, "panel.png");
            using var stream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await Windows.Graphics.Imaging.BitmapEncoder.CreateAsync(Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId, stream);
            encoder.SetPixelData(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8, Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
                (uint)target.PixelWidth, (uint)target.PixelHeight, 96, 96, pixels.ToArray());
            await encoder.FlushAsync();
            stream.Seek(0);
            using var file = File.Create(path);
            await stream.AsStreamForRead().CopyToAsync(file);
        }
        catch (Exception e)
        {
            Log.Write("Snapshot: " + e.Message);
        }
    }

    private void ApplyRows(List<RowItem> rows)
    {
        var key = _preferKey ?? CursorRow?.Key;
        _preferKey = null;
        _rows = rows;
        RowList.ItemsSource = rows;
        var index = key is null ? -1 : rows.FindIndex(r => r.Key == key);
        _cursor = index >= 0 ? index : Math.Clamp(_cursor, 0, Math.Max(0, rows.Count - 1));
        if (rows.Count == 0) return;
        _cursor = NearestSelectable(_cursor, 1);
        RowList.SelectedIndex = _cursor;
        RowList.ScrollIntoView(rows[_cursor]);
        // A refresh while someone is typing a search must not take the keyboard away from them.
        if (_visible && Top is not (NavPane or NavCommand) && !IsInside(SearchBox))
            DispatcherQueue.TryEnqueue(() => { if (!IsInside(SearchBox)) RowList.Focus(FocusState.Programmatic); });
    }

    /// <summary>The tree's header is just the app's name; a pushed view gets a large title with its state under it.</summary>
    private void SetHero(string crumb, string title, string subtitle, Tone? tone, Brush? subtitleBrush = null)
    {
        var tree = Top is NavTree;
        CrumbText.Text = crumb;
        CrumbText.Visibility = crumb == "" ? Visibility.Collapsed : Visibility.Visible;
        TitleText.Text = title;
        TitleText.FontSize = tree ? 14 : 18;
        SubtitleText.Text = subtitle;
        SubtitleText.Foreground = subtitleBrush ?? Palette.Secondary;
        SubtitleLine.Visibility = subtitle == "" ? Visibility.Collapsed : Visibility.Visible;
        HeroDot.Visibility = tone is null || tree ? Visibility.Collapsed : Visibility.Visible;
        if (tone is { } t)
        {
            HeroDot.Fill = t == Tone.Warn ? Palette.Transparent : Palette.For(t);
            HeroDot.Stroke = t == Tone.Warn ? Palette.For(t) : Palette.Transparent;
        }
    }

    private static string Capitalize(string s) => s.Length < 2 ? s.ToUpperInvariant() : char.ToUpperInvariant(s[0]) + s[1..];

    /// <summary>A site's facts as a two-column grid of caption over value.</summary>
    private void SetFacts(IReadOnlyList<(string Label, string Value)> facts)
    {
        FactsGrid.Children.Clear();
        FactsGrid.RowDefinitions.Clear();
        FactsCard.Visibility = facts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
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
            cell.Children.Add(new TextBlock { Text = ForgeWindow.FactValue(facts[i].Label, facts[i].Value), TextTrimming = TextTrimming.CharacterEllipsis });
            ToolTipService.SetToolTip(cell, facts[i].Value);
            Grid.SetRow(cell, i / 2);
            Grid.SetColumn(cell, i % 2);
            FactsGrid.Children.Add(cell);
        }
    }

    private static readonly Dictionary<string, string> ActionGlyphs = new()
    {
        ["deploy"] = "", ["maintenance"] = "", ["site-php-reload"] = "", ["command"] = "",
        ["log"] = "", ["heartbeats"] = "", ["open"] = "", ["forge"] = "", ["ssh"] = "",
        ["ssh-terminal"] = "", ["nginx-restart"] = "", ["php-reload"] = "", ["php-restart"] = "",
        ["supervisor-restart"] = "", ["redis-restart"] = "", ["database-restart"] = "",
        ["reboot"] = "", ["events"] = "", ["recipes"] = "", ["monitors"] = "",
    };

    private static string GlyphFor(ActionSpec a) =>
        ActionGlyphs.TryGetValue(a.Id, out var g) ? g : a.Log != "" ? "" : "";

    /// <summary>Which titled section of a site's or server's page an action belongs in; "" leaves it off the list.</summary>
    private static string SectionOf(ActionSpec a) => a.Id switch
    {
        // Drawn as the page's buttons and switch instead.
        "deploy" or "open" or "maintenance" => "",
        "log" => "Logs",
        _ when a.Log != "" => "Logs",
        "command" or "site-php-reload" or "heartbeats" => "Tools",
        "forge" or "ssh" or "ssh-terminal" => "Connect",
        "reboot" or "events" or "recipes" or "monitors" => "Server",
        _ => "Services",
    };

    private List<RowItem> Sectioned(IEnumerable<RowItem> rows)
    {
        var list = new List<RowItem>();
        string[] order = ["Logs", "Tools", "Services", "Server", "Connect"];
        foreach (var group in rows.GroupBy(r => SectionOf((ActionSpec)r.Payload!)).Where(g => g.Key != "")
                     .OrderBy(g => Array.IndexOf(order, g.Key)))
        {
            list.Add(new RowItem { Key = "header|" + group.Key, Kind = RowKind.Header, Title = group.Key });
            list.AddRange(group);
        }
        return list;
    }

    private static string ServerKey(string org, string serverId) => $"server|{org}|{serverId}";
    private static string SiteRowKey(string org, string siteKey) => $"site|{org}|{siteKey}";
    private static string DeployKey(string org, string siteKey) => $"deploy|{org}|{siteKey}";

    private static RowItem Info(string key, string title, string detail = "", Tone tone = Tone.Idle, object? payload = null) =>
        new() { Key = key, Kind = RowKind.Info, Title = title, Detail = detail, Tone = tone, Payload = payload };

    private List<RowItem> TreeRows(ForgeSnapshot snap)
    {
        var rows = new List<RowItem>();
        var now = NowMs;
        SetHero("", snap.Orgs.Count == 1 ? snap.Orgs[0].Label : "Forge", "", null);

        if (snap.NeedsSetup)
        {
            rows.Add(Info("setup", "Add a Forge API token to get started", "Press Enter to open Settings", Tone.Warn, "settings"));
            return rows;
        }

        SyncOrgBar(snap);
        var orgs = snap.Orgs.Where(o => _orgFilter == "" || o.Slug == _orgFilter).ToList();
        BuildChips(orgs);
        var query = _query.Trim();
        bool Matches(string name) => query == "" || name.Contains(query, StringComparison.OrdinalIgnoreCase);

        var many = orgs.Count > 1;
        foreach (var org in orgs)
        {
            // A search shows only what matches, so an organization with nothing that does is left out entirely.
            if (query != "" && !org.Servers.Any(s => Matches(s.Name) || org.SitesOf(s.Id).Any(site => Matches(site.Name))))
                continue;
            if (many)
                rows.Add(new RowItem
                {
                    Key = "org|" + org.Slug, Kind = RowKind.Org, Org = org.Slug, Title = org.Label,
                    Tone = Palette.ToneOf(org.Health), Status = HealthWord(org.Health),
                });
            if (org.AccountError != "") rows.Add(Info("token|" + org.Slug, org.AccountError, "Press Enter to open Settings", Tone.Warn, "settings"));
            else if (org.LastError != "") rows.Add(Info("error|" + org.Slug, org.LastError, "", Tone.Bad));
            if (org.Note != "") rows.Add(Info("note|" + org.Slug, org.Note));
            if (org.Servers.Count == 0)
            {
                rows.Add(Info("empty|" + org.Slug, org.LastRefreshMs == 0 ? "Loading…" : "No servers in this organization"));
                continue;
            }

            var depth = many ? 1 : 0;
            foreach (var server in org.Servers)
            {
                var key = ServerKey(org.Slug, server.Id);
                var sites = org.SitesOf(server.Id);
                if (query != "" && !Matches(server.Name))
                {
                    sites = sites.Where(s => Matches(s.Name)).ToList();
                    if (sites.Count == 0) continue;
                }
                var expanded = query != "" || !_collapsed.Contains(key);
                rows.Add(new RowItem
                {
                    Key = key, Kind = RowKind.Server, Org = org.Slug, Title = server.Name,
                    Detail = Status.ServerMeta(server),
                    Status = server.State == ServerState.Ready
                        ? sites.Count == 0 ? "no sites" : ForgeText.Pluralize(sites.Count, "site") + Worst(sites, expanded)
                        : Status.ServerStateLabel(server.State),
                    Tone = Status.ServerTone(server.State), Depth = depth,
                    Expanded = sites.Count > 0 ? expanded : null, Payload = server,
                });
                if (!expanded) continue;
                foreach (var site in sites)
                {
                    var st = Status.SiteStatus(site);
                    rows.Add(new RowItem
                    {
                        Key = SiteRowKey(org.Slug, site.Key), Kind = RowKind.Site, Org = org.Slug,
                        Title = site.Name, Detail = SiteLine(site), Status = st.Label,
                        Time = st.Timed ? ForgeText.RelativeTime(site.DeployedAt, now) : "",
                        Tone = st.Tone, Depth = depth + 1, ArmKey = DeployKey(org.Slug, site.Key), Payload = site,
                    });
                }
            }
        }
        if (query != "" && rows.All(r => r.Kind is RowKind.Org or RowKind.Info))
            rows.Add(Info("no-match", $"Nothing matches “{query}”", "Esc clears the search"));
        return rows;
    }

    /// <summary>Organization tabs, shown only when there is more than one to choose between.</summary>
    private void SyncOrgBar(ForgeSnapshot snap)
    {
        var wanted = new List<(string Slug, string Label)> { ("", "All") };
        wanted.AddRange(snap.Orgs.Select(o => (o.Slug, o.Label)));
        OrgScroll.Visibility = snap.Orgs.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        if (_orgFilter != "" && snap.Org(_orgFilter) is null) _orgFilter = "";

        var current = OrgBar.Items.Select(i => (i.Tag as string ?? "", i.Text)).ToList();
        _syncingOrgBar = true;
        try
        {
            if (!current.SequenceEqual(wanted))
            {
                OrgBar.Items.Clear();
                foreach (var (slug, label) in wanted)
                    OrgBar.Items.Add(new SelectorBarItem { Text = label, Tag = slug, IsTabStop = false });
            }
            var selected = OrgBar.Items.FirstOrDefault(i => (i.Tag as string ?? "") == _orgFilter);
            if (selected is not null && OrgBar.SelectedItem != selected) OrgBar.SelectedItem = selected;
        }
        finally
        {
            _syncingOrgBar = false;
        }
    }

    /// <summary>What needs looking at, one chip per kind of trouble; a chip opens the first such site.</summary>
    private void BuildChips(IReadOnlyList<OrgView> orgs)
    {
        ChipPanel.Children.Clear();
        var sites = orgs.SelectMany(o => o.Servers.SelectMany(s => o.SitesOf(s.Id)).Select(site => (Org: o.Slug, Site: site))).ToList();
        var down = orgs.SelectMany(o => o.Servers.Where(s => Status.ServerTone(s.State) == Tone.Bad).Select(s => (Org: o.Slug, Server: s))).ToList();
        if (sites.Count == 0 && down.Count == 0)
        {
            ChipPanel.Visibility = Visibility.Collapsed;
            return;
        }
        ChipPanel.Visibility = Visibility.Visible;

        if (down.Count > 0)
            AddChip(Tone.Bad, ForgeText.Pluralize(down.Count, "server") + " down", () =>
            {
                var index = _rows.FindIndex(r => r.Key == ServerKey(down[0].Org, down[0].Server.Id));
                if (index >= 0) MoveTo(index);
            });
        foreach (var (tone, word) in new[] { (Tone.Bad, "failed"), (Tone.Busy, "deploying"), (Tone.Warn, "in maintenance") })
        {
            var hits = sites.Where(s => Status.SiteTone(s.Site) == tone).ToList();
            if (hits.Count == 0) continue;
            var first = hits[0];
            AddChip(tone, $"{hits.Count} {word}", () => Push(new NavSite { Org = first.Org, SiteKey = first.Site.Key }));
        }
        var ok = sites.Count(s => Status.SiteTone(s.Site) is Tone.Ok or Tone.Idle);
        if (ok > 0 || ChipPanel.Children.Count == 0)
            AddChip(Tone.Ok, ok == sites.Count ? $"All {ForgeText.Pluralize(sites.Count, "site")} healthy" : $"{ok} healthy", null);
    }

    private void AddChip(Tone tone, string text, Action? onClick)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            Width = 8, Height = 8, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center,
            Fill = tone == Tone.Warn ? Palette.Transparent : Palette.For(tone),
            Stroke = tone == Tone.Warn ? Palette.For(tone) : Palette.Transparent,
        });
        content.Children.Add(new TextBlock { Text = text, FontSize = 12 });
        var chip = new Button
        {
            Content = content,
            Padding = new Thickness(8, 2, 10, 3),
            MinHeight = 24,
            CornerRadius = new CornerRadius(12),
            IsTabStop = false,
            IsHitTestVisible = onClick is not null,
        };
        if (onClick is not null)
        {
            chip.Click += (_, _) => onClick();
            ToolTipService.SetToolTip(chip, "Show it");
        }
        ChipPanel.Children.Add(chip);
    }

    /// <summary>A folded server still says when something under it needs looking at.</summary>
    private static string Worst(IReadOnlyList<Site> sites, bool expanded)
    {
        if (expanded) return "";
        var tones = sites.Select(Status.SiteTone).ToList();
        if (tones.Contains(Tone.Bad)) return " · failed";
        if (tones.Contains(Tone.Busy)) return " · deploying";
        if (tones.Contains(Tone.Warn)) return " · maintenance";
        return "";
    }

    private static string HealthWord(Health health) => health switch
    {
        Health.Bad => "needs attention",
        Health.Error => "request failed",
        Health.Setup => "needs a token",
        Health.Busy => "deploying",
        Health.Maintenance => "maintenance",
        _ => "",
    };

    private static string SiteLine(Site site)
    {
        var parts = new List<string>();
        if (site.Branch != "") parts.Add(site.Branch);
        if (site.CommitHash != "") parts.Add((site.CommitHash + " " + site.CommitMessage).Trim());
        if (parts.Count == 0 && site.RepoUrl == "") parts.Add("no repository");
        return string.Join(" · ", parts);
    }

    private RowItem ActionRow(ActionSpec a, string org, string subjectKey, string deploySiteKey = "") => new()
    {
        Key = "action|" + a.Id,
        Kind = RowKind.Action,
        Org = org,
        Title = a.Label,
        Status = a.Available ? a.Hint.ToUpperInvariant() : a.Reason,
        Enabled = a.Available,
        Glyph = GlyphFor(a),
        ArmKey = !a.Armable ? "" : a.ArmsSubject ? DeployKey(org, deploySiteKey) : $"{a.Id}|{org}|{subjectKey}|{a.ArmIntent}",
        Payload = a,
    };

    private List<RowItem> ServerRows(ForgeSnapshot snap, NavServer nav)
    {
        var org = snap.Org(nav.Org);
        var server = org?.ServerById(nav.ServerId);
        SetHero(org?.Label ?? nav.Org, server?.Name ?? "Server",
            server is null ? "No longer listed"
                : string.Join(" · ", new[] { Capitalize(Status.ServerStateLabel(server.State)), server.Kind, Status.ServerMeta(server) }.Where(s => s != "")),
            server is null ? Tone.Idle : Status.ServerTone(server.State));
        return Sectioned(ServerActionRows(snap, nav));
    }

    private List<RowItem> ServerActionRows(ForgeSnapshot snap, NavServer nav) =>
        Actions.ServerActions(nav.Org, snap.Org(nav.Org)?.ServerById(nav.ServerId))
            .Select(a => ActionRow(a, nav.Org, nav.ServerId)).ToList();

    private List<RowItem> SiteActionRows(ForgeSnapshot snap, NavSite nav) =>
        Actions.SiteActions(nav.Org, snap.Org(nav.Org)?.SiteByKey(nav.SiteKey))
            .Select(a => ActionRow(a, nav.Org, nav.SiteKey, nav.SiteKey)).ToList();

    private List<RowItem> SiteRows(ForgeSnapshot snap, NavSite nav)
    {
        var org = snap.Org(nav.Org);
        var site = org?.SiteByKey(nav.SiteKey);
        var server = site is null ? null : org!.ServerById(site.ServerId);
        var st = Status.SiteStatus(site);
        var subtitle = site is null ? "No longer listed"
            : string.Join(" · ", new[]
            {
                Capitalize(st.Label) + (st.Timed && site.DeployedAt != "" ? " " + ForgeText.RelativeTime(site.DeployedAt, NowMs) : ""),
                SiteLine(site),
            }.Where(s => s != ""));
        SetHero($"{org?.Label ?? nav.Org} › {server?.Name ?? "server"}", site?.Name ?? "Site", subtitle, st.Tone,
            st.Tone is Tone.Bad or Tone.Busy ? Palette.Text(st.Tone) : null);

        var rows = SiteActionRows(snap, nav);
        ShowSitePage(nav, site, rows);
        return Sectioned(rows);
    }

    /// <summary>The page above a site's action list: deploy and open, what is happening, its facts, maintenance mode.</summary>
    private void ShowSitePage(NavSite nav, Site? site, List<RowItem> rows)
    {
        DetailPanel.Visibility = Visibility.Visible;
        var tone = Status.SiteTone(site);
        var deploy = rows.First(r => r.Payload is ActionSpec { Id: "deploy" });
        var open = rows.First(r => r.Payload is ActionSpec { Id: "open" });
        var maintenance = rows.First(r => r.Payload is ActionSpec { Id: "maintenance" });
        var deployArmed = _armedKey != "" && _armedKey == deploy.ArmKey;
        var deploying = tone == Tone.Busy && site?.MaintenanceStatus == "" || _busyKey == deploy.ArmKey;

        DeployButton.IsEnabled = deploy.Enabled && !deploying && !deployArmed;
        DeployButtonText.Text = deploying ? "Deploying…" : "Deploy";
        ToolTipService.SetToolTip(DeployButton, deploy.Enabled ? "Deploy (D)" : "Can't deploy: " + ((ActionSpec)deploy.Payload!).Reason);
        OpenSiteButton.IsEnabled = open.Enabled;

        if (deploying)
        {
            SiteInfo.Severity = InfoBarSeverity.Informational;
            SiteInfo.Title = "Deploying";
            SiteInfo.Message = "Checking every 15 seconds";
            SiteInfo.Content = new ProgressBar { IsIndeterminate = true, Margin = new Thickness(0, 0, 0, 10) };
            SiteInfoLink.Content = "Follow log";
            SiteInfo.IsOpen = true;
        }
        else if (tone == Tone.Bad)
        {
            SiteInfo.Severity = InfoBarSeverity.Error;
            SiteInfo.Title = "The last deploy failed";
            SiteInfo.Message = "";
            SiteInfo.Content = null;
            SiteInfoLink.Content = "View log";
            SiteInfo.IsOpen = true;
        }
        else SiteInfo.IsOpen = false;

        // Maintenance is drawn with the facts' card, so it isn't repeated there.
        SetFacts(Status.SiteDetails(site).Where(f => f.Label != "Maintenance").ToList());

        var parked = site?.Maintenance == true;
        var spec = (ActionSpec)maintenance.Payload!;
        _syncingSwitch = true;
        MaintenanceSwitch.IsOn = parked;
        _syncingSwitch = false;
        MaintenanceSwitch.IsEnabled = maintenance.Enabled && _busyKey != maintenance.ArmKey;
        MaintenanceText.Text = !maintenance.Enabled ? Capitalize(spec.Reason)
            : parked ? "Visitors get a 503 page" : "Visitors see the site";
        MaintenanceCard.Visibility = site is null ? Visibility.Collapsed : Visibility.Visible;
        SiteButtons.Visibility = site is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private List<RowItem> ListRows(ForgeSnapshot snap, NavList nav)
    {
        var org = snap.Org(nav.Org);
        var server = org?.ServerById(nav.ServerId);
        var site = nav.SiteKey == "" ? null : org?.SiteByKey(nav.SiteKey);
        var (title, empty) = nav.Kind switch
        {
            ListKind.Events => ("Server events", "No events"),
            ListKind.Recipes => ("Run a recipe", "This organization has no recipes"),
            ListKind.Monitors => ("Monitors", "No monitors on this server"),
            _ => ("Heartbeats", "No heartbeats for this site"),
        };
        SetHero($"{org?.Label ?? nav.Org} › {site?.Name ?? server?.Name ?? ""}", title,
            nav.Kind == ListKind.Recipes ? "Runs as the recipe's user on " + (server?.Name ?? "this server") : "", null);

        var rows = new List<RowItem>();
        if (nav.Error is not null) rows.Add(Info("list-error", nav.Error, "", Tone.Bad));
        var now = NowMs;
        foreach (var item in nav.Items)
        {
            switch (item)
            {
                case ForgeEvent e:
                    rows.Add(new RowItem
                    {
                        Key = "event|" + e.Id, Kind = RowKind.Event, Org = nav.Org, Title = e.Description,
                        Detail = string.Join(" · ", new[] { e.SiteName, e.RanAs == "" ? "" : "as " + e.RanAs }.Where(s => s != "")),
                        Status = ForgeText.RelativeTime(e.CreatedAt, now), Payload = e,
                    });
                    break;
                case Recipe r:
                    var spec = Actions.RecipeRun(nav.Org, server, r);
                    rows.Add(new RowItem
                    {
                        Key = "recipe|" + r.Id, Kind = RowKind.Recipe, Org = nav.Org, Title = r.Name,
                        Detail = string.Join(" · ", new[] { r.User == "" ? "" : "as " + r.User, r.FirstLine }.Where(s => s != "")),
                        Status = spec.Available ? "" : spec.Reason, Enabled = spec.Available,
                        ArmKey = $"recipe-run|{nav.Org}|{nav.ServerId}|{r.Id}", Payload = r,
                    });
                    break;
                case ServerMonitor m:
                    var (mLabel, mTone) = Status.MonitorStatus(m);
                    rows.Add(new RowItem
                    {
                        Key = "monitor|" + m.Id, Kind = RowKind.Monitor, Org = nav.Org, Title = Status.MonitorLabel(m),
                        Detail = Status.MonitorDetail(m), Status = mLabel, Tone = mTone,
                        Time = ForgeText.RelativeTime(m.StateChangedAt, now), Payload = m,
                    });
                    break;
                case Heartbeat h:
                    var (hLabel, hTone) = Status.HeartbeatStatus(h);
                    rows.Add(new RowItem
                    {
                        Key = "heartbeat|" + h.Id, Kind = RowKind.Heartbeat, Org = nav.Org, Title = h.Name,
                        Detail = Status.HeartbeatDetail(h), Status = hLabel, Tone = hTone, Payload = h,
                    });
                    break;
            }
        }
        if (nav.Loading) rows.Add(Info("list-loading", "Loading…"));
        else if (nav.Error is null && nav.Items.Count == 0) rows.Add(Info("list-empty", empty));
        if (!nav.Loading && nav.Cursor != "") rows.Add(new RowItem { Key = "more", Kind = RowKind.More, Title = "Load more…" });
        return rows;
    }

    private void RenderPane(NavPane pane)
    {
        SetHero(pane.Crumb, pane.Title, pane.Loading ? "Loading…" : pane.Watch is not null ? "Following the run" : "", null);
        PaneHeader.Text = pane.Error ?? pane.Header;
        PaneHeader.Visibility = PaneHeader.Text == "" ? Visibility.Collapsed : Visibility.Visible;
        PaneHeader.Foreground = pane.Error is not null || pane.Bad ? Palette.For(Tone.Bad) : Palette.Secondary;
        if (!ReferenceEquals(_paneShown, pane.Lines) || pane.Lines.Count == 0)
        {
            _paneShown = pane.Lines;
            PaneText.Text = pane.Lines.Count > 0 ? string.Join("\n", pane.Lines)
                : pane.Loading || pane.Error is not null ? "" : pane.EmptyText;
            if (pane.Lines.Count > 0)
                // The answer is in the tail; scroll once the text has been laid out.
                DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                    () => PaneScroll.ChangeView(0, PaneScroll.ScrollableHeight, null, disableAnimation: true));
        }
    }

    private void RenderCommand()
    {
        if (Top is not NavCommand nav) return;
        var site = _service.Snapshot.Org(nav.Org)?.SiteByKey(nav.SiteKey);
        SetHero(site?.Name ?? "", "Run a command", "", null);
        var spec = Actions.SiteCommand(nav.Org, site, CommandBox.Text);
        var armed = _armedKey != "" && _armedKey == CommandArmKey(nav, spec.ArmIntent);
        var sending = _busyKey.StartsWith("command-run|", StringComparison.Ordinal);
        CommandState.Text = armed ? _armedText
            : sending ? "Sending…"
            : spec.Available ? "Enter or Run asks before it sends" : Capitalize(spec.Reason);
        CommandState.Foreground = armed ? Palette.Accent : Palette.Secondary;
        RunButton.IsEnabled = spec.Available && !armed && !sending;
    }

    private void Flash(string message, bool bad = false)
    {
        FlashText.Text = message;
        FlashText.Foreground = bad ? Palette.For(Tone.Bad) : Palette.Accent;
        FlashText.Visibility = message == "" ? Visibility.Collapsed : Visibility.Visible;
        _flashTimer.Stop();
        _flashTimer.Start();
    }

    // --------------------------------------------------------- navigation

    private void Push(Nav nav)
    {
        nav.ReturnKey = CursorRow?.Key ?? "";
        _nav.Add(nav);
        _cursor = 0;
        _preferKey = null;
        _paneShown = null;
        Render();
        FocusContent();
    }

    /// <summary>A command prompt that has sent is replaced, not covered: backing onto it invites running it twice.</summary>
    private void ReplaceTop(Nav nav)
    {
        nav.ReturnKey = Top.ReturnKey;
        _nav[^1] = nav;
        _cursor = 0;
        _paneShown = null;
        Render();
        FocusContent();
    }

    private void Pop()
    {
        if (_nav.Count <= 1) return;
        var top = _nav[^1];
        _nav.RemoveAt(_nav.Count - 1);
        if (top is NavPane { Watch: { } watch }) watch.Stop();
        _preferKey = top.ReturnKey;
        _paneShown = null;
        Render();
        FocusContent();
    }

    private void Move(int delta)
    {
        if (_rows.Count == 0) return;
        var target = Math.Clamp(_cursor + delta, 0, _rows.Count - 1);
        // Step over section titles in the direction of travel; at an end, settle on the nearest real row.
        MoveTo(NearestSelectable(target, Math.Sign(delta) == 0 ? 1 : Math.Sign(delta)));
    }

    private int NearestSelectable(int index, int direction)
    {
        for (var i = index; i >= 0 && i < _rows.Count; i += direction)
            if (_rows[i].Selectable) return i;
        for (var i = index; i >= 0 && i < _rows.Count; i -= direction)
            if (_rows[i].Selectable) return i;
        return Math.Clamp(index, 0, Math.Max(0, _rows.Count - 1));
    }

    private void MoveTo(int index)
    {
        if (_rows.Count == 0) return;
        _cursor = NearestSelectable(Math.Clamp(index, 0, _rows.Count - 1), 1);
        RowList.SelectedIndex = _cursor;
        RowList.ScrollIntoView(_rows[_cursor]);
    }

    private void Activate(RowItem row)
    {
        switch (row.Kind)
        {
            case RowKind.Info when row.Payload as string == "settings":
                _openSettings();
                break;
            case RowKind.Server:
                ToggleFold(row);
                break;
            case RowKind.Site when row.Payload is Site site:
                Push(new NavSite { Org = row.Org, SiteKey = site.Key });
                break;
            case RowKind.Action when row.Payload is ActionSpec spec:
                RunAction(row, spec);
                break;
            case RowKind.Event when row.Payload is ForgeEvent ev && Top is NavList list:
                OpenEventOutput(list, ev);
                break;
            case RowKind.Recipe when row.Payload is Recipe recipe && Top is NavList list:
                ArmRecipe(list, row, recipe);
                break;
            case RowKind.More when Top is NavList list:
                _ = LoadList(list, list.Cursor);
                break;
        }
    }

    private void ToggleFold(RowItem row)
    {
        if (row.Expanded is null || row.Payload is not Server server) return;
        if (_collapsed.Remove(row.Key)) _ = _service.FetchServerSitesAsync(row.Org, server.Id);
        else _collapsed.Add(row.Key);
        Render();
    }

    private void DrillIn()
    {
        if (CursorRow is not { } row) return;
        switch (row.Kind)
        {
            case RowKind.Server when row.Payload is Server server:
                if (_collapsed.Contains(row.Key)) ToggleFold(row);
                else Push(new NavServer { Org = row.Org, ServerId = server.Id });
                break;
            default:
                Activate(row);
                break;
        }
    }

    private void Back()
    {
        if (_nav.Count > 1) { Pop(); return; }
        if (CursorRow is not { } row) return;
        if (row is { Kind: RowKind.Server, Expanded: true }) { ToggleFold(row); return; }
        if (row is { Kind: RowKind.Site, Payload: Site site })
        {
            var index = _rows.FindIndex(r => r.Key == ServerKey(row.Org, site.ServerId));
            if (index >= 0) MoveTo(index);
        }
    }

    // ------------------------------------------------------------ subjects

    private (string Org, Site Site)? CurrentSite()
    {
        var snap = _service.Snapshot;
        string org, key;
        switch (Top)
        {
            case NavSite s: org = s.Org; key = s.SiteKey; break;
            case NavCommand c: org = c.Org; key = c.SiteKey; break;
            case NavPane { SiteKey: not "" } p: org = p.Org; key = p.SiteKey; break;
            case NavList { SiteKey: not "" } l: org = l.Org; key = l.SiteKey; break;
            case NavTree when CursorRow is { Kind: RowKind.Site, Payload: Site s }: org = CursorRow.Org; key = s.Key; break;
            default: return null;
        }
        return snap.Org(org)?.SiteByKey(key) is { } site ? (org, site) : null;
    }

    private (string Org, Server Server)? CurrentServer()
    {
        var snap = _service.Snapshot;
        string org, id;
        switch (Top)
        {
            case NavServer s: org = s.Org; id = s.ServerId; break;
            case NavList { ServerId: not "" } l: org = l.Org; id = l.ServerId; break;
            case NavPane { ServerId: not "" } p: org = p.Org; id = p.ServerId; break;
            case NavTree when CursorRow is { Kind: RowKind.Server, Payload: Server s }: org = CursorRow.Org; id = s.Id; break;
            default:
                if (CurrentSite() is not { } cs) return null;
                org = cs.Org;
                id = cs.Site.ServerId;
                break;
        }
        return snap.Org(org)?.ServerById(id) is { } server ? (org, server) : null;
    }

    // ------------------------------------------------------------- actions

    /// <summary>
    /// A write asks before it is sent. Cancel is the dialog's default, so Enter
    /// and Esc both drop it; the write goes only by its own button or a capital Y,
    /// the same deliberate press the keyboard flow has always needed.
    /// </summary>
    private async void Arm(string key, string title, string body, string cta, Func<bool> valid, Func<Task> send)
    {
        if (_confirmDialog is not null) return;
        _armedKey = key;
        _armedText = "Waiting for you to confirm";
        _armedValid = valid;
        _armedSend = send;
        Render();

        var yes = await WriteConfirm.AskAsync(Root.XamlRoot, Root.ActualTheme, title, body, cta, d => _confirmDialog = d);
        if (_armedKey != key) return;
        if (yes) Confirm();
        else
        {
            Disarm();
            Flash("Not sent");
            Render();
            FocusContent();
        }
    }

    private void Disarm()
    {
        _armedKey = "";
        _armedText = "";
        _armedValid = null;
        _armedSend = null;
        _confirmDialog?.Hide();
    }

    private async void Confirm()
    {
        if (_armedKey == "" || _armedSend is null) return;
        var key = _armedKey;
        var valid = _armedValid;
        var send = _armedSend;
        Disarm();
        // What an arm pointed at can change under it — a refresh that flips maintenance mode, a site that went away.
        if (valid is not null && !valid())
        {
            Flash("That changed while it was armed — nothing was sent", bad: true);
            Render();
            return;
        }
        _busyKey = key;
        Render();
        try
        {
            await send();
        }
        catch (Exception e)
        {
            Flash(e.Message, bad: true);
        }
        finally
        {
            _busyKey = "";
            Render();
        }
    }

    private bool RowStillArmable(string key) =>
        Top switch
        {
            NavServer s => ServerActionRows(_service.Snapshot, s).Any(r => r.ArmKey == key && r.Enabled),
            NavSite s => SiteActionRows(_service.Snapshot, s).Any(r => r.ArmKey == key && r.Enabled),
            _ => false,
        };

    private void RunAction(RowItem row, ActionSpec spec)
    {
        if (!spec.Available)
        {
            Flash(spec.Reason == "" ? "Not available" : spec.Reason);
            return;
        }
        if (row.ArmKey != "")
        {
            if (spec.ArmsSubject) { ArmDeploy(); return; }
            var org = row.Org;
            var serverId = Top switch
            {
                NavServer s => s.ServerId,
                _ => CurrentSite()?.Site.ServerId ?? "",
            };
            var subject = Top is NavServer ? CurrentServer()?.Server.Name ?? "this server"
                : spec.Id == "maintenance" ? CurrentSite()?.Site.Name ?? "this site"
                : _service.Snapshot.Org(org)?.ServerById(serverId)?.Name ?? "this server";
            var (title, body, cta) = WriteConfirm.Wording(spec, subject);
            Arm(row.ArmKey, title, body, cta, () => RowStillArmable(row.ArmKey), async () =>
            {
                var outcome = await _service.SendActionAsync(org, serverId, spec);
                Flash(outcome.Message, !outcome.Ok);
            });
            return;
        }

        switch (spec.Id)
        {
            case "events": OpenList(ListKind.Events); break;
            case "recipes": OpenList(ListKind.Recipes); break;
            case "monitors": OpenList(ListKind.Monitors); break;
            case "heartbeats": OpenList(ListKind.Heartbeats); break;
            case "forge": OpenForge(); break;
            case "ssh": CopySsh(); break;
            case "ssh-terminal": OpenTerminal(); break;
            case "open": OpenSite(); break;
            case "log": OpenDeployLog(); break;
            case "command" when CurrentSite() is { } cs:
                CommandBox.Text = "";
                Push(new NavCommand { Org = cs.Org, SiteKey = cs.Site.Key });
                break;
            default:
                if (spec.Log != "") OpenSiteLog(spec.Log);
                break;
        }
    }

    private void ArmDeploy()
    {
        if (CurrentSite() is not { } cs)
        {
            Flash("Pick a site to deploy");
            return;
        }
        var (org, site) = cs;
        if (!Status.CanDeploy(site))
        {
            Flash(site.Name + " has no repository to deploy");
            return;
        }
        var serverName = _service.Snapshot.Org(org)?.ServerById(site.ServerId)?.Name ?? "its server";
        Arm(DeployKey(org, site.Key), $"Deploy {site.Name}?",
            $"Runs the deploy script on {serverName}. You can follow the log while it runs.", "Deploy",
            () => _service.Snapshot.Org(org)?.SiteByKey(site.Key) is { } s && Status.CanDeploy(s),
            async () =>
            {
                var latest = _service.Snapshot.Org(org)?.SiteByKey(site.Key) ?? site;
                var outcome = await _service.DeployAsync(org, latest);
                Flash(outcome.Ok ? $"{site.Name}: {outcome.Message}" : outcome.Message, !outcome.Ok);
            });
    }

    private static string CommandArmKey(NavCommand nav, string text) => $"command-run|{nav.Org}|{nav.SiteKey}|{text}";

    private void ArmCommand()
    {
        if (Top is not NavCommand nav) return;
        var site = _service.Snapshot.Org(nav.Org)?.SiteByKey(nav.SiteKey);
        var spec = Actions.SiteCommand(nav.Org, site, CommandBox.Text);
        if (!spec.Available || site is null)
        {
            Flash(spec.Reason);
            return;
        }
        var text = spec.ArmIntent;
        Arm(CommandArmKey(nav, text), "Run this command?",
            $"{text}\n\nRuns on {site.Name} as the site's user.", "Run",
            () => Top == nav && CommandBox.Text.Trim() == text,
            async () =>
            {
                var (outcome, watch) = await _service.RunCommandAsync(nav.Org, site, text);
                Flash(outcome.Message, !outcome.Ok);
                if (watch is null || Top != nav) return;
                OpenRun(new NavPane
                {
                    Org = nav.Org, SiteKey = site.Key, Crumb = site.Name, Title = "$ " + text,
                    FileName = $"forge-{ForgeText.SafeFileName(site.Name)}-command.out",
                }, watch, replace: true);
            });
    }

    private void ArmRecipe(NavList list, RowItem row, Recipe recipe)
    {
        var server = _service.Snapshot.Org(list.Org)?.ServerById(list.ServerId);
        var spec = Actions.RecipeRun(list.Org, server, recipe);
        if (!spec.Available || server is null)
        {
            Flash(spec.Reason);
            return;
        }
        Arm(row.ArmKey, $"Run {recipe.Name}?",
            $"Runs the recipe on {server.Name}" + (recipe.User == "" ? "." : $" as {recipe.User}."), "Run",
            () => _service.Snapshot.Org(list.Org)?.ServerById(list.ServerId)?.State == ServerState.Ready,
            async () =>
            {
                var (outcome, watch) = await _service.RunRecipeAsync(list.Org, server, recipe);
                Flash(outcome.Message, !outcome.Ok);
                if (watch is null) return;
                OpenRun(new NavPane
                {
                    Org = list.Org, ServerId = server.Id, Crumb = server.Name, Title = recipe.Name,
                    FileName = $"forge-{ForgeText.SafeFileName(server.Name)}-recipe-{ForgeText.SafeFileName(recipe.Name)}.out",
                }, watch, replace: false);
            });
    }

    private void OpenRun(NavPane pane, RunWatch watch, bool replace)
    {
        pane.Watch = watch;
        pane.Header = watch.Latest.Header;
        pane.Loading = true;
        pane.EmptyText = "No output";
        watch.Updated += update => DispatcherQueue.TryEnqueue(() =>
        {
            if (pane.Watch != watch) return;
            pane.Header = update.Header + (update.Message is null ? "" : " — " + update.Message);
            pane.Bad = update.Bad;
            if (update.Lines is not null) pane.Lines = update.Lines;
            pane.Loading = update.Running && update.Lines is null && update.Message is null;
            if (Top == pane) Render();
        });
        pane.Reload = () => { watch.Restart(); return Task.CompletedTask; };
        if (replace) ReplaceTop(pane);
        else Push(pane);
    }

    private void OpenDocument(NavPane pane, Func<Task<ReadResult<IReadOnlyList<string>>>> fetch)
    {
        pane.Reload = async () =>
        {
            pane.Loading = true;
            pane.Error = null;
            if (Top == pane) Render();
            var result = await fetch();
            pane.Loading = false;
            if (result.Ok) pane.Lines = result.Value!;
            else pane.Error = result.Error;
            if (Top == pane) Render();
        };
        Push(pane);
        _ = pane.Reload();
    }

    private void OpenDeployLog()
    {
        if (CurrentSite() is not { } cs) return;
        var (org, site) = cs;
        OpenDocument(new NavPane
        {
            Org = org, SiteKey = site.Key, Crumb = site.Name, Title = "Deployment log",
            Header = Status.SiteStatus(site).Label + (site.CommitHash == "" ? "" : " · " + site.CommitHash),
            Bad = Status.SiteTone(site) == Tone.Bad, EmptyText = "The deployment printed nothing",
            FileName = $"forge-{ForgeText.SafeFileName(site.Name)}-{ForgeText.SafeFileName(site.DeploymentId)}.log",
        }, () => _service.DeploymentLogAsync(org, site));
    }

    private void OpenSiteLog(string kind)
    {
        if (CurrentSite() is not { } cs) return;
        var (org, site) = cs;
        OpenDocument(new NavPane
        {
            Org = org, SiteKey = site.Key, Crumb = site.Name, Title = Actions.SiteLogLabel(kind),
            EmptyText = "The log is empty",
            FileName = $"forge-{ForgeText.SafeFileName(site.Name)}-{ForgeText.SafeFileName(kind)}.log",
        }, () => _service.SiteLogAsync(org, site, kind));
    }

    private void OpenEventOutput(NavList list, ForgeEvent ev)
    {
        var server = _service.Snapshot.Org(list.Org)?.ServerById(list.ServerId);
        OpenDocument(new NavPane
        {
            Org = list.Org, ServerId = list.ServerId, Crumb = server?.Name ?? "", Title = ev.Description,
            Header = string.Join(" · ", new[] { ev.SiteName, ev.RanAs == "" ? "" : "as " + ev.RanAs }.Where(s => s != "")),
            EmptyText = "The event printed nothing",
            FileName = $"forge-{ForgeText.SafeFileName(server?.Name)}-event-{ForgeText.SafeFileName(ev.Id)}.log",
        }, () => _service.EventOutputAsync(list.Org, list.ServerId, ev.Id));
    }

    private void OpenList(ListKind kind)
    {
        NavList list;
        if (kind == ListKind.Heartbeats)
        {
            if (CurrentSite() is not { } cs) return;
            list = new NavList { Kind = kind, Org = cs.Org, ServerId = cs.Site.ServerId, SiteKey = cs.Site.Key };
        }
        else
        {
            if (CurrentServer() is not { } server) { Flash("Pick a server first"); return; }
            list = new NavList { Kind = kind, Org = server.Org, ServerId = server.Server.Id };
        }
        if (Top is NavPane or NavCommand) return;
        Push(list);
        _ = LoadList(list, null);
    }

    private async Task LoadList(NavList list, string? cursor)
    {
        list.Loading = true;
        list.Error = null;
        if (Top == list) Render();

        (List<object>? Items, string? Error, string Next) result;
        switch (list.Kind)
        {
            case ListKind.Events:
            {
                var r = await _service.EventsAsync(list.Org, list.ServerId, cursor);
                result = (r.Value?.Cast<object>().ToList(), r.Error, r.NextCursor);
                break;
            }
            case ListKind.Recipes:
            {
                var r = await _service.RecipesAsync(list.Org, cursor);
                result = (r.Value?.Cast<object>().ToList(), r.Error, r.NextCursor);
                break;
            }
            case ListKind.Monitors:
            {
                var r = await _service.MonitorsAsync(list.Org, list.ServerId, cursor);
                result = (r.Value?.Cast<object>().ToList(), r.Error, r.NextCursor);
                break;
            }
            default:
            {
                var site = _service.Snapshot.Org(list.Org)?.SiteByKey(list.SiteKey);
                if (site is null) { result = (null, "That site is no longer listed", ""); break; }
                var r = await _service.HeartbeatsAsync(list.Org, site, cursor);
                result = (r.Value?.Cast<object>().ToList(), r.Error, r.NextCursor);
                break;
            }
        }

        list.Loading = false;
        if (result.Error is not null) list.Error = result.Error;
        else
        {
            if (cursor is null) list.Items = result.Items!;
            else list.Items.AddRange(result.Items!);
            list.Cursor = result.Next;
        }
        if (Top == list) Render();
    }

    private void OpenSite()
    {
        if (CurrentSite() is not { } cs) { Flash("Pick a site first"); return; }
        if (!Shell.OpenUrl(cs.Site.Url)) Flash(cs.Site.Name + " has no address to open");
    }

    private void OpenForge()
    {
        var template = _service.Setup.Settings.DashboardUrlTemplate;
        string url;
        string org;
        if (CurrentSite() is { } cs && Top is not NavServer && !(Top is NavTree && CursorRow?.Kind == RowKind.Server))
        {
            org = cs.Org;
            var server = _service.Snapshot.Org(org)?.ServerById(cs.Site.ServerId);
            url = server is null ? "" : ForgeText.DashboardUrl(template, org, server, cs.Site.Id);
        }
        else if (CurrentServer() is { } s)
        {
            org = s.Org;
            url = ForgeText.DashboardUrl(template, org, s.Server, null);
        }
        else
        {
            org = CursorRow?.Org is { Length: > 0 } o ? o : _service.Setup.DefaultOrganization;
            url = "";
        }
        if (url == "") url = ForgeText.OrgDashboardUrl(org);
        Shell.OpenUrl(url);
    }

    private void CopySsh()
    {
        if (CurrentServer() is not { } s) { Flash("Pick a server or site first"); return; }
        var command = ForgeText.SshCommand(s.Server);
        if (command == "") { Flash(s.Server.Name + " has no public IP"); return; }
        try
        {
            Shell.CopyText(command);
            Flash("Copied: " + command);
        }
        catch (Exception e)
        {
            Flash("Could not copy: " + e.Message, bad: true);
        }
    }

    private void OpenTerminal()
    {
        if (CurrentServer() is not { } s) { Flash("Pick a server or site first"); return; }
        if (!Shell.OpenSsh(s.Server)) Flash(s.Server.Name + " has no address ssh can use", bad: true);
        else HidePanel();
    }

    private void Refresh()
    {
        switch (Top)
        {
            case NavPane { Reload: { } reload }:
                _ = reload();
                break;
            case NavList list:
                _ = LoadList(list, null);
                break;
            default:
                _service.RefreshNow();
                Flash("Refreshing…");
                break;
        }
    }

    private void CopyPane(NavPane pane)
    {
        if (pane.Lines.Count == 0) { Flash("Nothing to copy"); return; }
        Shell.CopyText(string.Join(Environment.NewLine, pane.Lines));
        Flash($"Copied {ForgeText.Pluralize(pane.Lines.Count, "line")}");
    }

    private void SavePane(NavPane pane)
    {
        if (pane.Lines.Count == 0) { Flash("Nothing to save"); return; }
        try
        {
            var folder = Shell.DownloadsFolder();
            var name = Path.GetFileNameWithoutExtension(pane.FileName);
            var ext = Path.GetExtension(pane.FileName);
            var path = Path.Combine(folder, pane.FileName);
            for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name}-{i}{ext}");
            File.WriteAllText(path, string.Join(Environment.NewLine, pane.Lines) + Environment.NewLine);
            Flash("Saved to " + path);
        }
        catch (Exception e)
        {
            Flash("Could not save: " + e.Message, bad: true);
        }
    }

    // ---------------------------------------------------------------- input

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static bool IsModifier(VirtualKey key) => key is VirtualKey.Shift or VirtualKey.LeftShift or VirtualKey.RightShift
        or VirtualKey.Control or VirtualKey.LeftControl or VirtualKey.RightControl or VirtualKey.Menu
        or VirtualKey.LeftMenu or VirtualKey.RightMenu or VirtualKey.LeftWindows or VirtualKey.RightWindows
        or VirtualKey.CapitalLock;

    private const VirtualKey Comma = (VirtualKey)188;
    private const VirtualKey Slash = (VirtualKey)191;

    private bool IsInside(DependencyObject target)
    {
        if (Root.XamlRoot is null) return false;
        for (var node = FocusManager.GetFocusedElement(Root.XamlRoot) as DependencyObject; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node == target) return true;
        return false;
    }

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var key = e.Key;
        if (IsModifier(key)) return;
        var shift = IsDown(VirtualKey.Shift);
        var ctrl = IsDown(VirtualKey.Control);

        // While the confirm dialog is up it owns the keyboard; nothing here may act behind it.
        if (_confirmDialog is not null) return;

        if (ctrl && key == VirtualKey.O)
        {
            e.Handled = true;
            OpenWindow();
            return;
        }

        if (Top is NavTree && ctrl && key == VirtualKey.F)
        {
            e.Handled = true;
            SearchBox.Focus(FocusState.Keyboard);
            return;
        }

        // Typing in the search box is text, not shortcuts; only the keys that leave it are taken.
        if (IsInside(SearchBox))
        {
            switch (key)
            {
                case VirtualKey.Escape:
                    e.Handled = true;
                    if (SearchBox.Text != "") SearchBox.Text = "";
                    else RowList.Focus(FocusState.Programmatic);
                    break;
                case VirtualKey.Down or VirtualKey.Enter:
                    e.Handled = true;
                    MoveTo(0);
                    RowList.Focus(FocusState.Programmatic);
                    if (key == VirtualKey.Enter && CursorRow is { } first) Activate(first);
                    break;
            }
            return;
        }

        if (Top is NavCommand)
        {
            if (key == VirtualKey.Enter) { e.Handled = true; ArmCommand(); }
            else if (key == VirtualKey.Escape) { e.Handled = true; Pop(); }
            return;
        }

        if (Top is NavPane pane)
        {
            switch (key)
            {
                case VirtualKey.C when !ctrl: CopyPane(pane); e.Handled = true; return;
                case VirtualKey.W: SavePane(pane); e.Handled = true; return;
                case VirtualKey.R: Refresh(); e.Handled = true; return;
                case VirtualKey.D: ArmDeploy(); e.Handled = true; return;
                case VirtualKey.Escape or VirtualKey.Left or VirtualKey.H or VirtualKey.Back: Pop(); e.Handled = true; return;
                case VirtualKey.J: PaneScroll.ChangeView(null, PaneScroll.VerticalOffset + 48, null); e.Handled = true; return;
                case VirtualKey.K: PaneScroll.ChangeView(null, PaneScroll.VerticalOffset - 48, null); e.Handled = true; return;
                default: return;
            }
        }

        if (ctrl) return;
        e.Handled = true;
        switch (key)
        {
            case VirtualKey.Up or VirtualKey.K: Move(-1); break;
            case VirtualKey.Down or VirtualKey.J: Move(1); break;
            case VirtualKey.PageUp: Move(-8); break;
            case VirtualKey.PageDown: Move(8); break;
            case VirtualKey.Home: MoveTo(0); break;
            case VirtualKey.End: MoveTo(_rows.Count - 1); break;
            case VirtualKey.Enter or VirtualKey.Space: if (CursorRow is { } row) Activate(row); break;
            case VirtualKey.Right or VirtualKey.L: DrillIn(); break;
            case VirtualKey.Left or VirtualKey.H or VirtualKey.Back: Back(); break;
            case VirtualKey.Escape:
                if (_nav.Count > 1) Pop();
                else if (_query != "") SearchBox.Text = "";
                else HidePanel();
                break;
            case VirtualKey.D: ArmDeploy(); break;
            case VirtualKey.M: ArmMaintenance(); break;
            case Slash when shift: KeysFlyout.ShowAt(KeysButton); break;
            case VirtualKey.O: OpenSite(); break;
            case VirtualKey.F: OpenForge(); break;
            case VirtualKey.S: CopySsh(); break;
            case VirtualKey.T: OpenTerminal(); break;
            case VirtualKey.E: OpenList(ListKind.Events); break;
            case VirtualKey.R: Refresh(); break;
            case Comma: _openSettings(); break;
            default: e.Handled = false; break;
        }
    }

    private void RowList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not RowItem { Selectable: true } row) return;
        _cursor = _rows.IndexOf(row);
        Activate(row);
    }

    /// <summary>Section titles can't be clicked, hovered or tabbed to.</summary>
    private void RowList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.Item is not RowItem row) return;
        args.ItemContainer.IsHitTestVisible = row.Selectable;
        args.ItemContainer.IsTabStop = row.Selectable;
        if (args.ItemContainer.ContentTemplateRoot is Grid grid) ShowQuickActions(grid, false);
    }

    private void RowList_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is not FrameworkElement { DataContext: RowItem row } source) return;
        if (!SelectRow(row.Key)) return;
        if (MenuFor(row) is not { } menu) return;
        e.Handled = true;
        menu.ShowAt(source, new FlyoutShowOptions { Position = e.GetPosition(source) });
    }

    /// <summary>Puts the cursor on a row, so the actions that act on "the current site" act on that one.</summary>
    private bool SelectRow(string key)
    {
        var index = _rows.FindIndex(r => r.Key == key);
        if (index < 0) return false;
        MoveTo(index);
        return true;
    }

    /// <summary>A site's or server's right-click menu: the everyday actions, and a way to all of them.</summary>
    private MenuFlyout? MenuFor(RowItem row)
    {
        var menu = new MenuFlyout();
        void Item(string text, string glyph, string keys, Action run, bool enabled = true)
        {
            var item = new MenuFlyoutItem
            {
                Text = text,
                Icon = new FontIcon { Glyph = glyph, FontFamily = (FontFamily)Application.Current.Resources["IconFont"] },
                KeyboardAcceleratorTextOverride = keys,
                IsEnabled = enabled,
            };
            item.Click += (_, _) => run();
            menu.Items.Add(item);
        }
        void Separator() => menu.Items.Add(new MenuFlyoutSeparator());

        switch (row)
        {
            case { Kind: RowKind.Site, Payload: Site site }:
                Item("Deploy", "", "D", ArmDeploy, Status.CanDeploy(site));
                Item("Open site", "", "O", OpenSite, ForgeText.ExternalUrl(site.Url) != "");
                Item("Open in Forge", "", "F", OpenForge);
                Separator();
                Item("Deployment log", "", "", OpenDeployLog, site.DeploymentId != "");
                Item("Application log", "", "", () => OpenSiteLog("application"));
                Separator();
                Item("Copy ssh command", "", "S", CopySsh);
                Item("Open ssh in Windows Terminal", "", "T", OpenTerminal);
                Separator();
                Item("All actions", "", "Enter", () => Push(new NavSite { Org = row.Org, SiteKey = site.Key }));
                return menu;
            case { Kind: RowKind.Server, Payload: Server server }:
                Item("Server actions", "", "→", () => Push(new NavServer { Org = row.Org, ServerId = server.Id }));
                Item("Server events", "", "E", () => OpenList(ListKind.Events));
                Item("Open in Forge", "", "F", OpenForge);
                Separator();
                Item("Copy ssh command", "", "S", CopySsh);
                Item("Open ssh in Windows Terminal", "", "T", OpenTerminal);
                if (row.Expanded is { } open)
                {
                    Separator();
                    Item(open ? "Fold" : "Unfold", open ? "" : "", "Enter", () => ToggleFold(row));
                }
                return menu;
            default:
                return null;
        }
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid { DataContext: RowItem { HasQuickActions: true } } grid) ShowQuickActions(grid, true);
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Grid grid) ShowQuickActions(grid, false);
    }

    /// <summary>On hover a row trades its status for buttons, the way File Explorer's rows do.</summary>
    private static void ShowQuickActions(Grid grid, bool show)
    {
        if (grid.FindName("QuickActions") is UIElement actions) actions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (grid.FindName("Trailing") is UIElement trailing && grid.DataContext is RowItem row)
            trailing.Visibility = show ? Visibility.Collapsed : row.BodyVisibility;
    }

    private void OnRow(object sender, Action run)
    {
        if (sender is FrameworkElement { Tag: string key } && SelectRow(key)) run();
    }

    private void QuickDeploy_Click(object sender, RoutedEventArgs e) => OnRow(sender, ArmDeploy);
    private void QuickOpen_Click(object sender, RoutedEventArgs e) => OnRow(sender, OpenSite);
    private void QuickEvents_Click(object sender, RoutedEventArgs e) => OnRow(sender, () => OpenList(ListKind.Events));
    private void QuickTerminal_Click(object sender, RoutedEventArgs e) => OnRow(sender, OpenTerminal);

    private void QuickMore_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key } button || !SelectRow(key) || CursorRow is not { } row) return;
        MenuFor(row)?.ShowAt(button, new FlyoutShowOptions { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight });
    }

    private void ArmMaintenance()
    {
        if (Top is not NavSite nav) { Flash("Open a site first"); return; }
        var row = SiteActionRows(_service.Snapshot, nav).FirstOrDefault(r => r.Payload is ActionSpec { Id: "maintenance" });
        if (row?.Payload is ActionSpec spec) RunAction(row, spec);
    }

    private void MaintenanceSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncingSwitch) return;
        // The switch only asks; it shows the site's real state until Forge reports the change.
        _syncingSwitch = true;
        MaintenanceSwitch.IsOn = !MaintenanceSwitch.IsOn;
        _syncingSwitch = false;
        ArmMaintenance();
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (_query == sender.Text) return;
        _query = sender.Text;
        _cursor = 0;
        if (Top is NavTree) Render();
    }

    private void OrgBar_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (_syncingOrgBar) return;
        _orgFilter = sender.SelectedItem?.Tag as string ?? "";
        _cursor = 0;
        Render();
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => Pop();
    private void DeployButton_Click(object sender, RoutedEventArgs e) => ArmDeploy();
    private void OpenSiteButton_Click(object sender, RoutedEventArgs e) => OpenSite();
    private void SiteInfoLink_Click(object sender, RoutedEventArgs e) => OpenDeployLog();
    private void RunButton_Click(object sender, RoutedEventArgs e) => ArmCommand();
    private void PaneReload_Click(object sender, RoutedEventArgs e) => Refresh();
    private void PaneCopy_Click(object sender, RoutedEventArgs e) { if (Top is NavPane p) CopyPane(p); }
    private void PaneSave_Click(object sender, RoutedEventArgs e) { if (Top is NavPane p) SavePane(p); }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => Refresh();

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => _openSettings();

    private void WindowButton_Click(object sender, RoutedEventArgs e) => OpenWindow();

    /// <summary>Moves to the expanded window, on whatever site the flyout is showing or pointing at.</summary>
    private void OpenWindow()
    {
        var current = CurrentSite();
        HidePanel();
        _openWindow(current?.Org, current?.Site.Key);
    }

    // ---------------------------------------------------------------- win32

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);
}

// ------------------------------------------------------------------ routes

internal abstract class Nav
{
    public string Org { get; init; } = "";
    /// <summary>The row the view was opened from, so backing out lands on it.</summary>
    public string ReturnKey { get; set; } = "";
}

internal sealed class NavTree : Nav;

internal sealed class NavServer : Nav
{
    public string ServerId { get; init; } = "";
}

internal sealed class NavSite : Nav
{
    public string SiteKey { get; init; } = "";
}

internal sealed class NavCommand : Nav
{
    public string SiteKey { get; init; } = "";
}

internal enum ListKind { Events, Recipes, Monitors, Heartbeats }

internal sealed class NavList : Nav
{
    public ListKind Kind { get; init; }
    public string ServerId { get; init; } = "";
    public string SiteKey { get; init; } = "";
    public List<object> Items { get; set; } = [];
    public string Cursor { get; set; } = "";
    public bool Loading { get; set; }
    public string? Error { get; set; }
}

internal sealed class NavPane : Nav
{
    public string ServerId { get; init; } = "";
    public string SiteKey { get; init; } = "";
    public string Crumb { get; init; } = "";
    public string Title { get; init; } = "";
    public string Header { get; set; } = "";
    public bool Bad { get; set; }
    public IReadOnlyList<string> Lines { get; set; } = [];
    public bool Loading { get; set; } = true;
    public string? Error { get; set; }
    public string EmptyText { get; set; } = "Nothing in it";
    public string FileName { get; init; } = "forge.log";
    public Func<Task>? Reload { get; set; }
    public RunWatch? Watch { get; set; }
}
