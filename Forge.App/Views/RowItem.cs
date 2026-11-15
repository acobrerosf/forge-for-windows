using Forge.Core.Model;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace Forge.App.Views;

public enum RowKind { Org, Server, Site, Action, Event, Recipe, Monitor, Heartbeat, Info, More }

/// <summary>
/// One panel row, whatever it stands for. The tree, every action list and
/// every feed are one flat list, so the cursor is a single index.
/// </summary>
public sealed class RowItem
{
    public required string Key { get; init; }
    public RowKind Kind { get; init; }
    public string Org { get; init; } = "";
    public string Title { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Status { get; set; } = "";
    public string Time { get; init; } = "";
    public Tone Tone { get; init; } = Tone.Idle;
    public int Depth { get; init; }
    public bool Enabled { get; init; } = true;
    public bool? Expanded { get; init; }
    public bool HasCog { get; init; }
    /// <summary>Non-empty only on rows that write; what an arm is matched against.</summary>
    public string ArmKey { get; init; } = "";
    public bool Armed { get; set; }
    public bool Busy { get; set; }
    public object? Payload { get; init; }

    // ----------------------------------------------------------- presentation

    public Thickness Indent => new(12 + Depth * 22, 6, 6, 6);
    public double RowOpacity => Enabled ? 1.0 : 0.55;
    public Visibility DetailVisibility => Detail == "" ? Visibility.Collapsed : Visibility.Visible;
    public Visibility TimeVisibility => Time == "" ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CogVisibility => HasCog ? Visibility.Visible : Visibility.Collapsed;
    /// <summary>Server rows always keep the chevron column, so names line up whether or not a server has sites.</summary>
    public Visibility ChevronVisibility => Kind == RowKind.Server ? Visibility.Visible : Visibility.Collapsed;
    public double ChevronOpacity => Expanded is null ? 0 : 1;
    public string Chevron => Expanded == true ? "" : "";
    public Windows.UI.Text.FontWeight TitleWeight =>
        Kind is RowKind.Org or RowKind.Server ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;

    public Visibility DotVisibility => Kind is RowKind.Action or RowKind.Info or RowKind.More && Tone == Tone.Idle
        ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Warn is drawn as a ring rather than a colour, so it reads in every theme.</summary>
    public Brush DotFill => Tone == Tone.Warn ? Palette.Transparent : Palette.For(Tone);
    public Brush DotStroke => Tone == Tone.Warn ? Palette.For(Tone.Warn) : Palette.Transparent;

    public string StatusText => Busy ? "sending…" : Status;

    /// <summary>What a screen reader announces for the row.</summary>
    public override string ToString() =>
        string.Join(", ", new[] { Title, StatusText, Time, Detail }.Where(s => s != ""));
    public Brush StatusBrush => Armed || Busy ? Palette.Accent : Tone is Tone.Bad or Tone.Busy ? Palette.For(Tone) : Palette.Secondary;
}

internal static class Palette
{
    private static Brush Resource(string key, Windows.UI.Color fallback) =>
        Application.Current.Resources.TryGetValue(key, out var value) && value is Brush b ? b : new SolidColorBrush(fallback);

    public static Brush Transparent { get; } = new SolidColorBrush(Colors.Transparent);
    public static Brush Accent => Resource("AccentTextFillColorPrimaryBrush", Windows.UI.Color.FromArgb(255, 0, 120, 212));
    public static Brush Secondary => Resource("TextFillColorSecondaryBrush", Colors.Gray);
    public static Brush Tertiary => Resource("TextFillColorTertiaryBrush", Colors.Gray);

    public static Brush For(Tone tone) => tone switch
    {
        Tone.Bad => Resource("SystemFillColorCriticalBrush", Windows.UI.Color.FromArgb(255, 196, 43, 28)),
        Tone.Busy => Resource("AccentFillColorDefaultBrush", Windows.UI.Color.FromArgb(255, 0, 120, 212)),
        Tone.Ok => Resource("SystemFillColorSuccessBrush", Windows.UI.Color.FromArgb(255, 15, 123, 15)),
        Tone.Warn => Resource("SystemFillColorCautionBrush", Windows.UI.Color.FromArgb(255, 157, 93, 0)),
        _ => Tertiary,
    };

    public static Tone ToneOf(Health health) => health switch
    {
        Health.Bad => Tone.Bad,
        Health.Error or Health.Setup => Tone.Warn,
        Health.Busy => Tone.Busy,
        Health.Maintenance => Tone.Warn,
        _ => Tone.Ok,
    };
}
