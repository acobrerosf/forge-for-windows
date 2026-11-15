using Forge.App.Services;
using Forge.App.Tray;
using Forge.App.Views;
using Forge.Core.Api;
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

        Notifications.Initialize(url => _ui.TryEnqueue(() => Shell.OpenUrl(url)));

        _panel = new PanelWindow(_service, OpenSettings);
        _tray = new TrayIconController(TogglePanel, OpenSettings, _service.RefreshNow, Quit);

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
            if (toast.Arguments.TryGetValue("url", out var url)) Shell.OpenUrl(url);
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
    }

    private void TogglePanel() => _panel?.Toggle();

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
        _panel?.CloseForGood();
        await _service.DisposeAsync();
        Exit();
    }
}
