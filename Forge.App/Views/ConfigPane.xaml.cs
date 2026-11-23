using System.Collections.ObjectModel;
using Forge.Core.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Forge.App.Views;

/// <summary>
/// A site's three config files beside the editor. The list is known without asking, so only the
/// opened file costs a request. Choosing another file while an edit is unsaved asks first.
/// </summary>
public sealed partial class ConfigPane : UserControl
{
    private readonly ObservableCollection<ListEntry> _items = [];
    private bool _filling;
    private ListEntry? _shown;

    public ConfigPane()
    {
        InitializeComponent();
        Items.ItemsSource = _items;
    }

    /// <summary>The window's row template, which this control cannot reach from its own resources.</summary>
    public DataTemplate? ItemTemplate { get => Items.ItemTemplate; set => Items.ItemTemplate = value; }
    public Style? ItemContainerStyle { get => Items.ItemContainerStyle; set => Items.ItemContainerStyle = value; }

    /// <summary>A file was chosen: read it.</summary>
    public event Action<ListEntry>? Picked;

    public bool IsDirty => Editor.IsDirty;
    public bool HasFiles => _items.Count > 0;

    /// <summary>Lists the three files and opens one: the one open before, if any, otherwise the first.</summary>
    public void ShowFiles()
    {
        var keep = _shown?.Key ?? "";
        _filling = true;
        try
        {
            _items.Clear();
            foreach (var info in ConfigFiles.All)
                _items.Add(new ListEntry { Key = info.Key, Title = info.Label, Detail = info.Detail, Payload = info.File });
        }
        finally
        {
            _filling = false;
        }
        _shown = null;
        Select(_items.FirstOrDefault(e => e.Key == keep) ?? _items[0]);
    }

    /// <summary>The open file, read again (after Reload, say).</summary>
    public void Reopen()
    {
        if (_shown is { } entry) Picked?.Invoke(entry);
    }

    public void Reset()
    {
        _filling = true;
        try { _items.Clear(); }
        finally { _filling = false; }
        _shown = null;
        Editor.Reset();
    }

    private void Select(ListEntry entry)
    {
        _filling = true;
        try { Items.SelectedItem = entry; }
        finally { _filling = false; }
        _shown = entry;
        Picked?.Invoke(entry);
    }

    private async void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling || Items.SelectedItem is not ListEntry wanted || wanted == _shown) return;
        if (Editor.IsDirty)
        {
            // Stay on the edited file until the reader says to drop the edit.
            _filling = true;
            try { Items.SelectedItem = _shown; }
            finally { _filling = false; }
            if (!await WriteConfirm.DiscardAsync(XamlRoot, ActualTheme, Editor.What)) return;
        }
        Editor.EndEdit();
        Select(wanted);
    }
}
