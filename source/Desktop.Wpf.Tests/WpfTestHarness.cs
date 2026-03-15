using System;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace Desktop.Wpf.Tests;

internal static class WpfTestHarness
{
    public static void EnsureApplication()
    {
        if (Application.Current is not null) return;

        if (Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
        {
            throw new InvalidOperationException("WPF tests must run in STA.");
        }

        _ = new Application
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown
        };
    }

    public static void DoEvents(DispatcherPriority priority = DispatcherPriority.Background)
    {
        EnsureApplication();

        var dispatcher = Application.Current.Dispatcher;
        if (dispatcher.CheckAccess())
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            return;
        }

        dispatcher.Invoke(() =>
        {
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(priority, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
        });
    }
}
