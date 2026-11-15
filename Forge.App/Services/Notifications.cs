using Forge.Core.Model;
using Forge.Core.Service;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace Forge.App.Services;

/// <summary>Deployment toasts. Clicking one opens the site; the address is re-validated on the way out.</summary>
internal static class Notifications
{
    private static bool _registered;

    public static void Initialize(Action<string> openUrl)
    {
        var manager = AppNotificationManager.Default;
        manager.NotificationInvoked += (_, e) =>
        {
            if (e.Arguments.TryGetValue("url", out var url)) openUrl(url);
        };
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
