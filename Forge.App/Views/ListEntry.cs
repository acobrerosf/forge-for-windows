using Forge.Core.Model;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Forge.App.Views;

public enum EntryKind { Org, Server, Site, Item }

/// <summary>One row in the expanded window's lists: an organization or server heading, a site, a heartbeat or an event.</summary>
public sealed class ListEntry
{
    public required string Key { get; init; }
    public EntryKind Kind { get; init; } = EntryKind.Item;
    public string Org { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Status { get; init; } = "";
    public string Time { get; init; } = "";
    public Tone Tone { get; init; } = Tone.Idle;
    public object? Payload { get; init; }

    public double Height => Kind == EntryKind.Org ? 36 : Kind == EntryKind.Server ? 48 : 44;
    public Thickness Indent => Kind switch
    {
        EntryKind.Org => new Thickness(10, 10, 8, 2),
        EntryKind.Server => new Thickness(10, 6, 8, 4),
        EntryKind.Site => new Thickness(22, 4, 8, 4),
        _ => new Thickness(12, 6, 10, 6),
    };
    public Visibility DotVisibility => Kind is EntryKind.Org or EntryKind.Server ? Visibility.Collapsed : Visibility.Visible;
    public Visibility GlyphVisibility => Kind == EntryKind.Server ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DetailVisibility => Detail == "" ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TimeVisibility => Time == "" ? Visibility.Collapsed : Visibility.Visible;
    public Brush DotFill => Tone == Tone.Warn ? Palette.Transparent : Palette.For(Tone);
    public Brush DotStroke => Tone == Tone.Warn ? Palette.For(Tone.Warn) : Palette.Transparent;
    public double TitleSize => Kind == EntryKind.Org ? 12 : 14;
    public Brush TitleBrush => Kind == EntryKind.Org ? Palette.Secondary : Palette.Primary;
    public Windows.UI.Text.FontWeight TitleWeight =>
        Kind is EntryKind.Org or EntryKind.Server ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
    public Brush StatusBrush => Tone is Tone.Bad or Tone.Busy or Tone.Warn ? Palette.Text(Tone) : Palette.Secondary;
    public Windows.UI.Text.FontWeight StatusWeight =>
        Tone is Tone.Bad or Tone.Busy or Tone.Warn ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public override string ToString() => string.Join(", ", new[] { Title, Status, Time, Detail }.Where(s => s != ""));
}
