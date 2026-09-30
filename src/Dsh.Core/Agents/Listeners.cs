namespace Dsh.Core;

/// <summary>Raising an event whose listeners are someone else's code: a listener that throws must not stop the ones after it, and
/// must never cost the raiser what it was in the middle of (a server slot, a run's bookkeeping).</summary>
internal static class Listeners
{
    public static void Raise(Action? handlers)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action>())
        {
            try
            {
                handler();
            }
            catch (Exception)
            {
                // Theirs to handle.
            }
        }
    }

    public static void Raise<T>(Action<T>? handlers, T value)
    {
        if (handlers is null) return;
        foreach (var handler in handlers.GetInvocationList().Cast<Action<T>>())
        {
            try
            {
                handler(value);
            }
            catch (Exception)
            {
                // Theirs to handle.
            }
        }
    }
}
