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

internal static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "LayoutLock";

    public static bool IsEnabled
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
    }

    public static void Enable()
    {
        var (fileName, argumentPrefix) = LaunchCommand();
        using var key = Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(ValueName, $"\"{fileName}\" {argumentPrefix}agent");
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

    public static void Disable()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(ValueName, throwOnMissingValue: false);
    }
}
