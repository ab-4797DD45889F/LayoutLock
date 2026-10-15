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
