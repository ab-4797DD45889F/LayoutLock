using System.Diagnostics;
using System.Text.Json;
using static WindowPositionTool.NativeMethods;

namespace WindowPositionTool;

internal sealed class WindowRecord
{
    public long Hwnd { get; set; }
    public uint ProcessId { get; set; }
    public string ProcessName { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Title { get; set; } = "";
    public int ShowCmd { get; set; }
    public int MonitorIndex { get; set; }

    // Normal (restored) rectangle relative to the top-left corner of its monitor.
    public int OffsetX { get; set; }
    public int OffsetY { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    // Snapped (Win+Left/Right, vertically maximized) windows report SW_SHOWNORMAL with their pre-snap rectangle,
    // so their actual on-screen rectangle is stored separately, relative to the top-left corner of its monitor.
    public bool Snapped { get; set; }
    public int SnappedX { get; set; }
    public int SnappedY { get; set; }
    public int SnappedWidth { get; set; }
    public int SnappedHeight { get; set; }
}

internal sealed class Snapshot
{
    public DateTime SavedAt { get; set; }
    public string Reason { get; set; } = "";
    public int MonitorCount { get; set; }
    public List<string> MonitorLayout { get; set; } = [];
    public List<WindowRecord> Windows { get; set; } = [];

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public void Save(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temp, path, overwrite: true);
    }

    public static Snapshot? Load(string path) =>
        File.Exists(path) ? JsonSerializer.Deserialize<Snapshot>(File.ReadAllText(path)) : null;
}

internal static class WindowCapture
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow",
    };

    public static Snapshot Capture(string reason)
    {
        var monitors = Monitors.GetOrdered();
        var (dx, dy) = Monitors.WorkspaceToScreenOffset(monitors);
        var ownPid = (uint)Environment.ProcessId;
        var snapshot = new Snapshot
        {
            SavedAt = DateTime.Now,
            Reason = reason,
            MonitorCount = monitors.Count,
            MonitorLayout = monitors.Select(m => $"{m.Device} {m.Bounds}{(m.IsPrimary ? " primary" : "")}").ToList(),
        };

        EnumWindows((hWnd, _) =>
        {
            if (!IsCandidate(hWnd))
                return true;

            GetWindowThreadProcessId(hWnd, out var pid);
            if (pid == ownPid)
                return true;

            var placement = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            if (!GetWindowPlacement(hWnd, ref placement))
                return true;

            var normal = placement.rcNormalPosition;
            normal.Left += dx; normal.Right += dx;
            normal.Top += dy; normal.Bottom += dy;

            var actual = default(RECT);
            var snapped = placement.showCmd != SW_SHOWMAXIMIZED
                && !WindowRestore.IsMinimized(placement.showCmd)
                && GetWindowRect(hWnd, out actual)
                && !SameRect(actual, normal);

            // A minimized window sits at -32000, so its monitor is where it will reappear; a maximized or snapped one is where it is now.
            var hMonitor = placement.showCmd == SW_SHOWMAXIMIZED || snapped
                ? MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST)
                : MonitorFromRect(ref normal, MONITOR_DEFAULTTONEAREST);
            var index = Monitors.IndexOf(monitors, hMonitor);
            if (index < 0)
                return true;
            var monitor = monitors[index];

            var record = new WindowRecord
            {
                Hwnd = hWnd.ToInt64(),
                ProcessId = pid,
                ProcessName = GetProcessName(pid),
                ClassName = GetClassName(hWnd),
                Title = GetWindowText(hWnd),
                ShowCmd = placement.showCmd,
                MonitorIndex = index,
                OffsetX = normal.Left - monitor.Bounds.Left,
                OffsetY = normal.Top - monitor.Bounds.Top,
                Width = normal.Width,
                Height = normal.Height,
            };
            if (snapped)
            {
                record.Snapped = true;
                record.SnappedX = actual.Left - monitor.Bounds.Left;
                record.SnappedY = actual.Top - monitor.Bounds.Top;
                record.SnappedWidth = actual.Width;
                record.SnappedHeight = actual.Height;
            }
            snapshot.Windows.Add(record);
            return true;
        }, IntPtr.Zero);

        return snapshot;
    }

    public static bool SameRect(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    public static bool IsCandidate(IntPtr hWnd)
    {
        if (!IsWindowVisible(hWnd) || GetWindow(hWnd, GW_OWNER) != IntPtr.Zero)
            return false;
        var exStyle = GetWindowLongPtr(hWnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOOLWINDOW) != 0 && (exStyle & WS_EX_APPWINDOW) == 0)
            return false;
        if (GetWindowTextLength(hWnd) == 0)
            return false;
        return !IgnoredClasses.Contains(GetClassName(hWnd));
    }

    public static string GetProcessName(uint pid)
    {
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch
        {
            return "";
        }
    }
}

internal static class WindowRestore
{
    /// <param name="Restored">Windows placed successfully, including those that were already in place.</param>
    /// <param name="MovedApps">Process names of the windows that were actually out of place before the restore.</param>
    public sealed record Result(int Restored, IReadOnlyList<string> MovedApps, int Skipped, int Failed)
    {
        public string Summary =>
            $"Moved {MovedApps.Count} of {Restored} window(s), skipped {Skipped}, failed {Failed}";
    }

    public static Result Restore(Snapshot snapshot, Action<string> log)
    {
        var monitors = Monitors.GetOrdered();
        if (monitors.Count != snapshot.MonitorCount)
            log($"Warning: snapshot has {snapshot.MonitorCount} monitor(s), current setup has {monitors.Count}. " +
                "Windows on missing monitors are left where they are.");

        var (dx, dy) = Monitors.WorkspaceToScreenOffset(monitors);
        var used = new HashSet<IntPtr>();
        int restored = 0, skipped = 0, failed = 0;
        var movedApps = new List<string>();

        foreach (var record in snapshot.Windows)
        {
            if (record.MonitorIndex >= monitors.Count)
            {
                skipped++;
                continue;
            }

            var hWnd = FindWindow(record, used);
            if (hWnd == IntPtr.Zero)
            {
                skipped++;
                continue;
            }
            used.Add(hWnd);

            var monitor = monitors[record.MonitorIndex];
            var target = record.Snapped ? SnappedRect(record, monitor) : FitIntoWorkArea(record, monitor);

            var current = new WINDOWPLACEMENT { length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>() };
            GetWindowPlacement(hWnd, ref current);

            var stateChanged = IsMinimized(current.showCmd) != IsMinimized(record.ShowCmd)
                || (current.showCmd == SW_SHOWMAXIMIZED) != (record.ShowCmd == SW_SHOWMAXIMIZED)
                || (record.ShowCmd == SW_SHOWMAXIMIZED && MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST) != monitor.Handle);
            var wasMisplaced = stateChanged || (record.Snapped
                ? !GetWindowRect(hWnd, out var actual) || !WindowCapture.SameRect(actual, target)
                : current.rcNormalPosition.Left != target.Left - dx
                  || current.rcNormalPosition.Top != target.Top - dy
                  || current.rcNormalPosition.Width != target.Width
                  || current.rcNormalPosition.Height != target.Height);

            var placement = new WINDOWPLACEMENT
            {
                length = System.Runtime.InteropServices.Marshal.SizeOf<WINDOWPLACEMENT>(),
                // Async so a hung application cannot block the whole restore.
                flags = WPF_ASYNCWINDOWPLACEMENT,
                showCmd = record.ShowCmd switch
                {
                    SW_SHOWMAXIMIZED => SW_SHOWMAXIMIZED,
                    SW_SHOWMINIMIZED => SW_SHOWMINNOACTIVE,
                    _ => SW_SHOWNOACTIVATE,
                },
                ptMinPosition = current.ptMinPosition,
                ptMaxPosition = current.ptMaxPosition,
                rcNormalPosition = new RECT
                {
                    Left = target.Left - dx,
                    Top = target.Top - dy,
                    Right = target.Right - dx,
                    Bottom = target.Bottom - dy,
                },
            };

            // A maximized window must be un-maximized first, otherwise it stays maximized on the old monitor.
            if (current.showCmd == SW_SHOWMAXIMIZED && record.ShowCmd == SW_SHOWMAXIMIZED
                && MonitorFromWindow(hWnd, MONITOR_DEFAULTTONEAREST) != monitor.Handle)
            {
                var intermediate = placement with { showCmd = SW_SHOWNOACTIVATE, flags = 0 };
                SetWindowPlacement(hWnd, ref intermediate);
            }

            if (SetWindowPlacement(hWnd, ref placement))
            {
                // There is no API to re-snap a window, so a snapped one gets its snapped rectangle as a plain position;
                // SetWindowPos also covers a window Windows still considers snapped from before the restore.
                if (record.Snapped)
                    SetWindowPos(hWnd, IntPtr.Zero, target.Left, target.Top, target.Width, target.Height,
                        SWP_NOZORDER | SWP_NOACTIVATE | SWP_ASYNCWINDOWPOS);

                restored++;
                if (wasMisplaced)
                {
                    movedApps.Add(record.ProcessName);
                    log($"  moved: {record.ProcessName} \"{record.Title}\" -> monitor {record.MonitorIndex + 1}");
                }
            }
            else
            {
                failed++;
                log($"  failed: {record.ProcessName} \"{record.Title}\" (error {System.Runtime.InteropServices.Marshal.GetLastWin32Error()}, " +
                    "elevated windows need the tool to run as administrator)");
            }
        }

        return new Result(restored, movedApps, skipped, failed);
    }

    public static bool IsMinimized(int showCmd) => showCmd is SW_SHOWMINIMIZED or SW_SHOWMINNOACTIVE;

    // Not clamped to the work area: a snapped window's rectangle includes the invisible resize borders,
    // which deliberately extend a few pixels beyond it.
    private static RECT SnappedRect(WindowRecord record, MonitorDescriptor monitor)
    {
        var left = monitor.Bounds.Left + record.SnappedX;
        var top = monitor.Bounds.Top + record.SnappedY;
        return new RECT { Left = left, Top = top, Right = left + record.SnappedWidth, Bottom = top + record.SnappedHeight };
    }

    private static RECT FitIntoWorkArea(WindowRecord record, MonitorDescriptor monitor)
    {
        var work = monitor.WorkArea;
        var width = Math.Min(record.Width, work.Width);
        var height = Math.Min(record.Height, work.Height);
        var left = Math.Clamp(monitor.Bounds.Left + record.OffsetX, work.Left, work.Right - width);
        var top = Math.Clamp(monitor.Bounds.Top + record.OffsetY, work.Top, work.Bottom - height);
        return new RECT { Left = left, Top = top, Right = left + width, Bottom = top + height };
    }

    /// <summary>
    /// Hibernate keeps every process alive, so the saved handle is normally still the same window.
    /// Fall back to process + class + title for snapshots taken before an application was restarted.
    /// </summary>
    private static IntPtr FindWindow(WindowRecord record, HashSet<IntPtr> used)
    {
        var saved = new IntPtr(record.Hwnd);
        if (!used.Contains(saved) && IsWindow(saved))
        {
            GetWindowThreadProcessId(saved, out var pid);
            if (pid == record.ProcessId && GetClassName(saved) == record.ClassName)
                return saved;
        }

        IntPtr byTitle = IntPtr.Zero, byClass = IntPtr.Zero;
        EnumWindows((hWnd, _) =>
        {
            if (used.Contains(hWnd) || !WindowCapture.IsCandidate(hWnd) || GetClassName(hWnd) != record.ClassName)
                return true;
            GetWindowThreadProcessId(hWnd, out var pid);
            if (!string.Equals(WindowCapture.GetProcessName(pid), record.ProcessName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (GetWindowText(hWnd) == record.Title)
            {
                byTitle = hWnd;
                return false;
            }
            if (byClass == IntPtr.Zero)
                byClass = hWnd;
            return true;
        }, IntPtr.Zero);

        return byTitle != IntPtr.Zero ? byTitle : byClass;
    }
}
