using Forge.Core.Model;
using Forge.Core.Service;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Forge.App.Services;

/// <summary>
/// Deployment toasts. Clicking the toast opens the site; its buttons open the
/// deployment log, the site or the site in Forge. Every address is re-validated
/// on the way out.
/// </summary>
internal static class Notifications
{
    private static bool _registered;

    /// <param name="invoked">Gets the toast's or the clicked button's arguments.</param>
    public static void Initialize(Action<IDictionary<string, string>> invoked)
    {
        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += (_, e) => invoked(e.Arguments);
        try
        {
            manager.Register();
            _registered = true;
        }
        catch (Exception e)
        {
            Log.Write("Toast registration failed: " + e.Message);
        }
    }

    public static void ShowDeployment(DeploymentNotice notice)
    {
        if (!_registered) return;
        var headline = ForgeText.NotifyText(notice.Headline);
        var builder = new AppNotificationBuilder()
            .AddText(headline == "" ? "Forge" : headline)
            .AddText(ForgeText.PlainText(notice.Description));
        var url = ForgeText.ExternalUrl(notice.Url);
        if (url != "") builder.AddArgument("url", url);

        AppNotificationButton Button(string text, string action)
        {
            var button = new AppNotificationButton(text)
                .AddArgument("action", action)
                .AddArgument("org", notice.Org)
                .AddArgument("site", notice.Site.Key);
            if (url != "") button.AddArgument("url", url);
            return button;
        }

        // A finished deploy is usually checked by looking at the site; a failed one by reading why.
        if (notice.Failed)
        {
            builder.AddButton(Button("View log", "log"));
            builder.AddButton(Button("Open in Forge", "forge"));
        }
        else
        {
            if (url != "") builder.AddButton(Button("Open site", "open"));
            builder.AddButton(Button("View log", "log"));
        }

        try
        {
            AppNotificationManager.Default.Show(builder.BuildNotification());
#if DEBUG
            Log.Write($"Toast: {headline} — {notice.Description}");
#endif
        }
        catch (Exception e)
        {
            Log.Write("Toast failed: " + e.Message);
        }
    }
}
