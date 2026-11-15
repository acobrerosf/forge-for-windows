using System.Collections.ObjectModel;

namespace Forge.App.Views;

/// <summary>
/// Brings a list on screen up to date without rebuilding it. The service reports
/// several times per refresh, mostly with nothing new; swapping the whole item
/// source each time tears down every row and makes the list flicker. Here a row
/// is replaced only when how it looks changed, and the list is rebuilt only when
/// its rows themselves changed (a fold, a filter, a site added or gone).
/// </summary>
internal static class ListSync
{
    /// <param name="identity">What makes a row the same row from one report to the next.</param>
    /// <param name="looks">Everything the row draws; a difference means it is redrawn.</param>
    /// <returns>Whether anything on screen changed.</returns>
    public static bool Apply<T>(ObservableCollection<T> shown, IReadOnlyList<T> items, Func<T, string> identity, Func<T, string> looks)
    {
        if (shown.Count == items.Count && shown.Select(identity).SequenceEqual(items.Select(identity)))
        {
            var changed = false;
            for (var i = 0; i < items.Count; i++)
            {
                if (looks(shown[i]) == looks(items[i])) continue;
                shown[i] = items[i];
                changed = true;
            }
            return changed;
        }
        shown.Clear();
        foreach (var item in items) shown.Add(item);
        return true;
    }
}
