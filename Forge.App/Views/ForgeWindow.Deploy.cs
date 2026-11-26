using Forge.App.Services;
using Forge.Core.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace Forge.App.Views;

/// <summary>
/// The site's Deploy tab: quick deploy, the deploy hook, deployment webhooks and the deploy key.
/// Quick deploy rides the polled sites list; the webhooks and the key are read when the tab
/// opens (two requests); the hook URL only when it is copied. The hook URL and webhook URLs are
/// secrets, so neither is ever drawn whole, flashed or logged.
/// </summary>
public sealed partial class ForgeWindow
{
    private List<Webhook>? _webhooks;
    /// <summary>The public key, "" when the site has none, null until read (or when the read failed).</summary>
    private string? _deployKey;
    private bool _copyingHook;

    private void ResetDeploy()
    {
        _webhooks = null;
        _deployKey = null;
        WebhookRows.Children.Clear();
        WebhooksText.Text = "";
        WebhookBox.Text = "";
        DeployKeyState.Text = "";
        DeployKeyText.Text = "";
    }

    private void RenderDeploy(Site site)
    {
        var quick = Actions.QuickDeploy(_org, site);
        _syncing = true;
        QuickDeploySwitch.IsOn = site.QuickDeploy;
        _syncing = false;
        QuickDeploySwitch.IsEnabled = quick.Available && !_sending;
        QuickDeployText.Text = !quick.Available ? Capitalize(quick.Reason)
            : site.QuickDeploy
                ? $"On: every push to {(site.Branch == "" ? "the branch" : site.Branch)} deploys the site"
                : "Off: deploys only start by hand or through the deploy hook";

        DeployHookCopyButton.IsEnabled = !_copyingHook;
        DeployHookRegenerateButton.IsEnabled = !_sending;
        WebhookAddButton.IsEnabled = !_sending && Actions.WebhookUrlProblem(WebhookBox.Text) == "";
        foreach (var button in WebhookRows.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()))
            if ((string)button.Tag == "delete") button.IsEnabled = !_sending;

        var known = _deployKey is not null;
        var has = _deployKey is { Length: > 0 };
        DeployKeyText.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        DeployKeyCopyButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        DeployKeyDeleteButton.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        DeployKeyCreateButton.Visibility = known && !has ? Visibility.Visible : Visibility.Collapsed;
        DeployKeyCreateButton.IsEnabled = DeployKeyDeleteButton.IsEnabled = !_sending;
    }

    /// <summary>The webhooks, then the key. A newer load or another site makes either answer stale.</summary>
    private async void LoadDeploy(int gen)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        var key = site.Key;
        bool Stale() => gen != _loadGen || _org != org || _siteKey != key || _closed;

        WebhooksText.Text = "Loading…";
        DeployKeyState.Text = "Loading…";
        var hooks = await _service.WebhooksAsync(org, site);
        if (Stale()) return;
        if (hooks.Ok)
        {
            _webhooks = hooks.Value!;
            WebhooksText.Text = _webhooks.Count == 0
                ? "Forge calls these URLs after every deploy. None yet."
                : "Forge calls these URLs after every deploy. Only the host is shown: the rest may hold a token.";
        }
        else
        {
            _webhooks = null;
            WebhooksText.Text = hooks.Error ?? "Could not read the webhooks";
        }
        DrawWebhooks();

        var deployKey = await _service.DeployKeyAsync(org, site);
        if (Stale()) return;
        _deployKey = deployKey.Ok ? deployKey.Value : null;
        DeployKeyText.Text = _deployKey ?? "";
        DeployKeyState.Text = !deployKey.Ok ? deployKey.Error ?? "Could not read the deploy key"
            : _deployKey == "" ? "No deploy key. Forge pulls the repository through its source control connection."
            : "Forge pulls the repository with this key. It is public: add it to the repository as a read-only deploy key.";
        if (CurrentSite() is { } now) RenderDeploy(now);
    }

    private void DrawWebhooks()
    {
        WebhookRows.Children.Clear();
        foreach (var hook in _webhooks ?? [])
        {
            var row = new Grid { ColumnSpacing = 8 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var text = new TextBlock
            {
                Text = ForgeText.MaskedUrl(hook.Url),
                FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            if (hook.CreatedAt != "") ToolTipService.SetToolTip(text, "Added " + ForgeText.RelativeTime(hook.CreatedAt, NowMs));
            row.Children.Add(text);

            var copy = new Button { Content = "Copy", Tag = "copy" };
            copy.Click += (_, _) => CopySecret(hook.Url, "Webhook URL copied. It stays out of clipboard history.");
            Grid.SetColumn(copy, 1);
            row.Children.Add(copy);

            var delete = new Button { Content = "Delete…", Tag = "delete", IsEnabled = !_sending };
            delete.Click += (_, _) => DeleteWebhook(hook.Id);
            Grid.SetColumn(delete, 2);
            row.Children.Add(delete);
            WebhookRows.Children.Add(row);
        }
    }

    /// <summary>Copies text that may be seen, such as a public key: it stays in clipboard history.</summary>
    private void CopyPublic(string text, string done)
    {
        try
        {
            Shell.CopyText(text);
            Flash(done);
        }
        catch (Exception e)
        {
            Flash("Could not copy: " + e.Message, bad: true);
        }
    }

    private void CopySecret(string text, string done)
    {
        try
        {
            Shell.CopySecret(text);
            Flash(done);
        }
        catch (Exception e)
        {
            Flash("Could not copy: " + e.Message, bad: true);
        }
    }

    /// <summary>Re-reads the tab after a write, if the reader is still on it.</summary>
    private Action ReloadDeployAfter(string org, string key) => () =>
    {
        if (_org == org && _siteKey == key && _siteTab == "deploy") LoadDeploy(++_loadGen);
    };

    /// <summary>A deploy write: asks with the site's name, re-making the spec from the latest site before sending.</summary>
    private void DeployWrite(Func<Site?, ActionSpec> make, Action? done = null)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        var key = site.Key;
        SendAction(org, site.ServerId, make(site), site.Name,
            () => make(_service.Snapshot.Org(org)?.SiteByKey(key)), done);
    }

    private void QuickDeploySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        // The switch only asks; it keeps showing the site's real state until Forge reports the change.
        _syncing = true;
        QuickDeploySwitch.IsOn = !QuickDeploySwitch.IsOn;
        _syncing = false;
        DeployWrite(site => Actions.QuickDeploy(_org, site));
    }

    private async void DeployHookCopy_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSite() is not { } site || _copyingHook) return;
        _copyingHook = true;
        DeployHookCopyButton.IsEnabled = false;
        try
        {
            var result = await _service.DeployHookUrlAsync(_org, site);
            if (_closed) return;
            if (!result.Ok) Flash(result.Error ?? "Could not read the deploy hook", bad: true);
            else CopySecret(result.Value!, "Deploy hook URL copied. It stays out of clipboard history.");
        }
        finally
        {
            _copyingHook = false;
            if (!_closed) DeployHookCopyButton.IsEnabled = true;
        }
    }

    private void DeployHookRegenerate_Click(object sender, RoutedEventArgs e) =>
        DeployWrite(site => Actions.RegenerateDeployHook(_org, site));

    private void DeleteWebhook(string id)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        DeployWrite(s => Actions.DeleteWebhook(org, s, _webhooks?.FirstOrDefault(w => w.Id == id)),
            ReloadDeployAfter(org, site.Key));
    }

    private void WebhookAdd_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        var text = WebhookBox.Text.Trim();
        var problem = Actions.WebhookUrlProblem(text);
        if (problem != "") { Flash(problem); return; }
        var reload = ReloadDeployAfter(org, site.Key);
        // The text is fixed when asked about: what is sent is what the dialog was about.
        DeployWrite(s => Actions.AddWebhook(org, s, text), () =>
        {
            if (WebhookBox.Text.Trim() == text) WebhookBox.Text = "";
            reload();
        });
    }

    private void WebhookBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        WebhookAdd_Click(sender, e);
    }

    private void WebhookBox_TextChanged(object sender, TextChangedEventArgs e) =>
        WebhookAddButton.IsEnabled = !_sending && Actions.WebhookUrlProblem(WebhookBox.Text) == "";

    private void DeployKeyCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_deployKey is { Length: > 0 } key) CopyPublic(key, "Public deploy key copied");
    }

    private void DeployKeyCreate_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        DeployWrite(s => Actions.CreateDeployKey(org, s), ReloadDeployAfter(org, site.Key));
    }

    private void DeployKeyDelete_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        DeployWrite(s => Actions.DeleteDeployKey(org, s), ReloadDeployAfter(org, site.Key));
    }
}
