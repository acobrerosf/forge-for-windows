using System.Windows.Input;
using Forge.Core.Model;
using Forge.Core.Service;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;

namespace Forge.App.Tray;

/// <summary>
/// One tray icon for every watched organization — the Windows counterpart of
/// the Omarchy bar widget. Left click toggles the panel; right click is a menu.
/// </summary>
internal sealed class TrayIconController : IDisposable
{
    private readonly TaskbarIcon _icon;
    private readonly DispatcherQueueTimer _pulse;
    private readonly ToggleMenuFlyoutItem _notifyItem;
    private readonly Func<bool> _notifying;
    private Badge _badge = Badge.Warn;
    private bool _lit = true;

    /// <param name="notifying">Whether deployment toasts are on.</param>
    /// <param name="setNotifying">Turns them on or off, as the Notifications switch in Settings does.</param>
    public TrayIconController(Action toggle, Action settings, Action refresh, Action quit,
        Func<bool> notifying, Action<bool> setNotifying)
    {
        _notifying = notifying;
        // The Windows tray convention: the default action first, then quick switches, then Quit last.
        // (The menu is drawn natively, which ignores font weight, so the default item can't be bold.)
        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = "Open Forge", Command = new RelayCommand(toggle) });
        menu.Items.Add(new MenuFlyoutItem { Text = "Refresh now", Command = new RelayCommand(refresh) });
        menu.Items.Add(new MenuFlyoutSeparator());
        _notifyItem = new ToggleMenuFlyoutItem
        {
            Text = "Deployment notifications",
            IsChecked = notifying(),
            // Mute and unmute without opening Settings.
            Command = new RelayCommand(() =>
            {
                setNotifying(!_notifying());
                _notifyItem!.IsChecked = _notifying();
            }),
        };
        menu.Items.Add(_notifyItem);
        menu.Items.Add(new MenuFlyoutItem { Text = "Settings", Command = new RelayCommand(settings) });
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(new MenuFlyoutItem { Text = "Quit Forge", Command = new RelayCommand(quit) });

        _icon = new TaskbarIcon
        {
            ToolTipText = "Forge",
            NoLeftClickDelay = true,
            ContextMenuMode = ContextMenuMode.PopupMenu,
            ContextFlyout = menu,
            LeftClickCommand = new RelayCommand(toggle),
            Icon = IconFactory.Get(_badge, true),
            MenuActivation = PopupActivationMode.RightClick,
        };
        _icon.ForceCreate(enablesEfficiencyMode: false);

        _pulse = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _pulse.Interval = TimeSpan.FromMilliseconds(650);
        _pulse.Tick += (_, _) =>
        {
            _lit = !_lit;
            _icon.UpdateIcon(IconFactory.Get(_badge, _lit));
        };
    }

    public void Update(ForgeSnapshot snapshot)
    {
        // Settings can change the same switch; keep the menu's tick in step with it.
        _notifyItem.IsChecked = _notifying();
        var badge = snapshot.NeedsSetup ? Badge.Warn : Status.BadgeFor(snapshot.Health);
        var tip = snapshot.Summary;
        // The shell truncates tray tooltips at 127 characters.
        _icon.ToolTipText = tip.Length > 120 ? tip[..119] + "…" : tip;
        if (badge == _badge) return;
        _badge = badge;
        _lit = true;
        _icon.UpdateIcon(IconFactory.Get(badge, true));
        if (badge == Badge.Busy) _pulse.Start();
        else _pulse.Stop();
    }

    public void Dispose()
    {
        _pulse.Stop();
        _icon.Dispose();
    }
}

internal sealed class RelayCommand(Action run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run();
}
