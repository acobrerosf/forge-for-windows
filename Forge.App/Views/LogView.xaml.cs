using Forge.App.Services;
using Forge.Core.Model;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Forge.App.Views;

/// <summary>
/// A log as the expanded window shows one: find (which narrows to the matching
/// lines), wrap, reload, copy and save. It follows the tail as new text arrives,
/// unless a search is narrowing it.
/// </summary>
public sealed partial class LogView : UserControl
{
    private IReadOnlyList<string> _lines = [];
    private string _fileName = "forge.log";
    private string _empty = "Nothing in it";
    private bool _loading;
    private string? _error;

    public LogView()
    {
        InitializeComponent();
    }

    /// <summary>Raised by the Reload button; the owner fetches again and calls <see cref="Show"/>.</summary>
    public event Action? ReloadRequested;

    /// <summary>Whether the Reload button is offered (a finished command's output can't be fetched again).</summary>
    public bool CanReload
    {
        get => ReloadButton.Visibility == Visibility.Visible;
        set => ReloadButton.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Icon-only buttons and a narrower search, for a log that shares its row with something else.</summary>
    public bool Compact
    {
        get => ReloadLabel.Visibility == Visibility.Collapsed;
        set
        {
            var labels = value ? Visibility.Collapsed : Visibility.Visible;
            WrapLabel.Visibility = ReloadLabel.Visibility = CopyLabel.Visibility = SaveLabel.Visibility = labels;
            FindColumn.Width = new GridLength(value ? 180 : 260);
        }
    }

    public void ShowLoading(string header = "")
    {
        _loading = true;
        _error = null;
        HeaderText.Text = header;
        HeaderText.Foreground = Palette.Secondary;
        Render(scroll: false);
    }

    public void Show(IReadOnlyList<string> lines, string header, string fileName, string empty = "Nothing in it", bool bad = false)
    {
        var grew = lines.Count != _lines.Count;
        _loading = false;
        _error = null;
        _lines = lines;
        _fileName = fileName;
        _empty = empty;
        HeaderText.Text = header;
        HeaderText.Foreground = bad ? Palette.For(Tone.Bad) : Palette.Secondary;
        Render(scroll: grew);
    }

    public void ShowError(string error)
    {
        _loading = false;
        _error = error;
        HeaderText.Text = error;
        HeaderText.Foreground = Palette.For(Tone.Bad);
        Render(scroll: false);
    }

    public void Clear()
    {
        _lines = [];
        _error = null;
        _loading = false;
        FindBox.Text = "";
        HeaderText.Text = "";
        Render(scroll: false);
    }

    private void Render(bool scroll)
    {
        LoadingRing.IsActive = _loading;
        HeaderText.Visibility = HeaderText.Text == "" ? Visibility.Collapsed : Visibility.Visible;
        var find = FindBox.Text.Trim();
        var shown = find == "" ? _lines : _lines.Where(l => l.Contains(find, StringComparison.OrdinalIgnoreCase)).ToList();
        MatchText.Text = find == "" ? "" : shown.Count == 1 ? "1 matching line" : $"{shown.Count} matching lines";
        LogText.Text = shown.Count > 0 ? string.Join("\n", shown)
            : _loading || _error is not null ? ""
            : find != "" ? "No line matches" : _empty;
        // The answer is usually at the end: follow the tail, but not while a search is narrowing the view.
        if (scroll && find == "" && shown.Count > 0)
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low,
                () => Scroll.ChangeView(0, Scroll.ScrollableHeight, null, disableAnimation: true));
    }

    private void FindBox_TextChanged(object sender, TextChangedEventArgs e) => Render(scroll: false);

    private void WrapButton_Click(object sender, RoutedEventArgs e)
    {
        var wrap = WrapButton.IsChecked == true;
        LogText.TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
        Scroll.HorizontalScrollBarVisibility = wrap ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
    }

    private void ReloadButton_Click(object sender, RoutedEventArgs e) => ReloadRequested?.Invoke();

    /// <summary>What a Copy or Save did, for the owner to report.</summary>
    public event Action<string, bool>? Reported;

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lines.Count == 0) { Reported?.Invoke("Nothing to copy", false); return; }
        Shell.CopyText(string.Join(Environment.NewLine, _lines));
        Reported?.Invoke($"Copied {ForgeText.Pluralize(_lines.Count, "line")}", false);
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        if (_lines.Count == 0) { Reported?.Invoke("Nothing to save", false); return; }
        try
        {
            var folder = Shell.DownloadsFolder();
            var name = Path.GetFileNameWithoutExtension(_fileName);
            var ext = Path.GetExtension(_fileName);
            var path = Path.Combine(folder, _fileName);
            for (var i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{name}-{i}{ext}");
            File.WriteAllText(path, string.Join(Environment.NewLine, _lines) + Environment.NewLine);
            Reported?.Invoke("Saved to " + path, false);
        }
        catch (Exception ex)
        {
            Reported?.Invoke("Could not save: " + ex.Message, true);
        }
    }
}
