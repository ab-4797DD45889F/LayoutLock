using System.Runtime.InteropServices;
using System.Windows.Automation;
using static LayoutLock.NativeMethods;

namespace LayoutLock;

/// <summary>
/// Saves and restores the order of window buttons on the taskbar(s).
///
/// Windows has no API for the order. It is read through UI Automation: every uncombined taskbar button exposes
/// AutomationId "Window: 0x&lt;hwnd&gt;" and its on-screen position. It is changed with ITaskbarList: DeleteTab
/// followed by AddTab moves a window's button to the end of its app group, so re-adding a group's windows one by
/// one in the saved order rebuilds that order. Explorer keeps the position of app groups themselves (pinned apps
/// stay in their pinned slot), so only the order of windows inside a group is restored.
///
/// Moving a window to another monitor ("show taskbar buttons on the taskbar where the window is open") re-creates
/// its button at the end of its group, which is why the order looks random after windows were moved around.
/// </summary>
internal static class Taskbar
{
    private const string WindowIdPrefix = "Window: 0x";
    private static readonly TimeSpan UiaTimeout = TimeSpan.FromSeconds(5);

    // Explorer processes the tab messages asynchronously; without a pause it may coalesce delete+add of the same window.
    private const int TabCallDelayMs = 60;

    /// <summary>Window handles in on-screen order (taskbars left to right, buttons left to right); null if unavailable.</summary>
    public static List<long>? CaptureOrder()
    {
        var buttons = CaptureButtons();
        return buttons?.SelectMany(taskbar => taskbar).Select(h => h.ToInt64()).ToList();
    }

    /// <summary>
    /// Re-adds buttons whose order inside their app group differs from <paramref name="savedOrder"/>.
    /// Returns the number of app groups that were reordered.
    /// </summary>
    public static int RestoreOrder(IReadOnlyList<long> savedOrder, Action<string> log)
    {
        if (savedOrder.Count == 0)
            return 0;
        var taskbars = CaptureButtons();
        if (taskbars is null)
        {
            log("  taskbar: could not read the button order, skipped.");
            return 0;
        }

        var rank = new Dictionary<long, int>();
        for (var i = 0; i < savedOrder.Count; i++)
            rank.TryAdd(savedOrder[i], i);

        var toReAdd = new List<IntPtr>();
        var groups = new List<string>();
        foreach (var buttons in taskbars)
        {
            foreach (var group in ContiguousGroups(buttons))
            {
                if (group.Windows.Count < 2)
                    continue;
                // Windows unknown to the snapshot (opened later) keep their relative order at the end of the group.
                var desired = group.Windows
                    .Select((hWnd, index) => (hWnd, index))
                    .OrderBy(w => rank.TryGetValue(w.hWnd.ToInt64(), out var r) ? r : int.MaxValue)
                    .ThenBy(w => w.index)
                    .Select(w => w.hWnd)
                    .ToList();
                if (desired.SequenceEqual(group.Windows))
                    continue;
                toReAdd.AddRange(desired);
                groups.Add(group.ProcessName);
            }
        }

        if (toReAdd.Count == 0)
            return 0;

        var taskbarList = (ITaskbarList)new TaskbarListCoClass();
        try
        {
            taskbarList.HrInit();
            foreach (var hWnd in toReAdd)
            {
                taskbarList.DeleteTab(hWnd);
                Thread.Sleep(TabCallDelayMs);
                taskbarList.AddTab(hWnd);
                Thread.Sleep(TabCallDelayMs);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(taskbarList);
        }

        foreach (var name in groups)
            log($"  taskbar: reordered {name} buttons");
        return groups.Count;
    }

    private sealed record Group(string ProcessName, List<IntPtr> Windows);

    /// <summary>Buttons of one app sit next to each other; a run of buttons of the same process is one group.</summary>
    private static IEnumerable<Group> ContiguousGroups(List<IntPtr> buttons)
    {
        Group? current = null;
        foreach (var hWnd in buttons)
        {
            GetWindowThreadProcessId(hWnd, out var pid);
            var name = WindowCapture.GetProcessName(pid);
            if (current is null || !string.Equals(current.ProcessName, name, StringComparison.OrdinalIgnoreCase))
            {
                if (current is not null)
                    yield return current;
                current = new Group(name, []);
            }
            current.Windows.Add(hWnd);
        }
        if (current is not null)
            yield return current;
    }

    /// <summary>Per taskbar (left to right) the window handles of its buttons (left to right).</summary>
    private static List<List<IntPtr>>? CaptureButtons()
    {
        // UI Automation talks to Explorer; never let a hung Explorer block a save during suspend.
        var task = Task.Run(CaptureButtonsCore);
        try
        {
            return task.Wait(UiaTimeout) ? task.Result : null;
        }
        catch (Exception ex)
        {
            Storage.Log($"Could not read the taskbar order: {ex.GetBaseException().Message}");
            return null;
        }
    }

    private static List<List<IntPtr>> CaptureButtonsCore()
    {
        var trayCondition = new OrCondition(
            new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_TrayWnd"),
            new PropertyCondition(AutomationElement.ClassNameProperty, "Shell_SecondaryTrayWnd"));
        var taskbars = AutomationElement.RootElement.FindAll(TreeScope.Children, trayCondition)
            .Cast<AutomationElement>()
            .OrderBy(t => t.Current.BoundingRectangle.Left)
            .ThenBy(t => t.Current.BoundingRectangle.Top);

        var result = new List<List<IntPtr>>();
        foreach (var taskbar in taskbars)
        {
            var buttons = new List<(IntPtr hWnd, double x, double y)>();
            foreach (AutomationElement element in taskbar.FindAll(TreeScope.Descendants, Condition.TrueCondition))
            {
                var id = element.Current.AutomationId;
                if (!id.StartsWith(WindowIdPrefix, StringComparison.Ordinal)
                    || !long.TryParse(id.AsSpan(WindowIdPrefix.Length), System.Globalization.NumberStyles.HexNumber, null, out var handle))
                    continue;
                var rect = element.Current.BoundingRectangle;
                buttons.Add((new IntPtr(handle), rect.Left, rect.Top));
            }
            // Vertical taskbars (third-party tools) are ordered top to bottom.
            result.Add(buttons.OrderBy(b => b.x).ThenBy(b => b.y).Select(b => b.hWnd).ToList());
        }
        return result;
    }

    [ComImport, Guid("56FDF342-FD6D-11d0-958A-006097C9A090"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ITaskbarList
    {
        void HrInit();
        void AddTab(IntPtr hWnd);
        void DeleteTab(IntPtr hWnd);
        void ActivateTab(IntPtr hWnd);
        void SetActiveAlt(IntPtr hWnd);
    }

    [ComImport, Guid("56FDF344-FD6D-11d0-958A-006097C9A090")]
    private class TaskbarListCoClass;
}
