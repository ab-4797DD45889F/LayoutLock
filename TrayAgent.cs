using System.Diagnostics;
using Microsoft.Win32;

namespace LayoutLock;

/// <summary>
/// Background agent: saves the layout when Windows suspends/hibernates and restores it after resume, and restores
/// the layout saved for the new number of monitors whenever monitors are connected or disconnected.
/// Restores wait until the display setup has stopped changing.
/// </summary>
internal sealed class TrayAgent : ApplicationContext
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SecondPassDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromMinutes(2);

    private readonly object _sync = new();
    private readonly NotifyIcon _icon;
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _restoreTimer;
    private readonly System.Threading.Timer _autosaveTimer;

    // A display change or resume is waiting for the setup to settle.
    private bool _pendingCheck;
    // The pending check must restore even if the number of monitors did not change.
    private bool _resumed;
    private bool _secondPassDone = true;
    private string _secondPassReason = "";
    private DateTime _lastDisplayChange;
    private DateTime _quietUntil;

    // Number of monitors whose layout is currently on screen; autosave writes only to that layout's snapshot.
    private int _activeMonitorCount;

    public TrayAgent()
    {
        var autostartItem = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsEnabled };
        autostartItem.Click += (_, _) =>
        {
            if (Autostart.IsEnabled) Autostart.Disable(); else Autostart.Enable();
            autostartItem.Checked = Autostart.IsEnabled;
        };

        var menu = new ContextMenuStrip();
        // Off the UI thread: both read the taskbar through UI Automation, and a restore waits for Explorer.
        menu.Items.Add("Save positions now", null, (_, _) => Task.Run(() => SaveNow("manual")));
        menu.Items.Add("Restore positions now", null, (_, _) => Task.Run(() => RestoreNow("manual")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(autostartItem);
        menu.Items.Add("Open data folder", null, (_, _) => OpenFolder());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "LayoutLock",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => Task.Run(() => RestoreNow("manual"));
        _icon.BalloonTipClicked += (_, _) => OpenLog();

        // Restores run on timer threads; the tray icon belongs to the UI thread.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

        _activeMonitorCount = Monitors.GetOrdered().Count;
        _restoreTimer = new System.Threading.Timer(_ => OnRestoreTimer());
        _autosaveTimer = new System.Threading.Timer(_ => OnAutosave(), null, AutosaveInterval, AutosaveInterval);

        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        Storage.Log(Autostart.IsElevated ? "Agent started (as administrator)." : "Agent started (not elevated: windows of apps run as administrator can't be moved).");
    }

    private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
    {
        switch (e.Mode)
        {
            case PowerModes.Suspend:
                // Runs synchronously inside the suspend broadcast, so the file is written before the machine goes down.
                lock (_sync)
                {
                    if (CanSave(out var why))
                        SaveNow("suspend");
                    else
                        Storage.Log($"Not saved on suspend: {why}.");
                }
                break;

            case PowerModes.Resume:
                lock (_sync)
                {
                    Storage.Log("Resumed; waiting for displays to settle.");
                    _resumed = true;
                    ScheduleCheck();
                }
                break;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        lock (_sync)
            ScheduleCheck();
    }

    private void ScheduleCheck()
    {
        _pendingCheck = true;
        _lastDisplayChange = DateTime.Now;
        _restoreTimer.Change(SettleDelay, Timeout.InfiniteTimeSpan);
    }

    private void OnRestoreTimer()
    {
        lock (_sync)
        {
            if (_pendingCheck)
            {
                var sinceChange = DateTime.Now - _lastDisplayChange;
                if (sinceChange < SettleDelay)
                {
                    _restoreTimer.Change(SettleDelay - sinceChange, Timeout.InfiniteTimeSpan);
                    return;
                }

                var count = Monitors.GetOrdered().Count;
                var reason = _resumed ? "resume"
                    : count != _activeMonitorCount ? $"{MonitorsText(_activeMonitorCount)} → {MonitorsText(count)}"
                    : null;
                _pendingCheck = false;
                _resumed = false;
                _activeMonitorCount = count;

                if (reason is not null)
                {
                    if (reason != "resume")
                        Storage.Log($"Display setup changed: {reason}.");
                    RestoreNow(reason, missingSnapshotIsNormal: true);
                    // Windows' own "remember window locations" logic sometimes moves windows again right after us.
                    _secondPassDone = false;
                    _secondPassReason = reason;
                    _restoreTimer.Change(SecondPassDelay, Timeout.InfiniteTimeSpan);
                    return;
                }
                // A change that kept the number of monitors (resolution, scaling): nothing to restore,
                // but a second pass interrupted by it still has to run.
            }

            if (!_secondPassDone)
            {
                _secondPassDone = true;
                RestoreNow($"{_secondPassReason}, second pass", notifyOnlyIfChanged: true, missingSnapshotIsNormal: true);
            }
        }
    }

    private static string MonitorsText(int count) => count == 1 ? "1 monitor" : $"{count} monitors";

    /// <summary>
    /// Saving is only safe while the windows show the layout of the current monitor setup: right after monitors were
    /// (dis)connected, Windows has piled them up and the restore for the new setup has not run yet.
    /// </summary>
    private bool CanSave(out string why)
    {
        var count = Monitors.GetOrdered().Count;
        why = count != _activeMonitorCount
            ? $"monitors changed ({MonitorsText(_activeMonitorCount)} → {MonitorsText(count)}) and the restore has not run yet"
            : "";
        return count == _activeMonitorCount;
    }

    private void OnAutosave()
    {
        lock (_sync)
        {
            if (_pendingCheck || !_secondPassDone || DateTime.Now < _quietUntil || !CanSave(out _))
                return;

            SaveNow("autosave", quiet: true);
        }
    }

    private void SaveNow(string reason, bool quiet = false)
    {
        lock (_sync)
        {
            try
            {
                var snapshot = WindowCapture.Capture(reason);
                snapshot.Save();
                if (!quiet)
                    Storage.Log($"Saved {snapshot.Windows.Count} window(s) on {snapshot.MonitorCount} monitor(s) [{reason}].");
            }
            catch (Exception ex)
            {
                Storage.Log($"Save failed [{reason}]: {ex}");
            }
        }
    }

    /// <param name="missingSnapshotIsNormal">
    /// True for automatic restores: the first time a monitor setup is seen there is nothing saved for it yet,
    /// so autosave starts recording it instead of warning.
    /// </param>
    private void RestoreNow(string reason, bool notifyOnlyIfChanged = false, bool missingSnapshotIsNormal = false)
    {
        lock (_sync)
        {
            try
            {
                var count = Monitors.GetOrdered().Count;
                var snapshot = Snapshot.Load(Storage.SnapshotPath(count));
                if (snapshot is null)
                {
                    Storage.Log($"Nothing to restore [{reason}]: no snapshot saved yet for {MonitorsText(count)}.");
                    if (!missingSnapshotIsNormal)
                        Notify("Window positions not restored", $"No snapshot saved yet for {MonitorsText(count)}.", ToolTipIcon.Warning);
                    return;
                }

                var result = WindowRestore.Restore(snapshot, Storage.Log);
                Storage.Log($"{result.Summary} from {MonitorsText(snapshot.MonitorCount)} snapshot of {snapshot.SavedAt:g} [{reason}].");
                _quietUntil = DateTime.Now + TimeSpan.FromMinutes(1);

                if (!notifyOnlyIfChanged || result.ChangedAnything)
                    NotifyRestore(reason, snapshot, result);
            }
            catch (Exception ex)
            {
                Storage.Log($"Restore failed [{reason}]: {ex}");
                Notify("Window restore failed", ex.Message, ToolTipIcon.Error);
            }
        }
    }

    private void NotifyRestore(string reason, Snapshot snapshot, WindowRestore.Result result)
    {
        var title = result.ChangedAnything
            ? $"Window positions restored ({reason})"
            : $"Window positions checked ({reason})";

        var apps = result.MovedApps
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)
            .ToList();
        var appsLine = apps.Count == 0
            ? result.ChangedAnything ? "" : "Everything was already in place.\n"
            : string.Join(", ", apps.Take(6)) + (apps.Count > 6 ? $" +{apps.Count - 6} more" : "") + "\n";
        var taskbarLine = result.TaskbarGroupsReordered > 0
            ? $"Taskbar order fixed for {result.TaskbarGroupsReordered} app(s)\n"
            : "";

        var text = $"Moved {result.MovedApps.Count} of {result.Restored} window(s)\n" +
                   $"Skipped {result.Skipped}, failed {result.Failed}\n" +
                   $"{appsLine}{taskbarLine}Snapshot: {MonitorsText(snapshot.MonitorCount)}, {snapshot.SavedAt:g} ({snapshot.Reason})";
        Notify(title, text, result.Failed > 0 ? ToolTipIcon.Warning : ToolTipIcon.Info);
    }

    private void Notify(string title, string text, ToolTipIcon icon)
    {
        // Balloon text is truncated by Windows at ~255 characters.
        if (text.Length > 250)
            text = text[..247] + "...";
        _ui.Post(_ => _icon.ShowBalloonTip(10_000, title, text, icon), null);
    }

    private static Icon LoadAppIcon()
    {
        using var stream = typeof(TrayAgent).Assembly.GetManifestResourceStream("LayoutLock.app.ico");
        // Ask for the tray's own size (16/20/24/32 px depending on DPI) so Windows doesn't downscale a big frame.
        return stream is null ? SystemIcons.Application : new Icon(stream, SystemInformation.SmallIconSize);
    }

    private static void OpenLog()
    {
        if (File.Exists(Storage.LogPath))
            Process.Start(new ProcessStartInfo(Storage.LogPath) { UseShellExecute = true });
    }

    private static void OpenFolder()
    {
        Directory.CreateDirectory(Storage.Folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{Storage.Folder}\"") { UseShellExecute = true });
    }

    protected override void ExitThreadCore()
    {
        SystemEvents.PowerModeChanged -= OnPowerModeChanged;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _restoreTimer.Dispose();
        _autosaveTimer.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        Storage.Log("Agent stopped.");
        base.ExitThreadCore();
    }
}
