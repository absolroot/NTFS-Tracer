using System.Windows;

namespace JournalDesk;

internal static class WindowLifetime
{
    internal static CancellationToken UntilClosed(Window window)
    {
        var lifetime = new CancellationTokenSource();
        window.Closed += (_, _) =>
        {
            try
            {
                lifetime.Cancel();
            }
            finally { lifetime.Dispose(); }
        };
        return lifetime.Token;
    }
}
