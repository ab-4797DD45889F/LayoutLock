namespace LayoutLock;

internal static class Program
{
    private const string Usage = """
        LayoutLock - saves and restores window positions across different monitor sets.

        Usage:
          LayoutLock              run the tray agent (same as 'agent')
          LayoutLock agent        run the tray agent: auto-save on hibernate/sleep, auto-restore on wake
          LayoutLock save         save current window positions
          LayoutLock restore      restore window positions from the last save
          LayoutLock install      start the agent automatically at logon (and start it now)
          LayoutLock uninstall    remove the logon autostart

        Data and log: %LOCALAPPDATA%\LayoutLock
        """;

    [STAThread]
    private static int Main(string[] args)
    {
        var command = args.Length == 0 ? "agent" : args[0].TrimStart('-', '/').ToLowerInvariant();

        if (command != "agent")
            NativeMethods.AttachConsole(NativeMethods.ATTACH_PARENT_PROCESS);

        switch (command)
        {
            case "agent":
                return RunAgent();

            case "save":
                var snapshot = WindowCapture.Capture("manual");
                snapshot.Save(Storage.SnapshotPath);
                Storage.Log($"Saved {snapshot.Windows.Count} window(s) on {snapshot.MonitorCount} monitor(s) to {Storage.SnapshotPath}");
                return 0;

            case "restore":
                var saved = Snapshot.Load(Storage.SnapshotPath);
                if (saved is null)
                {
                    Storage.Log($"No snapshot found at {Storage.SnapshotPath}");
                    return 1;
                }
                var result = WindowRestore.Restore(saved, Storage.Log);
                Storage.Log($"{result.Summary} from snapshot of {saved.SavedAt:g}");
                return result.Failed == 0 ? 0 : 2;

            case "install":
                Autostart.Enable();
                Console.WriteLine("Autostart enabled. Starting the agent...");
                var (fileName, argumentPrefix) = Autostart.LaunchCommand();
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(fileName, argumentPrefix + "agent")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                return 0;

            case "uninstall":
                Autostart.Disable();
                NotificationIdentity.Remove();
                Console.WriteLine("Autostart removed. Exit the running agent from its tray icon.");
                return 0;

            default:
                Console.WriteLine(Usage);
                return command is "help" or "h" or "?" ? 0 : 1;
        }
    }

    private static int RunAgent()
    {
        using var mutex = new Mutex(true, @"Local\LayoutLock.Agent", out var isFirst);
        if (!isFirst)
            return 0;

        // dotnet.exe is a console app; the tray agent has no use for its window.
        if (Autostart.IsHostedByDotnet)
            NativeMethods.FreeConsole();

        // Must happen before the tray icon's window is created.
        NotificationIdentity.Apply();
        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAgent());
        return 0;
    }
}
