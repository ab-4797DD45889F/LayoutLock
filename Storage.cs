using Microsoft.Win32;

namespace LayoutLock;

internal static class Storage
{
    public static readonly string Folder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LayoutLock");

    public static readonly string SnapshotPath = Path.Combine(Folder, "snapshot.json");
    public static readonly string LogPath = Path.Combine(Folder, "log.txt");

    private const long MaxLogBytes = 1_000_000;
    private static readonly object LogLock = new();

    public static void Log(string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}";
        Console.WriteLine(line);
        lock (LogLock)
        {
            try
            {
                Directory.CreateDirectory(Folder);
                if (File.Exists(LogPath) && new FileInfo(LogPath).Length > MaxLogBytes)
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
            catch
            {
                // Logging must never break save/restore.
            }
        }
    }
}

/// <summary>
/// Windows names a notification after the sending process, which is ".NET Host" when run through dotnet.exe.
/// An explicit AppUserModelID registered with a display name and icon makes it show as "LayoutLock" instead.
/// </summary>
internal static class NotificationIdentity
{
    public const string AppId = "LayoutLock";
    private const string RegistryPath = @"Software\Classes\AppUserModelId\" + AppId;

    public static void Apply()
    {
        try
        {
            using (var key = Registry.CurrentUser.CreateSubKey(RegistryPath))
            {
                key.SetValue("DisplayName", "LayoutLock");
                key.SetValue("IconUri", WriteIconFile());
            }
            NativeMethods.SetCurrentProcessExplicitAppUserModelID(AppId);
        }
        catch (Exception ex)
        {
            Storage.Log($"Could not set notification identity: {ex.Message}");
        }
    }

    public static void Remove() => Registry.CurrentUser.DeleteSubKeyTree(RegistryPath, throwOnMissingSubKey: false);

    // IconUri must be a file path, and the single-file release has no loose files next to the exe.
    private static string WriteIconFile()
    {
        var path = Path.Combine(Storage.Folder, "app.ico");
        Directory.CreateDirectory(Storage.Folder);
        using var resource = typeof(NotificationIdentity).Assembly.GetManifestResourceStream("LayoutLock.app.ico")!;
        using var file = File.Create(path);
        resource.CopyTo(file);
        return path;
    }
}

/// <summary>
/// Logon autostart as a scheduled task with "Run with highest privileges": an elevated agent is required to move
/// windows of apps started as administrator, and HKCU\...\Run entries can never start elevated.
/// Creating or deleting such a task needs elevation, so non-elevated callers get a UAC prompt for schtasks.exe.
/// </summary>
internal static class Autostart
{
    private const string TaskName = "LayoutLock";

    // Earlier versions registered the autostart here; removed on Enable/Disable.
    private const string LegacyRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string LegacyValueName = "LayoutLock";

    public static bool IsElevated
    {
        get
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
    }

    public static bool IsEnabled => RunSchtasks($"/query /tn \"{TaskName}\"", elevate: false) == 0;

    public static bool Enable()
    {
        var (fileName, argumentPrefix) = LaunchCommand();
        var xmlPath = Path.Combine(Path.GetTempPath(), $"LayoutLock-task-{Environment.ProcessId}.xml");
        File.WriteAllText(xmlPath, TaskXml(fileName, argumentPrefix + "agent"), System.Text.Encoding.Unicode);
        try
        {
            if (RunSchtasks($"/create /f /tn \"{TaskName}\" /xml \"{xmlPath}\"", elevate: true) != 0)
                return false;
        }
        finally
        {
            File.Delete(xmlPath);
        }
        RemoveLegacyRunEntry();
        return true;
    }

    public static bool Disable()
    {
        RemoveLegacyRunEntry();
        return !IsEnabled || RunSchtasks($"/delete /f /tn \"{TaskName}\"", elevate: true) == 0;
    }

    private static void RemoveLegacyRunEntry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(LegacyRunKey, writable: true);
        key?.DeleteValue(LegacyValueName, throwOnMissingValue: false);
    }

    /// <summary>Returns the schtasks exit code, or -1 if it could not run (e.g. the UAC prompt was declined).</summary>
    private static int RunSchtasks(string arguments, bool elevate)
    {
        var useUac = elevate && !IsElevated;
        var startInfo = new System.Diagnostics.ProcessStartInfo("schtasks.exe", arguments)
        {
            UseShellExecute = useUac,
            Verb = useUac ? "runas" : "",
            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden,
            CreateNoWindow = true,
            RedirectStandardOutput = !useUac,
            RedirectStandardError = !useUac,
        };
        try
        {
            using var process = System.Diagnostics.Process.Start(startInfo)!;
            if (!useUac)
            {
                process.StandardOutput.ReadToEnd();
                process.StandardError.ReadToEnd();
            }
            process.WaitForExit();
            return process.ExitCode;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return -1;
        }
    }

    private static string TaskXml(string command, string arguments)
    {
        static string Escape(string s) => System.Security.SecurityElement.Escape(s);
        var user = Escape(System.Security.Principal.WindowsIdentity.GetCurrent().Name);
        return $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>LayoutLock tray agent: saves window positions on hibernate/sleep and restores them on wake.</Description>
              </RegistrationInfo>
              <Triggers>
                <LogonTrigger>
                  <Enabled>true</Enabled>
                  <UserId>{user}</UserId>
                </LogonTrigger>
              </Triggers>
              <Principals>
                <Principal id="Author">
                  <UserId>{user}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>{Escape(command)}</Command>
                  <Arguments>{Escape(arguments)}</Arguments>
                </Exec>
              </Actions>
            </Task>
            """;
    }

    public static bool IsHostedByDotnet =>
        string.Equals(Path.GetFileName(Environment.ProcessPath), "dotnet.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Corporate Defender ASR rules can block a freshly built, low-prevalence .exe while still allowing
    /// the trusted dotnet.exe host, so relaunch the same way this process was started.
    /// </summary>
    public static (string FileName, string ArgumentPrefix) LaunchCommand() =>
        IsHostedByDotnet
            ? (Environment.ProcessPath!, $"\"{typeof(Autostart).Assembly.Location}\" ")
            : (Environment.ProcessPath!, "");
}
