using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Forge.App.Views;

/// <summary>
/// A list on the left and, beside it, what the chosen row printed — deployment
/// history, server logs, background processes, scheduled jobs. Nothing is read
/// until the owner loads the list; nothing more until a row is chosen.
/// </summary>
public sealed partial class ListLogPane : UserControl
{
    private readonly ObservableCollection<ListEntry> _items = [];
    private string _next = "";
    private bool _filling;

    public ListLogPane()
    {
        InitializeComponent();
        Items.ItemsSource = _items;
        Log.ReloadRequested += () =>
        {
            if (Selected is { } entry) Picked?.Invoke(entry);
            else ListReloadRequested?.Invoke();
        };
    }

    /// <summary>The window's row template, which this control cannot reach from its own resources.</summary>
    public DataTemplate? ItemTemplate { get => Items.ItemTemplate; set => Items.ItemTemplate = value; }
    public Style? ItemContainerStyle { get => Items.ItemContainerStyle; set => Items.ItemContainerStyle = value; }
    public string ListName { set => Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(Items, value); }

    /// <summary>A row was chosen, or Reload was pressed while one is: show what it printed.</summary>
    public event Action<ListEntry>? Picked;
    /// <summary>Reload pressed with no row chosen: read the list again.</summary>
    public event Action? ListReloadRequested;
    /// <summary>"Load more" pressed; <see cref="Next"/> is the cursor to ask with.</summary>
    public event Action? MoreRequested;
    public event Action? ActionRequested;

    // Internal: a public ListEntry property makes the XAML compiler try to construct one.
    internal ListEntry? Selected => Items.SelectedItem as ListEntry;
    public string Next => _next;
    public int Count => _items.Count;

    /// <summary>
    /// A list kept newest first (deployments, events): whoever was reading the newest
    /// moves on to the new newest when it is read again, rather than staying put.
    /// </summary>
    public bool FollowFirst { get; set; }

    // What was chosen before a fresh read, so the read can open on it again.
    private string _keep = "";
    private bool _keepWasFirst;

    /// <summary>Starts a fresh read of the list, or marks a "load more" as in flight.</summary>
    public void BeginList(bool more, string loading)
    {
        MoreButton.IsEnabled = false;
        if (more) return;
        _keep = Selected?.Key ?? "";
        _keepWasFirst = _keep != "" && _keep == FirstKey;
        Fill(() => _items.Clear());
        _next = "";
        MoreButton.Visibility = Visibility.Collapsed;
        SetAction(null);
        Log.CanClear = false;
        Log.Show([], "", "forge.log", loading);
    }

    /// <summary>
    /// Shows the rows and, on a fresh read, opens one: the row chosen before if it is still
    /// listed, otherwise the first. Every list opens on something; none waits to be picked.
    /// </summary>
    public void ShowList(IEnumerable<ListEntry> items, bool more, string next, string empty)
    {
        Fill(() =>
        {
            if (!more) _items.Clear();
            foreach (var item in items) _items.Add(item);
        });
        _next = next;
        MoreButton.IsEnabled = true;
        MoreButton.Visibility = next != "" ? Visibility.Visible : Visibility.Collapsed;
        if (more) return;
        if (_items.Count == 0)
        {
            Log.Show([], "", "forge.log", empty);
            return;
        }
        var follow = FollowFirst && _keepWasFirst;
        if (_keep == "" || follow || !Select(_keep)) Select(FirstKey);
        _keep = "";
    }

    /// <summary>A failed list read lands in the log's header, as an event list's does — that is where the scope message is read.</summary>
    public void ShowListError(string error)
    {
        MoreButton.IsEnabled = true;
        Log.ShowError(error);
    }

    public void Reset()
    {
        Fill(() => _items.Clear());
        _next = "";
        _keep = "";
        Log.CanClear = false;
        MoreButton.Visibility = Visibility.Collapsed;
        SetAction(null);
        Log.Clear();
    }

    /// <summary>The write for the chosen row (Restart), or none. <paramref name="note"/> says why it is off.</summary>
    public void SetAction(string? text, bool enabled = true, string note = "")
    {
        ActionBar.Visibility = text is null ? Visibility.Collapsed : Visibility.Visible;
        ActionButton.Content = text ?? "";
        ActionButton.IsEnabled = enabled;
        ActionText.Text = note;
    }

    /// <summary>The top row's key: in a list kept newest first, the latest one.</summary>
    public string FirstKey => _items.Count > 0 ? _items[0].Key : "";

    /// <summary>Chooses the row with this key (after a reload, say), which shows what it printed. False when it isn't listed.</summary>
    public bool Select(string key)
    {
        if (_items.FirstOrDefault(e => e.Key == key) is not { } entry) return false;
        if (Items.SelectedItem == entry) Picked?.Invoke(entry);
        else Items.SelectedItem = entry;
        return true;
    }

    private void Fill(Action change)
    {
        _filling = true;
        try { change(); }
        finally { _filling = false; }
    }

    private void Items_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_filling) return;
        if (Selected is { } entry) Picked?.Invoke(entry);
    }

    private void MoreButton_Click(object sender, RoutedEventArgs e)
    {
        if (_next != "") MoreRequested?.Invoke();
    }

    private void ActionButton_Click(object sender, RoutedEventArgs e) => ActionRequested?.Invoke();
}
