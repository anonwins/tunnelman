using System.Windows.Threading;

namespace Tunnelman;

internal static class DialogCommit
{
    public static bool TryBegin(System.Windows.Controls.Button button, ref bool committing)
    {
        if (committing)
            return false;
        committing = true;
        button.IsDefault = false;
        return true;
    }

    public static void End(System.Windows.Controls.Button button, Action restoreBusy)
    {
        button.Dispatcher.BeginInvoke(() =>
        {
            button.IsDefault = true;
            restoreBusy();
        }, DispatcherPriority.Input);
    }
}
