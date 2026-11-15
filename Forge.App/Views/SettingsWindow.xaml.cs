using System.Runtime.InteropServices;
using Forge.App.Services;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Service;
using Forge.Core.Setup;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;

namespace Forge.App.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly ForgeService _service;
    private ForgeUser? _verifiedUser;
    private string _verifiedToken = "";
    private string? _confirmRemove;

    public SettingsWindow(ForgeService service)
    {
        _service = service;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        SystemBackdrop = new MicaBackdrop();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "forge.ico");
        if (File.Exists(icon)) AppWindow.SetIcon(icon);

        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(720 * scale), (int)(860 * scale)));
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - AppWindow.Size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - AppWindow.Size.Height) / 2)));

        // Braces would be read as markup extensions in XAML.
        TemplateHelp.Text = "{org} is the organization slug, {server} the server's slug, {serverId} its id and {site} the site id. "
                            + "A server's link drops the {site} part.";
        PathsText.Text = $"State: {_service.SetupPath}\nLog:   {Log.Path}\nTokens: Credential Manager → Windows Credentials → {WindowsCredentialStore.TargetPrefix}<account>";
        LoadSettings();
        RenderAccounts();
    }

    public void ShowAndFocus()
    {
        AppWindow.Show();
        Activate();
    }

    public void OnServiceChanged() => RenderAccounts();

    private void LoadSettings()
    {
        var s = _service.Setup.Settings;
        IntervalBox.Value = s.RefreshIntervalSec;
        NotifySwitch.IsOn = s.NotifyDeployments;
        StartupSwitch.IsOn = s.StartWithWindows;
        TemplateBox.Text = s.DashboardUrlTemplate;
    }

    private void RenderAccounts()
    {
        AccountsPanel.Children.Clear();
        var setup = _service.Setup;
        if (setup.Accounts.Count == 0)
        {
            AccountsPanel.Children.Add(Caption("No accounts yet — add a token below."));
            return;
        }
        foreach (var account in setup.Accounts)
        {
            var orgs = setup.Organizations.Where(o => o.Account == account.Name).Select(o => o.DisplayName).ToList();
            var hasToken = _service.HasToken(account.Name);
            var card = new Grid { ColumnSpacing = 12, Padding = new Thickness(14, 10, 10, 10) };
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            card.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var text = new StackPanel { Spacing = 2 };
            text.Children.Add(new TextBlock
            {
                Text = account.Label == account.Name ? account.Name : $"{account.Label} ({account.Name})",
                Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"],
            });
            if (account.User != "") text.Children.Add(Caption(account.User));
            text.Children.Add(Caption(orgs.Count == 0 ? "No organizations watched" : "Watching " + string.Join(", ", orgs)));
            if (!hasToken) text.Children.Add(Caption("No token in Credential Manager — add it again below", Palette.For(Tone.Bad)));
            card.Children.Add(text);

            var confirming = _confirmRemove == account.Name;
            var remove = new Button
            {
                Content = confirming ? "Remove token and account" : "Remove",
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (confirming) remove.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
            var name = account.Name;
            remove.Click += (_, _) =>
            {
                // Two presses: this deletes the stored token, which cannot be read back.
                if (_confirmRemove != name) { _confirmRemove = name; RenderAccounts(); return; }
                _confirmRemove = null;
                _service.RemoveAccount(name);
            };
            Grid.SetColumn(remove, 1);
            card.Children.Add(remove);

            AccountsPanel.Children.Add(new Border
            {
                Child = card,
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
                Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            });
        }
    }

    private static TextBlock Caption(string text, Microsoft.UI.Xaml.Media.Brush? brush = null) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = brush ?? Palette.Secondary,
    };

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        VerifyInfo.Severity = severity;
        VerifyInfo.Title = title;
        VerifyInfo.Message = message;
        VerifyInfo.IsOpen = true;
    }

    private async void VerifyButton_Click(object sender, RoutedEventArgs e)
    {
        var token = TokenBox.Password.Trim();
        OrgPicker.Visibility = Visibility.Collapsed;
        _verifiedUser = null;
        VerifyButton.IsEnabled = false;
        VerifyRing.IsActive = true;
        try
        {
            var (user, orgs, error) = await _service.VerifyTokenAsync(token);
            if (user is null)
            {
                ShowInfo(InfoBarSeverity.Error, "Token not accepted", error ?? "Unknown error");
                return;
            }

            _verifiedUser = user;
            _verifiedToken = token;
            var existing = _service.Setup.Accounts.FirstOrDefault(a => a.User != "" && a.User == user.Email);
            ShowInfo(error is null ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                $"Token belongs to {user.Name}" + (user.Email != "" ? $" <{user.Email}>" : ""),
                error ?? (existing is not null
                    ? $"That is the account '{existing.Name}', already set up — saving updates it."
                    : $"It can see {ForgeText.Pluralize(orgs.Count, "organization")}."));

            OrgChecks.Children.Clear();
            foreach (var org in orgs)
            {
                // An organization is reached through exactly one account; say so when another one has it.
                var entry = _service.Setup.Org(org.Slug);
                var other = entry is not null && entry.Account != existing?.Name ? entry.Account : null;
                var label = org.Name == org.Slug ? org.Slug : $"{org.Name}  ({org.Slug})";
                OrgChecks.Children.Add(new CheckBox
                {
                    Content = label + (other is null ? "" : $" — now watched through '{other}'"),
                    Tag = org,
                    // An existing account keeps what it watches; a new one starts with everything nobody else watches.
                    IsChecked = existing is not null ? entry?.Account == existing.Name : other is null,
                });
            }
            if (orgs.Count == 0) OrgChecks.Children.Add(Caption("This token cannot see any organizations."));
            OrgPicker.Visibility = Visibility.Visible;
            if (existing is not null && AccountNameBox.Text.Trim() == "") AccountNameBox.Text = existing.Label;
        }
        finally
        {
            VerifyButton.IsEnabled = true;
            VerifyRing.IsActive = false;
        }
    }

    private void SaveAccountButton_Click(object sender, RoutedEventArgs e)
    {
        if (_verifiedUser is null || _verifiedToken == "") return;
        if (TokenBox.Password.Trim() != _verifiedToken)
        {
            ShowInfo(InfoBarSeverity.Warning, "The token changed", "Check it again before saving.");
            OrgPicker.Visibility = Visibility.Collapsed;
            return;
        }
        var chosen = OrgChecks.Children.OfType<CheckBox>()
            .Where(c => c.IsChecked == true && c.Tag is Organization)
            .Select(c => (Organization)c.Tag)
            .Select(o => new OrgEntry(o.Slug, "", o.Name))
            .ToList();
        try
        {
            var name = _service.SaveAccount(AccountNameBox.Text, _verifiedToken, _verifiedUser, chosen);
            ShowInfo(InfoBarSeverity.Success, "Saved", $"Account '{name}' is watching {ForgeText.Pluralize(chosen.Count, "organization")}.");
            TokenBox.Password = "";
            AccountNameBox.Text = "";
            OrgPicker.Visibility = Visibility.Collapsed;
            _verifiedToken = "";
            _verifiedUser = null;
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "Could not save", ex.Message);
        }
    }

    private void SaveSettingsButton_Click(object sender, RoutedEventArgs e)
    {
        var interval = double.IsNaN(IntervalBox.Value) ? 60 : (int)IntervalBox.Value;
        var settings = new AppSettings
        {
            RefreshIntervalSec = interval,
            NotifyDeployments = NotifySwitch.IsOn,
            StartWithWindows = StartupSwitch.IsOn,
            DashboardUrlTemplate = TemplateBox.Text,
        }.Normalized();
        _service.UpdateSettings(settings);
        StartupRegistration.Sync(settings.StartWithWindows);
        LoadSettings();
        SettingsSaved.Text = "Saved";
    }

    private void TokenLink_Click(object sender, RoutedEventArgs e) => Shell.OpenUrl("https://forge.laravel.com/profile/api");

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Shell.RevealFolder(Path.GetDirectoryName(_service.SetupPath)!);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);
}
