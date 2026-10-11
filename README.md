# DisplayWeave

Windows virtual-monitor compatibility preview with automatic region detection, custom
screen regions, and optional per-application rules.

For development or AI-agent continuation, start with [AGENTS.md](AGENTS.md) and the
[development runbook](docs/development.md).

## Install or update

Windows 10/11 x64 is required.

### With the setup (recommended)

Download `DisplayWeave-Setup-<version>.exe` from the
[private releases page](https://github.com/alexchaoss/DisplayWeave/releases) and run
it. It installs or updates DisplayWeave for your Windows account (no administrator
rights), keeps your regions and app rules, can turn on **Start DisplayWeave when I
sign in**, and adds DisplayWeave to **Settings → Apps → Installed apps**. A running
DisplayWeave is closed and reopened. The setup verifies its embedded package's
SHA-256 checksum before installing.

The setup is not code-signed, so Windows SmartScreen may show "Windows protected
your PC". Choose **More info → Run anyway** only for a file from this releases page;
its SHA-256 is in the matching `.exe.sha256` file.

To uninstall, use **Settings → Apps → Installed apps → DisplayWeave → Uninstall**.
It closes DisplayWeave and removes its program files, Start menu shortcut and startup
entry. Your regions and app rules are kept unless you tick **Also delete my regions
and app rules**. Files that another running app still has loaded are removed at your
next sign-in. If you used **Restart as administrator** and a helper copy was still in
use when DisplayWeave exited, delete `%ProgramFiles%\DisplayWeave` as administrator.

For scripted use: `--quiet`, `--no-launch`, `--startup Enable|Disable|Preserve`,
`--uninstall`, `--remove-settings` and `--log <file>`.

### Automatic updates

From 0.5.5, an installed DisplayWeave checks the releases page a minute after it
starts and then every six hours. Because the repository is private, it does this
through the [GitHub CLI](https://cli.github.com/): install it and run `gh auth login`
once with an account that can access this repository. DisplayWeave never reads or
stores your GitHub credentials. Without a signed-in GitHub CLI, the check is skipped
and **Status & help** says why.

A newer release's setup is downloaded and checked against its SHA-256 file. It then
installs by itself as soon as the DisplayWeave window is closed and no app is
maximized or fullscreen in a region, so a running game is never resized by the
restart. Until then the tray menu offers **Install DisplayWeave <version> now**.
Your regions, app rules and startup choice are kept.

### With the install script

Download `install.ps1` from the
[private releases page](https://github.com/alexchaoss/DisplayWeave/releases).
For online updates, install the [GitHub CLI](https://cli.github.com/) and sign in
with an account that can access this repository:

```powershell
gh auth login
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1
```

Run the same script again to install the newest published release, including
preview releases. To choose a release, add `-Version v0.5.9-preview`.
The script verifies the release ZIP's SHA-256 checksum before installation.
It does not request administrator rights or store a GitHub token.

You can also download the ZIP and its `.sha256` file from the releases page and
install without the GitHub CLI:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\install.ps1 -PackagePath .\DisplayWeave-0.5.9-preview.zip -ChecksumPath .\DisplayWeave-0.5.9-preview.zip.sha256
```

The default installation is `%LOCALAPPDATA%\DisplayWeave`. Each release has its
own folder under `versions`; the stable launcher and Start menu shortcut select
the current version. Layout and app rules live in `data` and survive updates.
For a first migration from a portable copy, add `-MigrateFrom "C:\path\to\portable"`.
Existing installed settings take priority. Exit older portable versions manually
before migrating; versions before 0.4 cannot receive the updater's exit request.
The updater never forcibly closes other applications.
If the portable copy already starts with Windows, use `-Startup Enable` during
migration to point its startup entry at the installed launcher.

Under **Status & help**, check **Start DisplayWeave with Windows** to start
quietly in the tray when you sign in. Uncheck it to disable startup. The installer
preserves your startup choice; `-Startup Enable` or `-Startup Disable` can change
it explicitly. A first installation leaves it disabled. `-NoLaunch` installs
without opening the app. `-InstallRoot` chooses a different installation folder.

Updates leave older version folders in place because apps can still have their
native component loaded. Compatible hooks on already-prepared windows reconnect
automatically after an update, including hooks from the 0.3 and 0.4 releases.
The new controller reuses the component already loaded in the app; it does not
load a second copy or replace running native code. The newest native code takes
effect when the target application is eventually restarted. Do not overwrite a
loaded DLL or delete old version folders while their apps are still running.

## Portable use and layout

Extract the portable ZIP and open `DisplayWeave.exe` (or `dist/DisplayWeave.exe`
in the source project). Keep its `native-v2-x64` and `native-v2-x86` folders next
to it. It uses the .NET Framework supplied with Windows 10/11. Exit other display or
window managers while using DisplayWeave so that two of them do not control the same app.

The settings window has three tabs: **Layout** for the display and ruler editor,
**App rules** for automatic handling and manual overrides, and **Status & help**
for detailed activity, shortcuts, and Exit. **Save layout** and **Pause handling** /
**Resume handling** stay visible on every tab. The bottom activity line opens
Status & help when clicked; hover it to read a longer message.

The window title and the tray icon's tooltip show the installed version.

**Status & help** lists every app DisplayWeave handles or tried to handle, with its
display, region and status: **Working**, **Starting**, **Not active**, **Ready**, **Fitted
from outside** or **Error**. **Ready** means nothing is loaded into the app and it is
fitted into its region from outside once maximized, as for Chromium-based apps. Windows
first maximizes such a window to the whole display and DisplayWeave fits it within about
one frame; Windows' maximize animation hides that frame. With window animations turned
off in Windows, the full-width frame can show briefly. An app with a working component
goes straight to its region. Select a row to read exactly what happened and what to do.
The status at the top right opens this list. When the layout's display is missing
or its regions no longer fit it, the status reads **Layout needs attention** and the
message names the displays Windows reports. Another display or region tool can
rename them; exit it. If DisplayWeave hits an internal error with one app, it leaves
only that app alone and shows the error on its row; every other app stays handled.
Choose **Apply saved rules** or Force fullscreen to retry that app. Status & help keeps
the technical details for a bug report.

1. On **Layout**, choose a display and optionally a preset. Set **Grid every (px)** to the desired ruler
   spacing (for example 10, 50, or 100 pixels). Presets are computed for the display's size, shape
   and Windows scaling. Halves, columns and quadrants are equal. **Wide center** keeps a 16:9 center
   at full height with two equal sides: 1980 | 3720 | 1980 on a 7680 × 2100 work area, and
   440 | 2560 | 440 on a 3440 × 1440 display. On a 16:9 display it uses quarters instead.
2. Drag the pins along the horizontal rulers to set column widths, or along the
   vertical rulers to set row heights. Pins snap to the pixel grid and stop before
   crossing their neighbors. Double-click a ruler, or use **Add width pin** /
   **Add height pin**, to add a divider. Select a pin and choose **Remove selected
   pin** to merge its neighboring regions.
3. Keep **Automatically use each app's current region** checked on **App rules**
   and choose **Save layout**. Saving writes
   the file, applies the layout, and resumes region handling. The green status
   line confirms the saved region count and grid spacing; unsaved edits are marked.
4. Move an ordinary app window into the desired region, then maximize it or use its
   fullscreen command. DisplayWeave prepares eligible apps automatically. Normal
   windows stay freely movable. If a window straddles regions, the greatest
   overlap chooses its region (the window center breaks ties).
5. If automatic handling does not work for an app, open **App rules** and use
   **Drag onto app** (or the window dropdown). Choose its target region on that
   same tab, then **Force fullscreen**. Wait for the ready status and try the
   app's fullscreen command again.
6. Some apps refuse DisplayWeave's component (Status & help reports a loader
   failure). Run those in their **windowed** mode and maximize
   them: automatic handling then fits the maximized window into its current region
   from outside. See [Apps that refuse the component](#apps-that-refuse-the-component).
7. Use **Ignore this app** to save an exclusion, **Restore selected** to release
   it for this session, or **Pause handling** to suspend every rule.

Portable rules and layout are saved beside the executable as `rules.xml` and
`layout.xml`; installed copies use the shared `data` folder.
Saved rules apply on the next launch and to future matching application windows.
They match the full executable path and window class, with one target window and
region per process. A missing region or monitor is not silently substituted; the
app shows as **Error** in Status & help until you assign it a region again. If
`rules.xml` cannot be read, DisplayWeave starts without rules, says so, and copies
the file to `rules.xml.unreadable.bak` before a save can replace it.
Changing a layout pauses handling; **Save layout** saves and resumes it.
**Apply saved rules** also saves and resumes. Manual Force and Ignore rules take
priority over automatic region selection. Automatic mode does not create saved
rules for each application. Normal restore or leaving fullscreen returns an
automatically managed app to its previous normal placement.

The Windows desktop, taskbar, Start menu, and shell processes are always excluded,
including from manual Force rules, and keep their full size. File Explorer folder
windows can share the process that runs the desktop and taskbar, so nothing is ever
loaded into them either. From 0.5.9, a maximized folder window is fitted into its
region from outside, as for Chromium-based apps, and Status & help lists it as Ready.
Since 0.3.3 the application and native helper both refuse shell hooks before they
are installed. Region layouts apply only to eligible application windows.

**X closes the settings window to the system tray**, keeping region handling
running. Double-click the tray icon, choose **Open DisplayWeave**, or launch the
executable again to reopen settings. **Exit DisplayWeave** in the window or tray
menu stops handling, restores controlled windows, and quits. Pending layout edits
stay pending when hiding to the tray. Exit saves them; if saving fails, the window
remains open with an error so edits are not silently lost.

Regions form a complete grid with no gaps or overlaps, up to nine cells. A width
pin runs through every row; a height pin runs through every column. The outer
display edges remain exact even when its dimensions are not a multiple of the
grid spacing. Interior dividers snap to the chosen spacing; a preset lowers the spacing
when needed (to 20 px for 1980 | 3720 | 1980) so its regions stay exact. A spacing
that cannot fit the existing dividers is rejected without changing the layout.

Moving pins retains application assignments. Splitting or merging gives the
affected regions new identities, so their manual Force rules must be assigned again;
unaffected regions keep their assignments. Old freehand layouts that do not form
a complete rectangular grid are replaced with a valid layout, with rules paused
for review. The previous file is backed up as `layout.xml.before-rulers.bak` on
the first migration. Ruler spacing is saved with the layout.

Shortcuts: **Ctrl+Alt+Shift+1…9** saves a region rule for the foreground app;
**Ctrl+Alt+Shift+0** releases it; **Ctrl+Alt+Shift+Backspace** pauses all rules.
The interface reports shortcuts already used by another program.

## Apps that refuse the component

Some apps refuse to load DisplayWeave's component, so a
Force rule cannot keep them in a region. From version 0.5.3, automatic handling (and a Force rule)
falls back for any app whose component did not load: run the app windowed and
maximize it from its title bar, and DisplayWeave moves the maximized window from
outside so its visible frame fills the region the window was in. No rule is needed.
Automatic handling still tries the component first, once per window.

**Maximize in region** on **App rules** does the same for one fixed region and never
loads, or tries to load, anything into that app; use it for apps that should not
receive a load attempt at all.

In both cases the title bar stays visible, as with any maximized window.
Un-maximizing hands the window back to Windows at its normal position. Pausing,
releasing, removing the rule, turning automatic handling off or exiting DisplayWeave returns a still-maximized
window to the whole monitor. An app that keeps changing its own maximized size is
left alone after three moves, with a message in Status & help.

The app still sees the whole monitor, so its own fullscreen or "windowed
fullscreen" modes still fill the monitor. Each move resizes the app's window; in-game
overlays from other programs that hook a game's graphics can crash some games on a
resize, so disable them if that happens.
A rules file containing a Maximize in region rule cannot be read by 0.5.1 or older.

## Chromium-based apps

Browsers and desktop apps built on Chromium stop themselves (a deliberate crash) when
their window handling is hooked. From 0.5.9, DisplayWeave never loads anything into a
window of Chromium's `Chrome_WidgetWin_*` class: automatic handling and Force rules
use the outside maximize fit described above, and a manual Force request explains why.
The native helper refuses these windows too. Their own fullscreen mode still fills the
monitor. An app hooked by 0.5.6 keeps that component until it restarts; it does nothing
while no DisplayWeave helper controls it, but restart the app if it still crashes.

## Launcher exclusion

Version 0.4.3 excludes one game launcher from region hooks because crashes have been
reported while it is hooked; `LauncherProcessProtection` in
`src/DisplayWeave.Core/FullscreenController.cs` lists its executable names. The
exclusion applies to automatic handling, manual Force rules, saved rules, and direct
native-helper requests. It matches complete executable names, not folders or parent
processes, so games started by the launcher remain eligible for region handling.

After updating, exit and reopen the launcher if it was already running with an
older DisplayWeave component. Pausing DisplayWeave does not unload that resident
component. The launcher runs at its normal size; region rules still apply to
eligible games.

## Apps running as administrator

DisplayWeave normally runs with ordinary user permissions, including when it
starts with Windows. Version 0.4.2 identifies a confirmed mismatch when an app
runs as administrator and DisplayWeave does not. A generic Windows access-denied
error (error 5) can have other causes; it does not by itself prove that elevation
is needed.

To handle an app already running as administrator, open **Status & help** and
choose **Restart as administrator**. DisplayWeave saves the layout and uses the
normal Windows UAC prompt. Approving restarts it with administrator access for
this session, preserving the settings directory and saved app rules. Cancelling
leaves the current DisplayWeave instance running. The restart does not change
your startup preference, add a service, bypass Windows protections, or make
administrator access mandatory for ordinary use.

For ordinary apps managed by an elevated controller, newly created control
objects retain the creator's default access list and use a medium integrity
label so the app can acknowledge commands. Elevated targets retain their default
security. Existing objects are never relabeled.

With administrator access, DisplayWeave starts its native helpers from a copy in
`%ProgramFiles%\DisplayWeave`, which only administrators can change, and removes
the copy when it exits. A copy still loaded by a running app is removed at the next
administrator start. Administrator mode never checks for or installs updates.

While running with administrator access, reopen settings from DisplayWeave's
tray icon. Use **Exit DisplayWeave** from the tray before running the ordinary
updater. Windows can block messages from a normal launcher or updater to an
administrator instance. After exiting it, an ordinary launch runs normally again.

Matching permissions does not establish compatibility with a game's graphics API
or protections.

## Fullscreen engine

Version 0.5 uses a USER32 monitor/window engine. An eligible app sees the selected display as the configured virtual regions. Monitor enumeration,
monitor lookup, monitor/work-area information and primary screen metrics agree
with that layout. Other physical displays stay available. Window positioning,
placement and maximize messages constrain the selected window to its region.
Normal windows remain movable in automatic mode.

The component intercepts `EnumDisplayMonitors`, `MonitorFromPoint`,
`MonitorFromRect`, `MonitorFromWindow`, `GetMonitorInfoA/W`, `GetSystemMetrics`,
`SetWindowPos`, `MoveWindow`, and `SetWindowPlacement`. It subclasses only the selected window
through the documented window procedure API. Layout changes are delivered through
an optional shared-memory sidecar without breaking compatible older controllers.

Each managed process gets an architecture-matched helper. Startup first uses
thread-specific Windows message hooks. If no startup callback arrives, the helper
uses ordinary Windows process permissions to load DisplayWeave's DLL and call its
exported initializer. This fallback does not depend on the application's message
filters. Loading and initialization share a bounded deadline, and diagnostics
identify the failed stage. No global hook, display driver, administrative service,
or security-policy change is installed.

Version 0.5.1 corrects loader-failure reporting. When the load thread ends but
DisplayWeave's DLL is absent, earlier 0.5.0 helpers supplied error 1114 themselves;
that was not the target thread's `GetLastError`. The new diagnostic reports the
observed thread result, process state, and module verification separately. A raw
thread result is not interpreted as a Windows loader error or a full module handle.
The original loader error remains unknown when it was not captured in that thread.

The new engine does not intercept DirectX or alter swapchain fullscreen state.
It targets applications that size their fullscreen or maximized windows using
Windows monitor/window APIs. Genuine hardware-exclusive scanout is not divided
into regions, and applications that bypass these APIs may remain incompatible.

The native component stays loaded until the target application exits. Ignore,
pause, exiting DisplayWeave, and controller heartbeat expiry make its hooks pass
through and request restoration of the window. Restart the target application
to unload the component completely. Depending on an application's own fullscreen
state, its fullscreen control may be needed after release. No unrelated application
is forcibly closed during an update.

Version 0.4.1 reconnects to an existing compatible component after its previous
controller exits. It checks the target window, protocol, and exclusive controller
ownership, then waits for fresh acknowledgements before resuming handling. A
different installation folder alone no longer requires restarting an app.

A reconnected component keeps the engine it was loaded with until the target app
restarts; a new installation does not replace code inside a running app. The
component also takes over any other window of the same app, such as a second
browser window.

A component from an earlier build under another name or native protocol stays
loaded but inactive. DisplayWeave loads its current component beside it, so the app
does not need a restart. A restart is needed only when a loaded DisplayWeave
component could not install its hooks or its control data is damaged; only these
cases show a restart instruction. Close and reopen the target app, then choose
**Save layout** or **Apply saved rules** to retry. Closing
a window may leave some apps running in their tray. App-specific failures remain
visible in the footer and **Status & help** even when another app reconnects.

To update a portable copy, use **Exit DisplayWeave**, extract the new release to
a separate folder, and copy `layout.xml` and `rules.xml` there. The installer is
recommended for subsequent updates because it keeps settings separate from
version files. Release archives contain no personal settings.

## Scope of this preview

- Consistent virtual-monitor geometry for 32-bit and 64-bit Win32 applications.
- Automatic maximize and borderless fullscreen handling, with manual Force rules
  for applications that need a fixed region.
- A Direct3D 11 windowed rendering fixture exercises monitor APIs, layout changes,
  restoration, loading, and continued rendering; see `TESTING.md` for actual results.
- No graphics-API exclusive-fullscreen interception in the new engine. Commercial
  game compatibility remains unverified.
- A confirmed elevation mismatch can be addressed with the explicit administrator
  restart above. Protected applications may still reject the hook; no protection
  bypass is attempted.
- Apps that are not DPI-aware are confined at the wrong size when Windows display
  scaling is above 100%. DPI-aware apps, which include most games, fit their region
  exactly.
- One selected window per process, one editable monitor layout, up to nine custom
  regions. Different independent processes can occupy different regions.

This is a testable first version, not a claim of compatibility with every app.

## Build and test

These commands apply to the source project, not the portable ZIP.

```powershell
.\scripts\fetch-tools.ps1
.\scripts\build-native.ps1 -Architecture all
.\scripts\build.ps1
.\dist\DisplayWeave.exe --self-test managed-test-results.txt
.\dist\native-v2-x64\RegionHookHost.exe --self-test-policy
.\dist\native-v2-x86\RegionHookHost.exe --self-test-policy
.\scripts\package.ps1
.\tests\test-monitor.ps1 -Architecture all
.\tests\test-monitor.ps1 -Architecture all -BootstrapLoader -FilteredMessages
.\tests\test-monitor.ps1 -Architecture all -HeartbeatExpiry
```

`fetch-tools.ps1` downloads Zig 0.13.0 and checks it against a SHA-256 pinned in the
script. MinHook v1.3.4 is vendored as sources and license only. The toolchain stays
inside the workspace.
The C# build uses Windows' .NET Framework compiler. Native integration tests open
temporary DirectX test windows and attach only to those test processes. The monitor fixture keeps its Direct3D swapchain windowed throughout. Legacy DXGI
fixtures remain available for testing older resident components only.

The app is in `src/DisplayWeave`, its engine in `src/DisplayWeave.Core`, its native
code in `native/legacy`; build scripts are in `scripts/`, installers in `installer/` and fixtures in `tests/` (see the
[runbook](docs/development.md#repository-layout)). MinHook's license
is preserved in `third_party/minhook/LICENSE.txt` and copied into the distribution.
GitHub Actions is configured to build both architectures, run noninteractive checks,
and upload the portable packages and test reports. `src/DisplayWeave/version.txt` and
`release.json` define the package version.

## Use the engine without the UI

The region engine is a separate library, `DisplayWeave.Core.dll`, built from
`src/DisplayWeave.Core`. It is compiled without any UI reference, so UI code cannot
creep into it. `RegionService` loads the layout and app rules that the app saves,
runs the engine, and offers the app's actions as methods that return a message for
the user:
- `Assign` a window to a region, as Force fullscreen or Maximize in region
- `Ignore` an app, `Release` a window, `RemoveRule`
- `Pause`, `ApplySavedRules`, `ApplyLayout`

`Layout`, `Rules` and `Engine` expose the full state, and save failures surface as
`IOException`. `Engine.HandledApps()` lists each handled app with its state, region
and error; `Engine.LayoutProblem` says why the layout cannot be used. A minimal host:

```csharp
// csc /r:DisplayWeave.Core.dll /r:System.Drawing.dll Headless.cs
using System;
using System.Threading;
using DisplayWeave;

static class Headless
{
    static void Main()
    {
        WindowManager.EnableDpiAwareness(); // the engine works in physical pixels
        string data = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\DisplayWeave\data");
        bool stop = false;
        // Ctrl+C ends the loop, so disposing the service returns windows to normal.
        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; stop = true; };
        using (RegionService service = new RegionService(data))
            while (!stop) { service.Tick(); Thread.Sleep(250); } // always from the same thread
    }
}
```

Put the host next to `DisplayWeave.Core.dll` and the `native-v2-x64` and
`native-v2-x86` folders; the engine starts its native helpers from there. A host that
runs as administrator should copy those folders somewhere only administrators can
change and set `FullscreenController.HelperDirectory`, as the app does. Exit the
DisplayWeave app first, so two controllers never handle the same windows.
`DisplayMonitor` lists monitors by the device names that layouts and rules use.
.NET also has a `System.ApplicationIdentity`, so a host that imports both namespaces and
names the type adds `using ApplicationIdentity = DisplayWeave.ApplicationIdentity;`.

## License

DisplayWeave is released under the [MIT License](LICENSE). It includes MinHook,
which has its own BSD 2-Clause license in `third_party/minhook/LICENSE.txt`.

## References

- [Microsoft's thread-specific Windows hook API](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowshookexw)
- [Windows monitor enumeration](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumdisplaymonitors)
- [DXGI fullscreen API](https://learn.microsoft.com/en-us/windows/win32/api/dxgi/nf-dxgi-idxgiswapchain-setfullscreenstate)
- [MinHook source and license](https://github.com/TsudaKageyu/minhook)
