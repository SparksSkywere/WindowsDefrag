using System.Windows;
using System.Windows.Threading;

namespace WindowsDefrag.Helpers;

internal static class UiThread
{
    public static void Post(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.InvokeAsync(action, DispatcherPriority.DataBind);
    }

    public static void Send(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action, DispatcherPriority.DataBind);
    }
}
