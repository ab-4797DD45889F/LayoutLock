namespace WindowPositionTool;

internal static class Program
{
    private const string Usage = """
        WindowPositionTool - saves and restores window positions across different monitor sets.

        Usage:
          WindowPositionTool                 run the tray agent (same as 'agent')
          WindowPositionTool agent           run the tray agent: auto-save on hibernate/sleep, auto-restore on wake
          WindowPositionTool save            save current window positions
          WindowPositionTool restore         restore window positions from the last save
          WindowPositionTool install         start the agent automatically at logon (and start it now)
          WindowPositionTool uninstall       remove the logon autostart

        Data and log: %LOCALAPPDATA%\WindowPositionTool
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
                Console.WriteLine("Autostart removed. Exit the running agent from its tray icon.");
                return 0;

            default:
                Console.WriteLine(Usage);
                return command is "help" or "h" or "?" ? 0 : 1;
        }
    }

    private static int RunAgent()
    {
        using var mutex = new Mutex(true, @"Local\WindowPositionTool.Agent", out var isFirst);
        if (!isFirst)
            return 0;

        // dotnet.exe is a console app; the tray agent has no use for its window.
        if (Autostart.IsHostedByDotnet)
            NativeMethods.FreeConsole();

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayAgent());
        return 0;
    }
}
