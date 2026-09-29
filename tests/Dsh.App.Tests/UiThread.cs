using System.Runtime.ExceptionServices;
using System.Windows.Threading;

namespace Dsh.App.Tests;

/// <summary>Runs an async test body on its own STA thread with a WPF dispatcher, the way AgentHost
/// runs in the app: every continuation comes back to that one thread.</summary>
public static class UiThread
{
    public static void Run(Func<Task> body, TimeSpan? timeout = null)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            var frame = new DispatcherFrame();
            dispatcher.BeginInvoke(async () =>
            {
                try
                {
                    await body();
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    frame.Continue = false;
                }
            });
            Dispatcher.PushFrame(frame);
            dispatcher.InvokeShutdown();
        })
        {
            IsBackground = true,
            Name = "test UI thread",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(timeout ?? TimeSpan.FromMinutes(3))) throw new TimeoutException("The test's UI thread didn't finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
