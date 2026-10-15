# LayoutLock

Saves and restores window positions when you hibernate at one location (e.g. the office) and resume at another
(e.g. home) with a different but equivalent pair of monitors.

Windows itself only remembers window locations per *exact* monitor set (hardware id + port). Two different
pairs of monitors look like two unrelated setups to it, so after resume everything lands on the main display.
This tool fixes that by matching monitors by their **left-to-right order** instead: "the right monitor at the
office" maps to "the right monitor at home". Each window's position is stored relative to its monitor.

## Requirements

- Windows 10/11
- .NET 8 SDK or runtime (`dotnet --list-runtimes` should show `Microsoft.WindowsDesktop.App 8.x`)

## Why everything is run through `dotnet`

The corporate Microsoft Defender policy enforces the Attack Surface Reduction rule
*"Block executable files from running unless they meet a prevalence, age, or trusted list criterion"*
(`01443614-CD74-433A-B99E-2ECDC07BFC25`). A freshly built `LayoutLock.exe` fails that check, so starting it
directly gives:

```
Program 'LayoutLock.exe' failed to run: Access is denied
```

The blocks are visible in Event Viewer → *Applications and Services Logs → Microsoft → Windows → Windows Defender →
Operational*, event id **1121**.

The trusted `dotnet.exe` host is not affected, so all commands below use `dotnet LayoutLock.dll ...`.
If IT adds an exception for the exe, `LayoutLock.exe ...` works identically.

## Build

From this folder:

```
dotnet publish -c Release -o publish
```

The commands below assume:

```
set TOOL=C:\Aleksey-no-sync\IR-2026\2026-10-15-Windows-Position-Tool\publish\LayoutLock.dll
```

(PowerShell: `$TOOL = "C:\Aleksey-no-sync\IR-2026\2026-10-15-Windows-Position-Tool\publish\LayoutLock.dll"`,
then use `$TOOL` instead of `%TOOL%`.)

## Automatic mode (recommended)

Install once:

```
dotnet %TOOL% install
```

This registers a logon autostart (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, value `LayoutLock`)
and starts the tray agent right away. A console window may flash briefly at logon before the agent hides it.

What the agent does:

| When | What happens |
|---|---|
| Windows starts going to **hibernate / sleep** | All window positions are saved. |
| After **resume** | Waits until the saved number of monitors is connected and the display setup has been stable for 5 s, then restores. A second pass runs 10 s later, in case Windows moves windows again on its own. If the monitors don't appear within 10 minutes, it gives up (restore manually). |
| Every **2 minutes** | Backup save, in case a suspend notification is missed. It never overwrites a multi-monitor snapshot while fewer monitors are connected (e.g. undocked laptop). |

### Notifications

Every restore shows a Windows notification from the tray icon, so an unplanned or unexpected restore is visible:

- **"Window positions restored (resume)"** — how many windows were actually out of place and moved back, which
  apps they belong to, and which snapshot was used (time + whether it was taken on suspend, by autosave or manually).
- **"Window positions checked (...)"** — the restore ran but every window was already in place.
- The automatic **second pass** after resume only notifies if it had to move something again (i.e. Windows
  rearranged windows after the first pass).
- Warnings when the restore was skipped (monitors didn't appear within 10 minutes, no snapshot) or windows failed
  to move, and an error notification if the restore crashed.

Click a notification to open `log.txt`, which lists every moved window by app and title. Notifications follow the
Windows *Do not disturb* / notification settings for the app.

Tray icon menu (right-click):

- **Save positions now**
- **Restore positions now** (also: double-click the icon)
- **Start with Windows** — toggles the logon autostart
- **Open data folder**
- **Exit**

Your daily routine doesn't change: hibernate at one place, wake up at the other, wait a few seconds.

### Uninstall

```
dotnet %TOOL% uninstall
```

Then right-click the tray icon → **Exit**. Optionally delete `%LOCALAPPDATA%\LayoutLock`.

## Manual mode

If you prefer not to run the agent:

```
dotnet %TOOL% save       :: before hibernating
dotnet %TOOL% restore    :: after resuming, once both monitors are on
```

`restore` exit codes: `0` success, `1` no snapshot saved yet, `2` some windows could not be moved.

## Quick test

1. Right-click the tray icon → **Save positions now** (or `dotnet %TOOL% save`).
2. Move a few windows to other places / the other monitor, maximize or minimize some.
3. Right-click the tray icon → **Restore positions now** (or `dotnet %TOOL% restore`).
4. Check `%LOCALAPPDATA%\LayoutLock\log.txt`.

## Files

| Path | Content |
|---|---|
| `%LOCALAPPDATA%\LayoutLock\snapshot.json` | Last saved layout: monitors, and per window the process, title, monitor index, position, maximized/minimized state. |
| `%LOCALAPPDATA%\LayoutLock\log.txt` | Every save/restore with counts; rotated to `log.txt.old` at ~1 MB. |

## Limitations

- Windows of applications started **as administrator** can only be moved if the tool also runs elevated; they
  are reported as `failed` in the log.
- Plain **sleep** on some laptops (Modern Standby) may not notify the agent; **hibernate** always does. The
  2-minute backup save covers a missed notification.
- If the two locations have a different number of monitors, windows saved on a monitor that doesn't exist at the
  new location are left where Windows put them.
- **Snapped windows** (Win+Left/Right halves, or stretched to full height by double-clicking the top/bottom edge)
  are restored to exactly the same rectangle, but as a plain positioned window: Windows has no API to re-snap a
  window, so after a restore dragging it away no longer brings back its pre-snap size. Snap it again (Win+Arrow)
  if you need that.
- Windows are matched by their window handle first (hibernate keeps every process running, so it is unchanged),
  then by process name + window class + title.

## Source overview

| File | Purpose |
|---|---|
| `Program.cs` | Command-line entry point (`agent`, `save`, `restore`, `install`, `uninstall`). |
| `TrayAgent.cs` | Tray icon, suspend/resume and display-change handling, autosave. |
| `Snapshot.cs` | Capturing and restoring window placements. |
| `Monitors.cs` | Monitor enumeration ordered left-to-right, coordinate conversion. |
| `Storage.cs` | Data/log paths, logon autostart registration. |
| `NativeMethods.cs` | Win32 P/Invoke declarations. |
| `icon.svg` | Source design of the app icon (snap layout + padlock). |
| `app.ico` | Generated multi-size icon (16–256 px): exe icon and, embedded, the tray icon. |
| `tools/IconGenerator/` | Renders `app.ico` from the same geometry as `icon.svg`. |

### Regenerating the icon

After changing the design, update both `icon.svg` and the drawing code in `tools/IconGenerator/Program.cs`, then:

```
dotnet build tools\IconGenerator -c Release
dotnet tools\IconGenerator\bin\Release\net8.0-windows\IconGenerator.dll app.ico
```

(`dotnet run` would start the generator's freshly built exe, which the Defender rule blocks.)
