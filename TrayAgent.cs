using System.Diagnostics;
using Microsoft.Win32;

namespace LayoutLock;

/// <summary>
/// Background agent: saves the layout when Windows suspends/hibernates and restores it after resume,
/// once the external monitors have come back and the display setup has stopped changing.
/// </summary>
internal sealed class TrayAgent : ApplicationContext
{
    private static readonly TimeSpan SettleDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan SecondPassDelay = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan GiveUpAfter = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan AutosaveInterval = TimeSpan.FromMinutes(2);

    private readonly object _sync = new();
    private readonly NotifyIcon _icon;
    private readonly SynchronizationContext _ui;
    private readonly System.Threading.Timer _restoreTimer;
    private readonly System.Threading.Timer _autosaveTimer;

    private bool _pendingRestore;
    private bool _secondPassDone = true;
    private DateTime _resumedAt;
    private DateTime _lastDisplayChange;
    private DateTime _quietUntil;

    public TrayAgent()
    {
        var autostartItem = new ToolStripMenuItem("Start with Windows") { Checked = Autostart.IsEnabled };
        autostartItem.Click += (_, _) =>
        {
            if (Autostart.IsEnabled) Autostart.Disable(); else Autostart.Enable();
            autostartItem.Checked = Autostart.IsEnabled;
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add("Save positions now", null, (_, _) => SaveNow("manual"));
        menu.Items.Add("Restore positions now", null, (_, _) => RestoreNow("manual"));
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
        _icon.DoubleClick += (_, _) => RestoreNow("manual");
        _icon.BalloonTipClicked += (_, _) => OpenLog();

        // Restores run on timer threads; the tray icon belongs to the UI thread.
        _ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();

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
                    SaveNow("suspend");
                    _pendingRestore = true;
                }
                break;

            case PowerModes.Resume:
                lock (_sync)
                {
                    Storage.Log("Resumed; waiting for displays to settle.");
                    _pendingRestore = true;
                    _resumedAt = DateTime.Now;
                    _lastDisplayChange = DateTime.Now;
                    _restoreTimer.Change(SettleDelay, Timeout.InfiniteTimeSpan);
                }
                break;
        }
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        lock (_sync)
        {
            _lastDisplayChange = DateTime.Now;
            if (_pendingRestore)
                _restoreTimer.Change(SettleDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnRestoreTimer()
    {
        lock (_sync)
        {
            if (!_secondPassDone)
            {
                // Windows' own "remember window locations" logic sometimes moves windows again right after us.
                _secondPassDone = true;
                RestoreNow("resume, second pass", notifyOnlyIfMoved: true);
                return;
            }

            if (!_pendingRestore)
                return;

            var sinceChange = DateTime.Now - _lastDisplayChange;
            if (sinceChange < SettleDelay)
            {
                _restoreTimer.Change(SettleDelay - sinceChange, Timeout.InfiniteTimeSpan);
                return;
            }

            var snapshot = Snapshot.Load(Storage.SnapshotPath);
            var monitorCount = Monitors.GetOrdered().Count;
            if (snapshot is null)
            {
                _pendingRestore = false;
                return;
            }

            if (monitorCount < snapshot.MonitorCount)
            {
                if (DateTime.Now - _resumedAt > GiveUpAfter)
                {
                    Storage.Log($"Gave up waiting for {snapshot.MonitorCount} monitors (have {monitorCount}). Use 'Restore positions now' manually.");
                    Notify("Window positions not restored",
                        $"Waited 10 minutes for {snapshot.MonitorCount} monitors, only {monitorCount} connected. " +
                        "Use 'Restore positions now' from the tray menu.", ToolTipIcon.Warning);
                    _pendingRestore = false;
                }
                else
                {
                    _restoreTimer.Change(TimeSpan.FromSeconds(15), Timeout.InfiniteTimeSpan);
                }
                return;
            }

            _pendingRestore = false;
            RestoreNow("resume");
            _secondPassDone = false;
            _restoreTimer.Change(SecondPassDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void OnAutosave()
    {
        lock (_sync)
        {
            if (_pendingRestore || !_secondPassDone || DateTime.Now < _quietUntil)
                return;

            // Never let a laptop-only layout (e.g. while undocked) overwrite a full multi-monitor snapshot.
            var previous = Snapshot.Load(Storage.SnapshotPath);
            if (previous is not null && Monitors.GetOrdered().Count < previous.MonitorCount)
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
                snapshot.Save(Storage.SnapshotPath);
                if (!quiet)
                    Storage.Log($"Saved {snapshot.Windows.Count} window(s) on {snapshot.MonitorCount} monitor(s) [{reason}].");
            }
            catch (Exception ex)
            {
                Storage.Log($"Save failed [{reason}]: {ex}");
            }
        }
    }

    private void RestoreNow(string reason, bool notifyOnlyIfMoved = false)
    {
        lock (_sync)
        {
            try
            {
                var snapshot = Snapshot.Load(Storage.SnapshotPath);
                if (snapshot is null)
                {
                    Storage.Log("Nothing to restore: no snapshot saved yet.");
                    Notify("Window positions not restored", "No snapshot saved yet.", ToolTipIcon.Warning);
                    return;
                }

                var result = WindowRestore.Restore(snapshot, Storage.Log);
                Storage.Log($"{result.Summary} from snapshot of {snapshot.SavedAt:g} [{reason}].");
                _quietUntil = DateTime.Now + TimeSpan.FromMinutes(1);

                if (!notifyOnlyIfMoved || result.MovedApps.Count > 0)
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
        var title = result.MovedApps.Count == 0
            ? $"Window positions checked ({reason})"
            : $"Window positions restored ({reason})";

        var apps = result.MovedApps
            .GroupBy(name => name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)
            .ToList();
        var appsLine = apps.Count == 0
            ? "Everything was already in place."
            : string.Join(", ", apps.Take(6)) + (apps.Count > 6 ? $" +{apps.Count - 6} more" : "");

        var text = $"Moved {result.MovedApps.Count} of {result.Restored} window(s)\n" +
                   $"Skipped {result.Skipped}, failed {result.Failed}\n" +
                   $"{appsLine}\nSnapshot: {snapshot.SavedAt:g} ({snapshot.Reason})";
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
