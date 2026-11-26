using Forge.App.Services;
using Forge.Core.Model;
using Forge.Core.Service;
using Forge.Core.Setup;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;

namespace Forge.App.Views;

/// <summary>
/// Access and safety: a server's SSH keys and firewall rules, and a site's certificates. Each is
/// read when its tab opens, never on the poll: SSH keys cost two requests (the keys, and the
/// server's own key once per visit), the firewall one, and certificates one per page of domains
/// plus one per domain, a batch at a time. A refused or held-back certificate check stops the batch.
/// </summary>
public sealed partial class ForgeWindow
{
    private List<SshKey>? _sshKeys;
    private List<FirewallRule>? _firewallRules;
    private IReadOnlyList<LocalSshKey> _localKeys = [];
    /// <summary>The server's public key, null until read or when the read failed.</summary>
    private string? _serverKey;
    /// <summary>The server key was asked for this visit (even if the answer was an error): it isn't asked again.</summary>
    private bool _serverKeyRead;
    private bool _lookingUpIp;
    /// <summary>The server and user a key was just added for, so the tab offers to ssh in straight away.</summary>
    private (string Server, string User) _keyAdded = ("", "");
    /// <summary>How many re-reads a list in progress (a key installing, a rule applying) still gets.</summary>
    private int _accessRechecks;

    private const string PasteChoice = "paste";
    private const string BrowseChoice = "browse";
    /// <summary>Keys chosen with "Choose a file…" this session, offered again beside the .ssh folder's.</summary>
    private readonly List<LocalSshKey> _pickedKeys = [];
    private bool _browsing;

    /// <summary>True while the reader is still on this server's page: an answer for it may be shown.</summary>
    private bool OnServerPage(string org, string serverId) =>
        !_closed && _org == org && _serverId == serverId && _siteKey == "";

    /// <summary>True while the reader is still on this site's page: an answer for it may be shown.</summary>
    private bool OnSitePage(string org, string siteKey) => !_closed && _org == org && _siteKey == siteKey;

    private void RenderCurrentAccess()
    {
        if (CurrentServer() is { } server) RenderAccess(server);
    }

    private void ResetAccess()
    {
        _sshKeys = null;
        _firewallRules = null;
        _serverKey = null;
        _serverKeyRead = false;
        _keyAdded = ("", "");
        _accessRechecks = 0;
        SshKeyRows.Children.Clear();
        FirewallRows.Children.Clear();
        SshKeysText.Text = FirewallText.Text = AddKeyText.Text = AddRuleText.Text = "";
        ServerKeyState.Text = ServerKeyText.Text = "";
        KeyNameBox.Text = Environment.MachineName;
        KeyUserBox.Text = "forge";
        PasteKeyBox.Text = "";
        RuleNameBox.Text = RulePortBox.Text = RuleIpBox.Text = "";
        _syncing = true;
        RuleTypeBox.SelectedIndex = 0;
        _syncing = false;
    }

    private void RenderAccess(Server server)
    {
        // Both panels are collapsed on any other tab; switching to one renders it again.
        if (_serverTab is not ("ssh-keys" or "firewall")) return;
        var addKey = AddKeySpec(server);
        AddKeyButton.IsEnabled = !_sending && addKey.Available;
        var added = _keyAdded.Server == server.Id;
        AddKeyText.Text = added ? AddedKeyNote(server, _keyAdded.User)
            : addKey.Available ? $"Signs in to {server.Name} as {KeyUserBox.Text.Trim()}. Add asks first."
            : KeyNameBox.Text.Trim() == "" && SelectedKeyText() == "" ? "" : Capitalize(addKey.Reason);
        AddedKeyTerminalButton.Visibility = added && _keyAdded.User == "forge" && server.Ip != ""
            ? Visibility.Visible : Visibility.Collapsed;

        var hasKey = _serverKey is { Length: > 0 };
        ServerKeyText.Visibility = ServerKeyCopyButton.Visibility = hasKey ? Visibility.Visible : Visibility.Collapsed;

        var rule = AddRuleSpec(server);
        AddRuleButton.IsEnabled = !_sending && rule.Available;
        AddRuleText.Text = rule.Available ? rule.Wording!.Value.Title
            : RuleNameBox.Text.Trim() == "" && RulePortBox.Text.Trim() == "" ? "Leave From empty to match any address."
            : Capitalize(rule.Reason);
        LookUpIpButton.IsEnabled = !_lookingUpIp;

        foreach (var panel in new[] { SshKeyRows, FirewallRows })
            foreach (var button in panel.Children.OfType<Grid>().SelectMany(g => g.Children.OfType<Button>()))
                button.IsEnabled = !_sending;
    }

    /// <summary>What to do with the key just added for <paramref name="user"/>: the user it was sent with, not what the box says now.</summary>
    private static string AddedKeyNote(Server server, string user) =>
        user == "forge"
            ? $"Added. Once Forge shows it installed, ssh in as forge@{server.Ip}."
            : $"Added. The app's ssh command signs in as forge, so run ssh {user}@{server.Ip} yourself.";

    // ------------------------------------------------------------- SSH keys

    /// <summary>The keys, then the server's own key. Local .pub files are read alongside: no request.</summary>
    private async void LoadSshKeys(int gen)
    {
        if (CurrentServer() is not { } server) return;
        var org = _org;
        var id = server.Id;
        bool Stale() => gen != _loadGen || !OnServerPage(org, id);

        SshKeysText.Text = "Loading…";
        if (ServerKeyState.Text == "") ServerKeyState.Text = "Loading…";
        LoadLocalKeys();
        var keys = await _service.SshKeysAsync(org, id);
        if (Stale()) return;
        _sshKeys = keys.Ok ? keys.Value! : null;
        SshKeysText.Text = !keys.Ok ? keys.Error ?? "Could not read the SSH keys"
            : _sshKeys!.Count == 0 ? "No keys added through Forge. Keys put on the server by hand aren't listed."
            : "Keys added through Forge. Keys put on the server by hand aren't listed.";
        DrawSshKeys(server);

        // The server key never changes on its own: ask once per visit to the server, and don't ask
        // again after a refusal or an error either. Only a read held back for the rate limit is retried.
        if (!_serverKeyRead)
        {
            var key = await _service.ServerKeyAsync(org, id);
            if (Stale()) return;
            _serverKeyRead = !key.Throttled;
            _serverKey = key.Ok ? key.Value : null;
            ServerKeyText.Text = _serverKey ?? "";
            ServerKeyState.Text = !key.Ok ? key.Error ?? "Could not read the server key"
                : _serverKey == "" ? "Forge reports no key for this server."
                : "The key this server signs in elsewhere with. It is public: add it where the server needs access.";
        }
        RenderCurrentAccess();
        RecheckWhileBusy(gen, "ssh-keys", AnyBusy(_sshKeys?.Select(k => k.Status)));
    }

    private static bool AnyBusy(IEnumerable<string>? statuses) =>
        statuses?.Any(s => Status.AccessStatus(s).Tone == Tone.Busy) == true;

    private async void LoadLocalKeys()
    {
        var found = await Task.Run(() => LocalSshKeys.Find());
        if (_closed) return;
        LocalKeysText.Text = found.Count == 0
            ? $"There are no public keys in {LocalSshKeys.DefaultDirectory}. Choose a .pub file elsewhere, or paste a key. Only .pub files are read: a private key never leaves this machine."
            : "Pick a public key from your .ssh folder, choose a .pub file elsewhere, or paste one. Only .pub files are read: a private key never leaves this machine.";
        FillKeyChoices(found, SelectedKeyTag);
    }

    /// <summary>The .ssh folder's keys, then any chosen this session, then "Choose a file…" and "Paste a key…".</summary>
    private void FillKeyChoices(IReadOnlyList<LocalSshKey> found, string? select)
    {
        _localKeys = found.Concat(_pickedKeys.Where(p => found.All(f => f.Key != p.Key))).ToList();
        _syncing = true;
        LocalKeyBox.Items.Clear();
        foreach (var key in _localKeys)
            LocalKeyBox.Items.Add(new ComboBoxItem
            {
                Content = key.FileName + "  ·  " + Actions.SshKeyShort(key.Key) + (key.Comment != "" ? "  ·  " + key.Comment : ""),
                Tag = "key:" + key.Key,
            });
        LocalKeyBox.Items.Add(new ComboBoxItem { Content = "Choose a file…", Tag = BrowseChoice });
        LocalKeyBox.Items.Add(new ComboBoxItem { Content = "Paste a key…", Tag = PasteChoice });
        var again = select == BrowseChoice ? null
            : LocalKeyBox.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == select);
        // With nothing found, start on pasting rather than on a picker that would open by itself.
        LocalKeyBox.SelectedItem = again ?? (_localKeys.Count > 0 ? LocalKeyBox.Items[0] : LocalKeyBox.Items[^1]);
        _syncing = false;
        ShowKeyChoice();
    }

    /// <summary>The paste box only when "Paste a key…" is chosen, then the form's button and hint.</summary>
    private void ShowKeyChoice()
    {
        PasteKeyBox.Visibility = SelectedKeyIsPaste() ? Visibility.Visible : Visibility.Collapsed;
        RenderCurrentAccess();
    }

    /// <summary>
    /// A .pub file anywhere, for a key kept outside the .ssh folder. The picker offers only .pub files,
    /// and <see cref="LocalSshKeys.Read"/> refuses anything else, so a private key is never opened.
    /// </summary>
    /// <param name="back">What the list showed before "Choose a file…", so a cancelled pick goes back to it.</param>
    private async void BrowseForKey(string? back)
    {
        if (_browsing) return;
        _browsing = true;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileOpenPicker(AppWindow.Id)
            {
                SuggestedStartLocation = Microsoft.Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary,
                CommitButtonText = "Use this key",
            };
            picker.FileTypeFilter.Add(".pub");
            var file = await picker.PickSingleFileAsync();
            if (_closed) return;
            if (file is null) { RefillKeyChoices(back); return; }
            var (key, problem) = await Task.Run(() => LocalSshKeys.Read(file.Path));
            if (_closed) return;
            if (key is null)
            {
                Flash(problem, bad: true);
                RefillKeyChoices(back);
                return;
            }
            if (_pickedKeys.All(k => k.Key != key.Key)) _pickedKeys.Add(key);
            RefillKeyChoices("key:" + key.Key);
        }
        catch (Exception e)
        {
            Flash("Could not open the file picker: " + e.Message, bad: true);
            RefillKeyChoices(back);
        }
        finally
        {
            _browsing = false;
        }
    }

    /// <summary>The list again from the .ssh folder's keys already found, with the picked ones after them.</summary>
    private void RefillKeyChoices(string? select) => FillKeyChoices(_localKeys.Except(_pickedKeys).ToList(), select);

    private string? SelectedKeyTag => (LocalKeyBox.SelectedItem as ComboBoxItem)?.Tag as string;

    private bool SelectedKeyIsPaste() => SelectedKeyTag == PasteChoice;

    private string SelectedKeyText()
    {
        if (SelectedKeyIsPaste()) return PasteKeyBox.Text.Trim();
        var tag = SelectedKeyTag ?? "";
        return tag.StartsWith("key:", StringComparison.Ordinal) ? tag["key:".Length..] : "";
    }

    private ActionSpec AddKeySpec(Server? server) =>
        Actions.AddSshKey(_org, server, KeyNameBox.Text, SelectedKeyText(), KeyUserBox.Text);

    private void DrawSshKeys(Server server)
    {
        SshKeyRows.Children.Clear();
        foreach (var key in _sshKeys ?? [])
        {
            var (state, tone) = Status.AccessStatus(key.Status);
            var detail = string.Join(" · ", new[]
            {
                key.User == "" ? "" : "as " + key.User, state,
                key.CreatedBy == "" ? "" : "by " + key.CreatedBy,
                key.CreatedAt == "" ? "" : "added " + ForgeText.RelativeTime(key.CreatedAt, NowMs),
            }.Where(s => s != ""));
            SshKeyRows.Children.Add(AccessRow(key.Name == "" ? "SSH key" : key.Name, detail, tone,
                "Remove…", () => DeleteSshKey(server.Id, key.Id)));
        }
    }

    private void DeleteSshKey(string serverId, string keyId)
    {
        if (CurrentServer()?.Id != serverId) return;
        var org = _org;
        ServerWrite(s => Actions.DeleteSshKey(org, s, _sshKeys?.FirstOrDefault(k => k.Id == keyId)), ReloadServerTabAfter(org, serverId, "ssh-keys"));
    }

    private void AddKey_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentServer() is not { } server) return;
        var org = _org;
        // What the dialog asks about is what is sent: the inputs are fixed here.
        var name = KeyNameBox.Text;
        var key = SelectedKeyText();
        var user = KeyUserBox.Text;
        var reload = ReloadServerTabAfter(org, server.Id, "ssh-keys");
        ServerWrite(s => Actions.AddSshKey(org, s, name, key, user), () =>
        {
            _keyAdded = (server.Id, user.Trim());
            if (SelectedKeyIsPaste() && PasteKeyBox.Text.Trim() == key.Trim()) PasteKeyBox.Text = "";
            reload();
        });
    }

    private void ServerKeyCopy_Click(object sender, RoutedEventArgs e)
    {
        if (_serverKey is { Length: > 0 } key) CopyPublic(key, "Server public key copied");
    }

    private void LocalKeyBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        _keyAdded = ("", "");
        if (SelectedKeyTag == BrowseChoice)
        {
            BrowseForKey((e.RemovedItems.FirstOrDefault() as ComboBoxItem)?.Tag as string);
            return;
        }
        ShowKeyChoice();
    }

    /// <summary>Any box of the add-key or add-rule form: the button and the hint follow what is typed.</summary>
    private void AccessInput_Changed(object sender, TextChangedEventArgs e)
    {
        // Editing the key form starts the next key, so the "Added" note gives way to the form's own hint.
        // The paste box emptied after a successful add (TextChanged arrives later) doesn't count.
        if (ReferenceEquals(sender, KeyNameBox) || ReferenceEquals(sender, KeyUserBox)
            || ReferenceEquals(sender, PasteKeyBox) && PasteKeyBox.Text.Trim() != "")
            _keyAdded = ("", "");
        RenderCurrentAccess();
    }

    // ------------------------------------------------------- firewall rules

    private async void LoadFirewall(int gen)
    {
        if (CurrentServer() is not { } server) return;
        var org = _org;
        var id = server.Id;
        FirewallText.Text = "Loading…";
        var rules = await _service.FirewallRulesAsync(org, id);
        if (gen != _loadGen || !OnServerPage(org, id)) return;
        _firewallRules = rules.Ok ? rules.Value! : null;
        FirewallText.Text = !rules.Ok ? rules.Error ?? "Could not read the firewall rules"
            : _firewallRules!.Count == 0 ? "No rules added through Forge."
            : "Rules added through Forge. A deny rule wins over an allow rule for the same port.";
        DrawFirewall(server);
        RenderCurrentAccess();
        RecheckWhileBusy(gen, "firewall", AnyBusy(_firewallRules?.Select(r => r.Status)));
    }

    private void DrawFirewall(Server server)
    {
        FirewallRows.Children.Clear();
        foreach (var rule in _firewallRules ?? [])
        {
            var (state, tone) = Status.AccessStatus(rule.Status);
            var verb = rule.Type == "deny" ? "Deny" : "Allow";
            var ports = Actions.PortText(rule.Port);
            var detail = string.Join(" · ", new[]
            {
                rule.Name, state, Actions.FirewallCoversSsh(rule, server) ? "ssh" : "",
            }.Where(s => s != ""));
            FirewallRows.Children.Add(AccessRow($"{verb} {ports} from {Actions.FirewallAddressText(rule.IpAddress)}", detail,
                rule.Type == "deny" && tone == Tone.Ok ? Tone.Warn : tone, "Delete…", () => DeleteFirewallRule(server.Id, rule.Id)));
        }
    }

    private bool RuleAllows() => (RuleTypeBox.SelectedItem as ComboBoxItem)?.Tag as string != "deny";

    private ActionSpec AddRuleSpec(Server? server) =>
        Actions.AddFirewallRule(_org, server, RuleNameBox.Text, RulePortBox.Text, RuleIpBox.Text, RuleAllows());

    private void DeleteFirewallRule(string serverId, string ruleId)
    {
        if (CurrentServer()?.Id != serverId) return;
        var org = _org;
        ServerWrite(s => Actions.DeleteFirewallRule(org, s, _firewallRules?.FirstOrDefault(r => r.Id == ruleId)),
            ReloadServerTabAfter(org, serverId, "firewall"));
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentServer() is not { } server) return;
        var org = _org;
        var (name, port, ip, allow) = (RuleNameBox.Text, RulePortBox.Text, RuleIpBox.Text, RuleAllows());
        var reload = ReloadServerTabAfter(org, server.Id, "firewall");
        ServerWrite(s => Actions.AddFirewallRule(org, s, name, port, ip, allow), () =>
        {
            if (RulePortBox.Text == port && RuleIpBox.Text == ip) RuleNameBox.Text = RulePortBox.Text = RuleIpBox.Text = "";
            reload();
        });
    }

    /// <summary>Asks api.ipify.org, only now that the button was pressed. The answer fills the box; nothing is sent to Forge.</summary>
    private async void LookUpIp_Click(object sender, RoutedEventArgs e)
    {
        if (_lookingUpIp) return;
        _lookingUpIp = true;
        LookUpIpButton.IsEnabled = false;
        try
        {
            var (ip, error) = await PublicIp.LookUpAsync();
            if (_closed) return;
            if (ip is null) { Flash(error ?? "Could not look up the address", bad: true); return; }
            RuleIpBox.Text = ip;
            Flash($"{PublicIp.Service} says this machine reaches the internet from {ip}. Check it before adding the rule.");
        }
        finally
        {
            _lookingUpIp = false;
            if (!_closed) LookUpIpButton.IsEnabled = true;
        }
    }

    private void RuleType_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        RenderCurrentAccess();
    }

    // --------------------------------------------------------- certificates

    private List<DomainCertificate> _certificates = [];
    private List<SiteDomain> _domainsLeft = [];
    /// <summary>Where the next page of domains starts, or "" when Forge has listed them all.</summary>
    private string _domainsCursor = "";

    private void ResetCertificates()
    {
        _certificates = [];
        _domainsLeft = [];
        _domainsCursor = "";
        CertificateRows.Children.Clear();
        CertificatesText.Text = "";
        CertificatesMoreButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>The domains, then each one's active certificate, a batch at a time: every domain costs a request.</summary>
    private async void LoadCertificates(int gen)
    {
        if (CurrentSite() is not { } site) return;
        var org = _org;
        var key = site.Key;
        ResetCertificates();
        CertificatesText.Text = "Loading the domains…";
        var domains = await _service.DomainsAsync(org, site);
        if (gen != _loadGen || !OnSitePage(org, key)) return;
        if (!domains.Ok) { CertificatesText.Text = domains.Error ?? "Could not read the domains"; return; }
        if (domains.Value!.Count == 0) { CertificatesText.Text = "Forge lists no domains for this site."; return; }
        _domainsLeft = domains.Value!;
        _domainsCursor = domains.NextCursor;
        await CheckCertificatesAsync(gen, org, site);
    }

    private async Task CheckCertificatesAsync(int gen, string org, Site site)
    {
        var batch = _domainsLeft.Take(ForgeService.CertificateBatch).ToList();
        _domainsLeft = _domainsLeft.Skip(batch.Count).ToList();
        // Hidden until the batch is done, so "Check more" can't start a second one alongside.
        CertificatesMoreButton.Visibility = Visibility.Collapsed;
        for (var i = 0; i < batch.Count; i++)
        {
            CertificatesText.Text = $"Checking {batch[i].Name} ({i + 1} of {batch.Count})…";
            var cert = await _service.ActiveCertificateAsync(org, site, batch[i]);
            if (gen != _loadGen || !OnSitePage(org, site.Key)) return;
            if (cert.Fatal || cert.Throttled)
            {
                // The rest would get the same answer: say it once, and keep the unchecked domains for "Check more".
                _domainsLeft = batch.Skip(i).Concat(_domainsLeft).ToList();
                CertificatesText.Text = cert.Error ?? "Could not read the certificates";
                ShowCertificatesMore();
                return;
            }
            _certificates.Add(new DomainCertificate(batch[i], cert.Ok ? cert.Value : null, cert.Ok ? "" : cert.Error ?? "could not read"));
            DrawCertificates();
        }
        var bad = _certificates.Count(c => Status.CertificateStatus(c, NowMs).Tone == Tone.Bad);
        CertificatesText.Text = bad > 0
            ? $"{ForgeText.Pluralize(bad, "certificate")} need{(bad == 1 ? "s" : "")} attention. Let's Encrypt renews by itself; a failed request is what to fix."
            : "Let's Encrypt renews by itself, so a failed or stuck request is what to look for. Fix one in Forge.";
        ShowCertificatesMore();
    }

    /// <summary>"Check more" while domains are left to check, or another page of them is still to be read.</summary>
    private void ShowCertificatesMore()
    {
        CertificatesMoreButton.Content = _domainsLeft.Count > 0
            ? $"Check {ForgeText.Pluralize(_domainsLeft.Count, "more domain")}"
            : "Check more domains";
        CertificatesMoreButton.Visibility = MoreDomains ? Visibility.Visible : Visibility.Collapsed;
    }

    private bool MoreDomains => _domainsLeft.Count > 0 || _domainsCursor != "";

    private void DrawCertificates()
    {
        var now = NowMs;
        CertificateRows.Children.Clear();
        var rows = _certificates.Select(c => (Row: c, Status: Status.CertificateStatus(c, now)))
            .OrderBy(r => Status.ToneRank(r.Status.Tone)).ThenBy(r => r.Row.Domain.Type == "primary" ? 0 : 1);
        foreach (var (row, (state, tone)) in rows)
        {
            var detail = string.Join(" · ", new[]
            {
                Capitalize(state), Status.CertificateDetail(row.Certificate),
                row.Domain.Type == "primary" ? "primary domain" : "",
            }.Where(s => s != ""));
            CertificateRows.Children.Add(AccessRow(row.Domain.Name, detail, tone, null, null));
        }
    }

    private async void CertificatesMore_Click(object sender, RoutedEventArgs e)
    {
        if (CurrentSite() is not { } site || !MoreDomains) return;
        var gen = _loadGen;
        var org = _org;
        if (_domainsLeft.Count == 0)
        {
            // The first page is done: read the next one, then check it like the first.
            CertificatesMoreButton.Visibility = Visibility.Collapsed;
            CertificatesText.Text = "Loading more domains…";
            var page = await _service.DomainsAsync(org, site, _domainsCursor);
            if (gen != _loadGen || !OnSitePage(org, site.Key)) return;
            if (!page.Ok)
            {
                CertificatesText.Text = page.Error ?? "Could not read the domains";
                ShowCertificatesMore();
                return;
            }
            var checkedIds = _certificates.Select(c => c.Domain.Id).ToHashSet(StringComparer.Ordinal);
            _domainsLeft = page.Value!.Where(d => !checkedIds.Contains(d.Id)).ToList();
            _domainsCursor = page.NextCursor;
            if (_domainsLeft.Count == 0)
            {
                CertificatesText.Text = "No more domains to check.";
                ShowCertificatesMore();
                return;
            }
        }
        await CheckCertificatesAsync(gen, org, site);
    }

    // --------------------------------------------------------------- shared

    /// <summary>One row of a list: a status dot, a title and a detail line, and a button when the row can be acted on.</summary>
    private Grid AccessRow(string title, string detail, Tone tone, string? button, Action? click)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var dot = new Ellipse { Width = 8, Height = 8, StrokeThickness = 2, VerticalAlignment = VerticalAlignment.Center };
        SetDot(dot, tone);
        row.Children.Add(dot);
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis });
        if (detail != "")
            text.Children.Add(new TextBlock
            {
                Text = detail, TextTrimming = TextTrimming.CharacterEllipsis,
                Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
                Foreground = Palette.Secondary,
            });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);
        if (button is not null && click is not null)
        {
            var b = new Button { Content = button, IsEnabled = !_sending, VerticalAlignment = VerticalAlignment.Center };
            b.Click += (_, _) => click();
            Grid.SetColumn(b, 2);
            row.Children.Add(b);
        }
        return row;
    }

    /// <summary>A server write: asks with the spec's own wording, re-making it from the latest server before sending.</summary>
    private void ServerWrite(Func<Server?, ActionSpec> make, Action? done = null)
    {
        if (CurrentServer() is not { } server) return;
        var org = _org;
        var id = server.Id;
        SendAction(org, id, make(server), server.Name, () => make(_service.Snapshot.Org(org)?.ServerById(id)), done);
    }

    /// <summary>Re-reads the tab after a write, if the reader is still on it; a key or rule still applying gets a few more looks.</summary>
    private Action ReloadServerTabAfter(string org, string serverId, string tab) => () =>
    {
        if (!OnServerPage(org, serverId) || _serverTab != tab) return;
        _accessRechecks = 3;
        LoadServerTab();
    };

    /// <summary>While a key installs or a rule applies, look again every 8 s, a few times at most, and only while the tab is open.</summary>
    private async void RecheckWhileBusy(int gen, string tab, bool busy)
    {
        if (!busy || _accessRechecks <= 0) return;
        _accessRechecks--;
        var org = _org;
        var id = _serverId;
        await Task.Delay(TimeSpan.FromSeconds(8));
        if (gen != _loadGen || !OnServerPage(org, id) || _serverTab != tab) return;
        LoadServerTab();
    }
}
