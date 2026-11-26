using Forge.App.Services;
using Forge.App.Tray;
using Forge.App.Views;
using Forge.Core.Api;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Service;
using Forge.Core.Setup;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Microsoft.Windows.AppNotifications;

namespace Forge.App;

public partial class App : Application
{
    private DispatcherQueue _ui = null!;
    private ForgeService _service = null!;
    private TrayIconController? _tray;
    private PanelWindow? _panel;
    private SettingsWindow? _settings;
    private ForgeWindow? _window;
    private bool _quitting;

    public App()
    {
        InitializeComponent();
        // A tray app has no window most of the time; it ends only when asked to.
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) =>
        {
            Log.Write("Unhandled: " + e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _ui = DispatcherQueue.GetForCurrentThread();
        _service = new ForgeService(new ForgeClient(), new WindowsCredentialStore(), new StateStore());

        Notifications.Initialize(args => _ui.TryEnqueue(() => OnToast(args)));

        _panel = new PanelWindow(_service, OpenSettings, OpenWindow);
        _tray = new TrayIconController(TogglePanel, () => OpenWindow(null, null), OpenSettings, _service.RefreshNow, Quit,
            () => _service.Setup.Settings.NotifyDeployments,
            on => _service.UpdateSettings(_service.Setup.Settings with { NotifyDeployments = on }));

        _service.Changed += () => _ui.TryEnqueue(OnServiceChanged);
        _service.DeploymentChanged += notice => _ui.TryEnqueue(() => Notifications.ShowDeployment(notice));
        AppInstance.GetCurrent().Activated += (_, e) => _ui.TryEnqueue(() => OnActivated(e, launch: false));

        _service.Start();
        StartupRegistration.Sync(_service.Setup.Settings.StartWithWindows);
        OnServiceChanged();

        OnActivated(AppInstance.GetCurrent().GetActivatedEventArgs(), launch: true);
    }

    private void OnActivated(AppActivationArguments activation, bool launch)
    {
        if (activation.Kind == ExtendedActivationKind.AppNotification &&
            activation.Data is AppNotificationActivatedEventArgs toast)
        {
            OnToast(toast.Arguments);
            return;
        }

        var background = launch && Environment.GetCommandLineArgs().Contains("--background", StringComparer.OrdinalIgnoreCase);
        if (_service.Setup.Organizations.Count == 0) OpenSettings();
        else if (!background) _panel?.ShowPanel();
    }

    private void OnServiceChanged()
    {
        var snapshot = _service.Snapshot;
        _tray?.Update(snapshot);
        _panel?.OnServiceChanged();
        _settings?.OnServiceChanged();
        _window?.OnServiceChanged();
#if DEBUG
        ShowTestToast(snapshot);
#endif
    }

#if DEBUG
    // A development build can show a sample deployment toast for the first listed site, so its
    // buttons can be tried without deploying anything. Off unless FORGE_TEST_TOAST is "done" or "failed".
    private static readonly string? TestToast = Environment.GetEnvironmentVariable("FORGE_TEST_TOAST");
    private bool _testToastShown;

    private void ShowTestToast(ForgeSnapshot snapshot)
    {
        if (_testToastShown || TestToast is not ("done" or "failed")) return;
        foreach (var org in snapshot.Orgs)
        {
            var site = org.Servers.SelectMany(s => org.SitesOf(s.Id)).FirstOrDefault(s => s.Url != "");
            if (site is null) continue;
            _testToastShown = true;
            var failed = TestToast == "failed";
            Notifications.ShowDeployment(new DeploymentNotice(org.Slug, org.Label, site, failed, site.Name,
                failed ? "Test: deployment failed" : "Test: deployed", site.Url));
            // Windows won't take a scripted click on a toast, so FORGE_TEST_TOAST_ACTION plays one of its buttons instead.
            if (Environment.GetEnvironmentVariable("FORGE_TEST_TOAST_ACTION") is { Length: > 0 } action)
                OnToast(new Dictionary<string, string> { ["action"] = action, ["org"] = org.Slug, ["site"] = site.Key, ["url"] = site.Url });
            return;
        }
    }
#endif

    /// <summary>A click on a deployment toast (the body opens the site) or on one of its buttons.</summary>
    private void OnToast(IDictionary<string, string> args)
    {
        args.TryGetValue("url", out var url);
        args.TryGetValue("org", out var org);
        args.TryGetValue("site", out var siteKey);
#if DEBUG
        Log.Write("Toast clicked: " + string.Join(", ", args.Select(a => a.Key + "=" + a.Value)));
#endif
        switch (args.TryGetValue("action", out var action) ? action : "open")
        {
            case "log" when org is not null && siteKey is not null:
                _panel?.ShowDeploymentLog(org, siteKey);
                break;
            case "forge" when org is not null && siteKey is not null:
                OpenInForge(org, siteKey);
                break;
            default:
                if (!string.IsNullOrEmpty(url)) Shell.OpenUrl(url);
                break;
        }
    }

    private void OpenInForge(string org, string siteKey)
    {
        var view = _service.Snapshot.Org(org);
        var site = view?.SiteByKey(siteKey);
        var server = site is null ? null : view!.ServerById(site.ServerId);
        var url = server is null ? "" : ForgeText.DashboardUrl(_service.Setup.Settings.DashboardUrlTemplate, org, server, site!.Id);
        Shell.OpenUrl(url == "" ? ForgeText.OrgDashboardUrl(org) : url);
    }

    private void TogglePanel() => _panel?.Toggle();

    /// <summary>The expanded window: one at a time, made on first use and dropped when closed.</summary>
    private void OpenWindow(string? org, string? siteKey, string? tab = null, string? serverId = null)
    {
        if (_window is null)
        {
            _window = new ForgeWindow(_service, OpenSettings);
            _window.Closed += (_, _) => _window = null;
        }
        _window.ShowAndFocus(org, siteKey, tab, serverId);
    }

    private void OpenSettings()
    {
        _panel?.HidePanel();
        if (_settings is null)
        {
            _settings = new SettingsWindow(_service);
            _settings.Closed += (_, _) => _settings = null;
        }
        _settings.ShowAndFocus();
    }

    private async void Quit()
    {
        if (_quitting) return;
        _quitting = true;
        _tray?.Dispose();
        _tray = null;
        _settings?.Close();
        _window?.Close();
        _panel?.CloseForGood();
        await _service.DisposeAsync();
        Exit();
    }
}
