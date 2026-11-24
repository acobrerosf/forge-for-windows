using Forge.App.Services;
using Forge.Core.Model;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.Core;

namespace Forge.App.Views;

/// <summary>
/// The one way a write asks before it is sent, in every window. Cancel has the
/// focus, so Enter and Esc both drop it; the write goes only by its own button
/// or a capital Y, the same deliberate press the keyboard flow has always needed.
/// </summary>
internal static class WriteConfirm
{
    /// <param name="shown">Gets the dialog while it is up, so the caller can close it early (the flyout hiding, say).</param>
    /// <param name="detail">Shown below the body: the diff a config save will make, say.</param>
    public static async Task<bool> AskAsync(XamlRoot root, ElementTheme theme, string title, string body, string cta,
        Action<ContentDialog?>? shown = null, UIElement? detail = null)
    {
        var text = new StackPanel { Spacing = 12 };
        text.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
        if (detail is not null) text.Children.Add(detail);
        text.Children.Add(new TextBlock
        {
            Text = "Shift+Y also confirms. Enter or Esc cancels.",
            Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
            Foreground = Palette.Secondary,
        });
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = text,
            // A diff needs more room than a sentence; ContentDialog caps its own width otherwise.
            Resources = detail is null ? new ResourceDictionary() : new ResourceDictionary { ["ContentDialogMaxWidth"] = 900.0 },
            PrimaryButtonText = cta,
            CloseButtonText = "Cancel",
            // No default button: a default is drawn as the accent, and the accent belongs on the write.
            DefaultButton = ContentDialogButton.None,
            PrimaryButtonStyle = (Style)Application.Current.Resources["AccentButtonStyle"],
            RequestedTheme = theme,
        };
        dialog.Opened += (_, _) =>
        {
            if (FindDescendant(dialog, "CloseButton") is Control cancel) cancel.Focus(FocusState.Keyboard);
        };
        var byKey = false;
        dialog.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Y && IsDown(VirtualKey.Shift) && !IsDown(VirtualKey.Control))
            {
                e.Handled = true;
                byKey = true;
                dialog.Hide();
            }
        };

        shown?.Invoke(dialog);
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary || byKey;
        }
        catch (Exception e)
        {
            // ShowAsync throws when another dialog is already open in the same window.
            Log.Write("Confirm dialog: " + e.Message);
            return false;
        }
        finally
        {
            shown?.Invoke(null);
        }
    }

    /// <summary>The dialog's wording for an action from a server's or site's list.</summary>
    public static (string Title, string Body, string Cta) Wording(ActionSpec spec, string subject)
    {
        var what = spec.ArmedText.StartsWith("press Y to ", StringComparison.Ordinal) ? spec.ArmedText["press Y to ".Length..] : spec.Label;
        return spec.Id switch
        {
            "maintenance" when spec.ArmIntent == "on" =>
                ("Turn on maintenance mode?", $"Visitors to {subject} get a 503 page until you turn it off.", "Turn on"),
            "maintenance" => ("Turn off maintenance mode?", $"{subject} starts serving visitors again.", "Turn off"),
            "reboot" => ($"Reboot {subject}?", "Every site on it is offline until it is back up.", "Reboot"),
            "quick-deploy" when spec.ArmIntent == "on" =>
                ("Turn on quick deploy?", $"Every push to the branch deploys {subject} straight away.", "Turn on"),
            "quick-deploy" => ("Turn off quick deploy?", $"Pushes stop deploying {subject}. Deploy still works by hand and through the deploy hook.", "Turn off"),
            "deploy-hook-regenerate" => ("Regenerate the deploy hook URL?",
                $"The current URL for {subject} stops working at once. Any CI or script that calls it will fail until you give it the new one.", "Regenerate"),
            "webhook-delete" => ("Delete this webhook?", $"Forge stops calling it after deploys of {subject}.", "Delete"),
            "webhook-add" => ("Add this webhook?", $"Forge will call it after every deploy of {subject}.", "Add"),
            "deploy-key-create" => ("Create a deploy key?",
                $"Forge makes an SSH key for {subject}. Add the public key to the repository so Forge can pull with it.", "Create"),
            "deploy-key-delete" => ("Delete the deploy key?",
                $"If {subject} pulls its repository with this key, deploys fail until a new key is added to the repository.", "Delete"),
            _ => ($"{spec.Label}?", $"This will {what} on {subject}.", spec.Label.Split(' ')[0]),
        };
    }

    /// <summary>
    /// What a config save changes, as a confirm dialog shows it: removed lines in red, added in
    /// green, unchanged ones grey, gaps and notes in italics. Scrolls when long.
    /// </summary>
    public static UIElement DiffBlock(IReadOnlyList<DiffLine> diff)
    {
        var lines = new StackPanel();
        foreach (var line in diff)
        {
            var (prefix, brush) = line.Kind switch
            {
                DiffKind.Added => ("+ ", Palette.For(Tone.Ok)),
                DiffKind.Removed => ("− ", Palette.For(Tone.Bad)),
                DiffKind.Same => ("  ", Palette.Secondary),
                _ => ("", Palette.Tertiary),
            };
            lines.Children.Add(new TextBlock
            {
                Text = line.Kind is DiffKind.Gap ? "⋯ " + line.Text : prefix + line.Text,
                Foreground = brush,
                FontStyle = line.Kind is DiffKind.Gap or DiffKind.Note ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
                FontFamily = (FontFamily)Application.Current.Resources["MonoFont"],
                FontSize = 12.5,
                TextWrapping = TextWrapping.NoWrap,
                IsTextSelectionEnabled = false,
            });
        }
        return new Border
        {
            Child = new ScrollViewer
            {
                Content = lines,
                MaxHeight = 360,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                Padding = new Thickness(10, 8, 14, 10),
            },
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
    }

    /// <summary>Asks before an unsaved edit is thrown away. Keep editing has the focus; nothing is sent either way.</summary>
    public static async Task<bool> DiscardAsync(XamlRoot root, ElementTheme theme, string what)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Discard your changes?",
            Content = new TextBlock { Text = $"Your edit to the {what} hasn't been saved. Discarding it leaves Forge's copy as it is.", TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "Discard",
            CloseButtonText = "Keep editing",
            DefaultButton = ContentDialogButton.Close,
            RequestedTheme = theme,
        };
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception e)
        {
            Log.Write("Discard dialog: " + e.Message);
            return false;
        }
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static FrameworkElement? FindDescendant(DependencyObject root, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name) return fe;
            if (FindDescendant(child, name) is { } found) return found;
        }
        return null;
    }
}
