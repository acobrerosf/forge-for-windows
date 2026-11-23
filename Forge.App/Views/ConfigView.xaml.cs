using Forge.App.Services;
using Forge.Core.Model;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;
using Windows.UI.Core;

namespace Forge.App.Views;

/// <summary>
/// One of a site's config files. It opens read-only, with an environment file's values masked
/// until Reveal is pressed. Edit is an explicit mode, and Save hands the edit to the owner, which
/// shows the diff and asks. What it shows lives only in these fields: it is never logged or
/// written to disk, and <see cref="Reset"/> lets go of it.
/// </summary>
public sealed partial class ConfigView : UserControl
{
    private ConfigFile? _file;
    private ConfigDocument? _doc;
    private bool _editing;
    private bool _busy;
    private string _header = "";
    private bool _headerBad;

    public ConfigView()
    {
        InitializeComponent();
        Render();
    }

    /// <summary>Raised by Reload; the owner reads the file again and calls <see cref="Show"/>.</summary>
    public event Action? ReloadRequested;
    /// <summary>Raised by Save or Ctrl+S while editing; the owner diffs, asks and sends.</summary>
    public event Action? SaveRequested;
    /// <summary>What a Copy did, for the owner to report.</summary>
    public event Action<string, bool>? Reported;

    public ConfigFile? File => _file;
    /// <summary>The file as Forge held it when it was read: what a save compares against.</summary>
    public ConfigDocument? Document => _doc;
    public bool IsEditing => _editing;

    /// <summary>The edit with the file's own line endings, whatever the text box did to them.</summary>
    public string EditedText => ConfigText.FromEditor(Editor.Text, _doc?.Content);

    public bool IsDirty => _editing && _doc is not null && EditedText != _doc.Content;

    /// <summary>"the environment file", for sentences about the open file.</summary>
    public string What => _file is { } f ? ConfigFiles.Lower(f) : "file";

    /// <summary>While a save is in flight: the buttons that would start another one are off.</summary>
    public bool Busy
    {
        get => _busy;
        set { _busy = value; Render(); }
    }

    private bool IsEnvironment => _file == ConfigFile.Environment;

    public void ShowLoading(ConfigFile file)
    {
        if (_file != file) RevealButton.IsChecked = false;
        _file = file;
        _doc = null;
        _editing = false;
        Editor.Text = "";
        SetHeader("Reading it from Forge…");
        LoadingRing.IsActive = true;
        Render();
    }

    /// <summary>Shows what Forge holds and leaves edit mode.</summary>
    public void Show(ConfigFile file, ConfigDocument doc, string? note = null)
    {
        if (_file != file) RevealButton.IsChecked = false;
        _file = file;
        _doc = doc;
        _editing = false;
        Editor.Text = "";
        LoadingRing.IsActive = false;
        SetHeader(note ?? ReadNote());
        Render();
    }

    public void ShowError(ConfigFile file, string error)
    {
        _file = file;
        _doc = null;
        _editing = false;
        Editor.Text = "";
        LoadingRing.IsActive = false;
        SetHeader(error, bad: true);
        Render();
    }

    /// <summary>A line under the toolbar, a conflict say, without leaving edit mode.</summary>
    public void Note(string text, bool bad = false)
    {
        SetHeader(text, bad);
        Render();
    }

    /// <summary>Drops the file and any edit: nothing it showed is kept.</summary>
    public void Reset()
    {
        _file = null;
        _doc = null;
        _editing = false;
        _busy = false;
        Editor.Text = "";
        ReadText.Text = "";
        RevealButton.IsChecked = false;
        LoadingRing.IsActive = false;
        SetHeader("");
        Render();
    }

    public void BeginEdit()
    {
        if (_doc is null || _editing) return;
        _editing = true;
        Editor.Text = _doc.Content;
        SetHeader(IsEnvironment
            ? "Editing. Values are shown while you edit. Save shows what changes and asks before anything is sent."
            : "Editing. Save shows what changes and asks before anything is sent.");
        Render();
        Editor.Focus(FocusState.Programmatic);
    }

    /// <summary>Leaves edit mode without saving; the file shows as Forge held it.</summary>
    public void EndEdit()
    {
        if (!_editing) return;
        _editing = false;
        Editor.Text = "";
        SetHeader(ReadNote());
        Render();
    }

    private string ReadNote() => IsEnvironment && RevealButton.IsChecked != true
        ? "Values are hidden. Reveal shows them; Edit shows them while you edit."
        : _file == ConfigFile.Nginx ? "The site's nginx server block, as Forge holds it."
        : "As Forge holds it.";

    private void SetHeader(string text, bool bad = false)
    {
        _header = text;
        _headerBad = bad;
    }

    private void Render()
    {
        var loaded = _doc is not null;
        TitleText.Text = _file is { } f ? ConfigFiles.Of(f).Label : "";
        HeaderText.Text = _header;
        HeaderText.Visibility = _header == "" ? Visibility.Collapsed : Visibility.Visible;
        HeaderText.Foreground = _headerBad ? Palette.For(Tone.Bad) : Palette.Secondary;

        RevealButton.Visibility = IsEnvironment && !_editing ? Visibility.Visible : Visibility.Collapsed;
        RevealButton.IsEnabled = loaded;
        ReloadButton.IsEnabled = _file is not null && !_editing && !_busy;
        CopyButton.IsEnabled = loaded;
        EditButton.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;
        EditButton.IsEnabled = loaded && !_busy;
        SaveButton.Visibility = DiscardButton.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = _editing && !_busy && IsDirty;
        DiscardButton.IsEnabled = _editing && !_busy;
        Editor.IsReadOnly = _busy;

        Editor.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
        Scroll.Visibility = _editing ? Visibility.Collapsed : Visibility.Visible;
        ReadText.Text = !loaded ? ""
            : _doc!.Content == "" ? "The file is empty."
            : IsEnvironment && RevealButton.IsChecked != true ? ConfigText.MaskEnv(_doc.Content)
            : _doc.Content;
    }

    private void RevealButton_Click(object sender, RoutedEventArgs e)
    {
        SetHeader(ReadNote());
        Render();
    }

    private void WrapButton_Click(object sender, RoutedEventArgs e)
    {
        var wrap = WrapButton.IsChecked == true ? TextWrapping.Wrap : TextWrapping.NoWrap;
        ReadText.TextWrapping = wrap;
        Editor.TextWrapping = wrap;
        Scroll.HorizontalScrollBarVisibility = wrap == TextWrapping.Wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke();

    private void EditButton_Click(object sender, RoutedEventArgs e) => BeginEdit();

    private void SaveButton_Click(object sender, RoutedEventArgs e) => RequestSave();

    private void RequestSave()
    {
        if (!_editing || _busy) return;
        if (!IsDirty) { Reported?.Invoke("Nothing changed", false); return; }
        SaveRequested?.Invoke();
    }

    private async void DiscardButton_Click(object sender, RoutedEventArgs e) => await DiscardAsync();

    /// <summary>Asks first when there is something to lose.</summary>
    private async Task DiscardAsync()
    {
        if (!_editing || _busy) return;
        if (IsDirty && !await WriteConfirm.DiscardAsync(XamlRoot, ActualTheme, What)) return;
        EndEdit();
    }

    private void Editor_TextChanged(object sender, TextChangedEventArgs e) =>
        SaveButton.IsEnabled = _editing && !_busy && IsDirty;

    /// <summary>
    /// Copies the file exactly as Forge sent it (or the edit, with the file's line endings), never
    /// the masked view. An environment file stays out of clipboard history and sync.
    /// </summary>
    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_doc is null) { Reported?.Invoke("Nothing to copy", false); return; }
        var text = _editing ? EditedText : _doc.Content;
        try
        {
            if (IsEnvironment)
            {
                Shell.CopySecret(text);
                Reported?.Invoke("Copied, values included. It stays out of clipboard history.", false);
            }
            else
            {
                Shell.CopyText(text);
                Reported?.Invoke("Copied the " + What, false);
            }
        }
        catch (Exception ex)
        {
            Reported?.Invoke("Could not copy: " + ex.Message, true);
        }
    }

    private async void View_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (!_editing) return;
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.S)
        {
            e.Handled = true;
            RequestSave();
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            await DiscardAsync();
        }
    }
}
