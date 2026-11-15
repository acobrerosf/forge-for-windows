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
    public static async Task<bool> AskAsync(XamlRoot root, ElementTheme theme, string title, string body, string cta,
        Action<ContentDialog?>? shown = null)
    {
        var text = new StackPanel { Spacing = 12 };
        text.Children.Add(new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap });
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
            _ => ($"{spec.Label}?", $"This will {what} on {subject}.", spec.Label.Split(' ')[0]),
        };
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
