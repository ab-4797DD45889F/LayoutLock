using static WindowPositionTool.NativeMethods;

namespace WindowPositionTool;

internal sealed record MonitorDescriptor(IntPtr Handle, string Device, RECT Bounds, RECT WorkArea, bool IsPrimary);

/// <summary>
/// Monitors are identified by their left-to-right order, not by device name or hardware id:
/// the office and home monitors are different devices, but "left" and "right" mean the same thing in both places.
/// </summary>
internal static class Monitors
{
    public static List<MonitorDescriptor> GetOrdered()
    {
        var result = new List<MonitorDescriptor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref info))
                result.Add(new MonitorDescriptor(hMonitor, info.szDevice, info.rcMonitor, info.rcWork,
                    (info.dwFlags & MONITORINFOF_PRIMARY) != 0));
            return true;
        }, IntPtr.Zero);

        return result
            .OrderBy(m => m.Bounds.Left)
            .ThenBy(m => m.Bounds.Top)
            .ToList();
    }

    public static int IndexOf(List<MonitorDescriptor> monitors, IntPtr hMonitor) =>
        monitors.FindIndex(m => m.Handle == hMonitor);

    /// <summary>
    /// WINDOWPLACEMENT rectangles are in workspace coordinates (relative to the primary monitor's work area),
    /// which differ from screen coordinates whenever the taskbar is docked at the top or left.
    /// </summary>
    public static (int dx, int dy) WorkspaceToScreenOffset(List<MonitorDescriptor> monitors)
    {
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
        return (primary.WorkArea.Left - primary.Bounds.Left, primary.WorkArea.Top - primary.Bounds.Top);
    }
}
