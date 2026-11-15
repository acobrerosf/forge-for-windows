using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace Forge.App;

/// <summary>
/// A custom entry point so a second launch (from the Start menu, the Run
/// key, or a clicked toast) hands its activation to the running instance
/// instead of starting another poller against the same rate budget.
/// </summary>
public static class Program
{
    public const string InstanceKey = "ForgeForWindows.main";

    [STAThread]
    private static int Main(string[] args)
    {
        WinRT.ComWrappersSupport.InitializeComWrappers();
        if (RedirectedToRunningInstance()) return 0;

        Application.Start(_initialization =>
        {
            var context = new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread());
            SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });
        return 0;
    }

    private static bool RedirectedToRunningInstance()
    {
        var main = AppInstance.FindOrRegisterForKey(InstanceKey);
        if (main.IsCurrent) return false;

        var activation = AppInstance.GetCurrent().GetActivatedEventArgs();
        // This launch was the user's own action, so it may take the foreground — and can pass that on,
        // or the panel the running instance opens would come up behind whatever has focus.
        AllowSetForegroundWindow(main.ProcessId);
        // The redirect is async and this is an STA thread with no dispatcher yet.
        using var done = new ManualResetEventSlim();
        _ = Task.Run(async () =>
        {
            try { await main.RedirectActivationToAsync(activation); }
            finally { done.Set(); }
        });
        done.Wait(TimeSpan.FromSeconds(5));
        return true;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(uint processId);
}
