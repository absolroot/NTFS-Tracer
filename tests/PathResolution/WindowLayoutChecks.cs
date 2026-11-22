using JournalDesk;

internal static class WindowLayoutChecks
{
    internal static void Run()
    {
        var saved = new WindowLayout(1211.5, 654.25, true);
        if (WindowLayout.Parse(saved.Serialize()) != saved
            || saved.Fit(760, 530, 640, 490) != new WindowLayout(640, 490, true)
            || new WindowLayout(25, 30, false).Fit(760, 530, 1280, 720) != new WindowLayout(760, 530, false))
            throw new InvalidOperationException("Restore normal dimensions and maximize state within this display's work area.");

        foreach (string? value in new[] { null, "", "810 600", "NaN 640 Normal", "900 Infinity Normal", "0 500 Normal", "930 610 Minimized" })
            if (WindowLayout.Parse(value) != null)
                throw new InvalidOperationException("Missing, invalid and obsolete window states must use the default layout.");
    }
}
