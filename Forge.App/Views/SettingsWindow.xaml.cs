using System.Reflection;
using System.Runtime.InteropServices;
using Forge.App.Services;
using Forge.Core.Model;
using Forge.Core.Secrets;
using Forge.Core.Service;
using Forge.Core.Setup;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.System;

namespace Forge.App.Views;

/// <summary>
/// Settings laid out like Windows' own: sections on the left, cards on the
/// right, and every change applied as it is made.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private readonly ForgeService _service;
    private ForgeUser? _verifiedUser;
    private string _verifiedToken = "";
    private bool _loading;
    /// <summary>Which account cards are open, so a background refresh redrawing them doesn't fold them shut.</summary>
    private readonly HashSet<string> _openAccounts = new(StringComparer.Ordinal);

    public SettingsWindow(ForgeService service)
    {
        _service = service;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        SystemBackdrop = new MicaBackdrop();
        var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "forge.ico");
        if (File.Exists(icon))
        {
            AppWindow.SetIcon(icon);
            foreach (var image in new[] { TitleIcon, PaneIcon, AboutIcon })
                image.Source = new BitmapImage(new Uri(icon));
        }

        var scale = GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(980 * scale), (int)(720 * scale)));
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Move(new PointInt32(
            work.X + Math.Max(0, (work.Width - AppWindow.Size.Width) / 2),
            work.Y + Math.Max(0, (work.Height - AppWindow.Size.Height) / 2)));

        // Braces would be read as markup extensions in XAML.
        TemplateHelp.Text = "Used by Open in Forge. {org} is the organization slug, {server} the server's slug, "
                            + "{serverId} its id and {site} the site id. A server's link drops the {site} part.";
        VersionText.Text = "Version " + Version();
        AddPath("Settings", _service.SetupPath);
        AddPath("Log", Log.Path);
        AddPath("Tokens", $"Credential Manager › Windows Credentials › {WindowsCredentialStore.TargetPrefix}<account>");
        AddPath("Start-up entry", @"HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ForgeForWindows");

        LoadSettings();
        RenderAccounts();
        Nav.SelectedItem = AccountsItem;
    }

    public void ShowAndFocus()
    {
        // Nothing set up yet: the only useful thing here is adding a token, so open straight onto it.
        if (_service.Setup.Accounts.Count == 0)
        {
            Nav.SelectedItem = AccountsItem;
            AddExpander.IsExpanded = true;
        }
        AppWindow.Show();
        Activate();
    }

    public void OnServiceChanged() => RenderAccounts();

    // ------------------------------------------------------------ sections

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        var tag = (args.SelectedItem as NavigationViewItem)?.Tag as string ?? "accounts";
        AccountsPage.Visibility = tag == "accounts" ? Visibility.Visible : Visibility.Collapsed;
        GeneralPage.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
        NotificationsPage.Visibility = tag == "notifications" ? Visibility.Visible : Visibility.Collapsed;
        AboutPage.Visibility = tag == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private static string Version()
    {
        var version = typeof(SettingsWindow).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? typeof(SettingsWindow).Assembly.GetName().Version?.ToString() ?? "";
        var plus = version.IndexOf('+');
        return plus > 0 ? version[..plus] : version;
    }

    private void AddPath(string label, string value)
    {
        var row = PathsGrid.RowDefinitions.Count;
        PathsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var name = Caption(label);
        var path = new TextBlock
        {
            Text = value,
            FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Grid.SetRow(name, row);
        Grid.SetRow(path, row);
        Grid.SetColumn(path, 1);
        PathsGrid.Children.Add(name);
        PathsGrid.Children.Add(path);
    }

    // ------------------------------------------------------------ settings

    private void LoadSettings()
    {
        _loading = true;
        try
        {
            var s = _service.Setup.Settings;
            IntervalBox.Value = s.RefreshIntervalSec;
            NotifySwitch.IsOn = s.NotifyDeployments;
            StartupSwitch.IsOn = s.StartWithWindows;
            TemplateBox.Text = s.DashboardUrlTemplate;
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>Every control writes through at once, the way Windows Settings does; there is no Save button.</summary>
    private void ApplySettings()
    {
        if (_loading) return;
        var current = _service.Setup.Settings;
        var interval = double.IsNaN(IntervalBox.Value) ? current.RefreshIntervalSec : (int)IntervalBox.Value;
        var settings = new AppSettings
        {
            RefreshIntervalSec = interval,
            NotifyDeployments = NotifySwitch.IsOn,
            StartWithWindows = StartupSwitch.IsOn,
            DashboardUrlTemplate = TemplateBox.Text,
        }.Normalized();
        _service.UpdateSettings(settings);
        if (settings.StartWithWindows != current.StartWithWindows) StartupRegistration.Sync(settings.StartWithWindows);
        // Normalizing can change what was typed (an interval out of range, a blank template); show what was kept.
        LoadSettings();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e) => ApplySettings();

    private void IntervalBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => ApplySettings();

    private void TemplateBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (TemplateBox.Text != _service.Setup.Settings.DashboardUrlTemplate) ApplySettings();
    }

    private void TemplateBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        ApplySettings();
    }

    // ------------------------------------------------------------ accounts

    private void RenderAccounts()
    {
        AccountsPanel.Children.Clear();
        var setup = _service.Setup;
        PaneSummary.Text = setup.Accounts.Count == 0 ? "Not set up yet"
            : $"{ForgeText.Pluralize(setup.Accounts.Count, "account")} · {ForgeText.Pluralize(setup.Organizations.Count, "organization")}";
        AddTitle.Text = setup.Accounts.Count == 0 ? "Add your Forge account" : "Add a Forge account";
        if (setup.Accounts.Count == 0)
        {
            AddExpander.IsExpanded = true;
            return;
        }
        foreach (var account in setup.Accounts) AccountsPanel.Children.Add(AccountCard(account, setup));
    }

    /// <summary>One account as an expander: who it is and how it is in the header, what it watches and what to do with it inside.</summary>
    private Expander AccountCard(AccountInfo account, SetupState setup)
    {
        var orgs = setup.Organizations.Where(o => o.Account == account.Name).Select(o => o.DisplayName).ToList();
        var hasToken = _service.HasToken(account.Name);
        var title = account.Label == account.Name ? account.Name : $"{account.Label} ({account.Name})";

        var header = new Grid { ColumnSpacing = 16, Padding = new Thickness(0, 10, 0, 10) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(32) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var initial = new Border
        {
            Width = 32, Height = 32, CornerRadius = new CornerRadius(16),
            Background = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"],
            Child = new TextBlock
            {
                Text = (account.Label != "" ? account.Label : account.Name)[..1].ToUpperInvariant(),
                HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = (Brush)Application.Current.Resources["TextOnAccentFillColorPrimaryBrush"],
            },
        };
        header.Children.Add(initial);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title });
        var line = string.Join(" · ", new[]
        {
            account.User,
            orgs.Count == 0 ? "watching nothing" : "watching " + string.Join(", ", orgs),
        }.Where(s => s != ""));
        text.Children.Add(Caption(line));
        Grid.SetColumn(text, 1);
        header.Children.Add(text);

        var state = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        var tone = hasToken ? Tone.Ok : Tone.Bad;
        state.Children.Add(new Microsoft.UI.Xaml.Shapes.Ellipse { Width = 8, Height = 8, Fill = Palette.For(tone), VerticalAlignment = VerticalAlignment.Center });
        state.Children.Add(new TextBlock
        {
            Text = hasToken ? "Token stored" : "Token missing",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = hasToken ? Palette.Secondary : Palette.For(Tone.Bad),
        });
        Grid.SetColumn(state, 2);
        header.Children.Add(state);

        var body = new StackPanel { Spacing = 12, Padding = new Thickness(48, 4, 0, 8) };
        var watched = new StackPanel { Spacing = 4 };
        watched.Children.Add(new TextBlock { Text = "Organizations watched", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        if (orgs.Count == 0) watched.Children.Add(Caption("None. Replace the token to choose some."));
        foreach (var org in orgs) watched.Children.Add(new TextBlock { Text = org });
        body.Children.Add(watched);
        if (!hasToken)
            body.Children.Add(Caption("Credential Manager has no token for this account. Replace it to start watching again.", Palette.For(Tone.Bad)));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var replace = new Button { Content = "Replace token" };
        replace.Click += (_, _) =>
        {
            // The add form doubles as the update form: a token for an existing account updates it.
            AccountNameBox.Text = account.Label;
            AddExpander.IsExpanded = true;
            AddExpander.StartBringIntoView();
            TokenBox.Focus(FocusState.Programmatic);
        };
        var remove = new Button { Content = "Remove account", Foreground = Palette.For(Tone.Bad) };
        remove.Click += (_, _) => ConfirmRemove(account.Name, title);
        buttons.Children.Add(replace);
        buttons.Children.Add(remove);
        body.Children.Add(buttons);

        var expander = new Expander
        {
            Header = header,
            Content = body,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            IsExpanded = !hasToken || _openAccounts.Contains(account.Name),
        };
        expander.Expanding += (_, _) => _openAccounts.Add(account.Name);
        expander.Collapsed += (_, _) => _openAccounts.Remove(account.Name);
        return expander;
    }

    /// <summary>Removing deletes the stored token, which can't be read back, so it asks first.</summary>
    private async void ConfirmRemove(string name, string title)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Remove {title}?",
            Content = "Its token is deleted from Credential Manager and its organizations stop being watched. Nothing changes in Forge itself.",
            PrimaryButtonText = "Remove",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = ((FrameworkElement)Content).ActualTheme,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary) _service.RemoveAccount(name);
    }

    private static TextBlock Caption(string text, Brush? brush = null) => new()
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

    private void TokenLink_Click(object sender, RoutedEventArgs e) => Shell.OpenUrl("https://forge.laravel.com/profile/api");

    private void OpenFolder_Click(object sender, RoutedEventArgs e) =>
        Shell.RevealFolder(Path.GetDirectoryName(_service.SetupPath)!);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);
}
