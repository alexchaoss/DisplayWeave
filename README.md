# DisplayWeave

Windows virtual-monitor compatibility app with automatic region detection, custom
screen regions, and optional per-application rules.

For development or AI-agent continuation, start with [AGENTS.md](AGENTS.md) and the
[development runbook](docs/development.md).

## Install or update

Windows 10/11 x64 is required.

### With the setup (recommended)

Download `DisplayWeave-Setup-<version>.exe` from the
[releases page](https://github.com/alexchaoss/DisplayWeave/releases) and run
it. It installs or updates DisplayWeave for your Windows account (no administrator
rights), keeps your regions and app rules, can turn on **Start DisplayWeave when I
sign in**, and adds DisplayWeave to **Settings → Apps → Installed apps**. A running
DisplayWeave is closed and reopened. The setup is built with Inno Setup; its own
SHA-256 is published in the matching `.exe.sha256` file.

From 1.0.5, the setup starts nothing through PowerShell: a signed launcher program
opens DisplayWeave from the Start menu and at sign-in. From 1.0.0, the setup, the app,
its native helpers and its launcher (from 1.0.5 also its uninstaller) are code-signed by Alexandre St-Pierre (Microsoft
Artifact Signing); earlier releases are not. Windows SmartScreen may still show
"Windows protected your PC" while the publisher builds reputation.
Choose **More info → Run anyway** only when it names Alexandre St-Pierre as the
publisher and the file is from this releases page; its SHA-256 is in the matching
`.exe.sha256` file.

To uninstall, use **Settings → Apps → Installed apps → DisplayWeave → Uninstall**.
It closes DisplayWeave and removes its program files, Start menu shortcut and startup
entry. When it asks **Also delete your DisplayWeave regions and app rules?**, choose
**No** to keep them (a silent uninstall always keeps them). If another DisplayWeave
installation is running, the setup and the uninstaller stop and ask you to close it
rather than closing it for you. A helper file that a still-running app has loaded can
stay in `%LOCALAPPDATA%\DisplayWeave\versions`; delete it after closing that app. If you used **Restart as administrator** and a helper copy was still in
use when DisplayWeave exited, delete `%ProgramFiles%\DisplayWeave` as administrator.

For scripted or unattended use, the setup takes the standard Inno Setup switches, for
example `/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /DIR="<folder>" /LOG="<file>"`. The
automatic updater runs it this way.

### Automatic updates

An installed DisplayWeave checks the public
[releases page](https://github.com/alexchaoss/DisplayWeave/releases) as soon as it
starts and then every hour (before 1.0.2: a minute after start, then every six hours).
It reads the releases anonymously over HTTPS: no GitHub account, token or GitHub CLI
is needed. If the check fails (for example while offline), **Status & help** says why
and it tries again five minutes later.

Versions 0.5.5 to 0.5.9 checked through a signed-in GitHub CLI instead. If you run one
of those without it, install the newest setup once by hand; later updates then arrive
by themselves.

From 1.0.2, releases are signed with a release key that versions before 1.0.1 do not
know, so those copies report that the update is not signed. Install the newest setup
once by hand; later updates then arrive by themselves.

A newer release's setup is downloaded with its SHA-256 file and that file's signature.
DisplayWeave installs it only when the signature comes from a DisplayWeave release key
and the setup matches the checksum. The signing key is kept off GitHub, so someone who
gains access to the repository or its releases cannot push an update to installed
copies. A release without a valid signature is refused, and **Status & help** says so.
A verified setup installs by itself as soon as the DisplayWeave window is closed and no
game is in its region through a Force fullscreen or Borderless in region rule, so such
a game is never resized by the restart. Windows fitted
from outside do not delay it: from 0.5.11 they stay in their regions and the new
version takes over the maximized ones. Until the update installs, the tray menu
offers **Install DisplayWeave <version> now**.
Your regions, app rules and startup choice are kept.

From 0.6.0, right before it starts a downloaded setup, DisplayWeave checks it again and holds it open,
so it cannot be swapped in between. A new version that stops twice before it has run
for a minute, without being exited, is given up: the launcher goes back to the version
it replaced, and updates skip it from then on. The tray menu's **Go back to
DisplayWeave <version>** does the same by hand; DisplayWeave restarts in the earlier
version, which stays installed next to the newer one. Version 0.5.12 and older do not know to skip
a given-up version, so once back on one of them it installs that version again at its
next check.

### Install layout

The default installation is `%LOCALAPPDATA%\DisplayWeave`. Each release has its own
folder under `versions`; the signed launcher (`DisplayWeave.Launcher.exe`) and the
Start menu shortcut select the current version named in `current.json`. Layout and app
rules live in `data` and survive updates. The setup does not request administrator
rights, and its silent switches (above) choose a different folder with `/DIR=`.

Under **Status & help**, check **Start DisplayWeave with Windows** to start quietly in
the tray when you sign in; uncheck it to disable startup. A first installation offers
the same choice and leaves it off by default; an update keeps whatever you chose.

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

The settings window has four tabs: **Layout** for the display, ruler editor and layout
profiles, **App rules** for automatic handling and manual overrides, **Shortcuts** for
your keyboard shortcuts, and **Status & help** for detailed activity and Exit. **Save layout** and **Pause handling** /
**Resume handling** stay visible on every tab. The bottom activity line opens
Status & help when clicked; hover it to read a longer message.

The window title and the tray icon's tooltip show the installed version.

**Status & help** lists every app DisplayWeave handles or tried to handle, with its
display, region and status: **Working**, **Starting**, **Not active**, **Ready**, **Fitted
from outside** or **Error**. **Ready** means nothing is loaded into the app: once it is
maximized or in borderless fullscreen, DisplayWeave fits it into its region from outside.
From 0.5.11 automatic handling treats every app this way; **Working**, **Starting** and
**Not active** are for apps with a Force rule. Windows first maximizes a window to the
whole display and DisplayWeave fits it within about one frame; Windows' maximize
animation hides that frame. With window animations turned off in Windows, the
full-width frame can show briefly. An app with a working component goes straight to
its region. Select a row to read exactly what happened and what to do.
The status at the top right opens this list. From 1.1.4, a layout whose display is not
connected at start (off, asleep or switched away) keeps that display: it is listed as
**not connected**, nothing is handled, and exiting leaves the layout as it was. When the
display is connected again, handling resumes by itself. Choose another display to move
the layout there. When the layout's display is missing
or its regions no longer fit it, the status reads **Layout needs attention** and the
message names the displays Windows reports. Another display or region tool can
rename them; exit it. If DisplayWeave hits an internal error with one app, it leaves
only that app alone and shows the error on its row; every other app stays handled.
Choose **Apply saved rules** or Force fullscreen to retry that app. Status & help keeps
the technical details for a bug report.

To report a bug, choose **Diagnostic report…** at the bottom of Status & help. It shows
a Markdown summary for you to review: DisplayWeave and Windows versions, each display's
size, work area, scaling and adapter, graphics driver versions, the layout and each
handled app's status, and the last 40 lines of the activity log. It has no window
titles, paths, or user or computer names. App file names appear only if you tick
**Include app file names**. Choose **Copy to
clipboard**, then **Open a new issue** and paste it. Report security problems privately,
as [SECURITY.md](.github/SECURITY.md) explains.

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
4. Move an app window into the desired region, then maximize it or switch it to
   borderless (windowed) fullscreen. DisplayWeave fits the window into that region
   from outside; nothing is loaded into the app. Exclusive fullscreen is not
   confined. Normal windows stay freely movable. If a window straddles regions, the
   greatest overlap chooses its region (the window center breaks ties).
5. If an app needs something different, such as a game that resizes itself back to
   the whole monitor, open **App rules**. Choose the app in the list on the left (or
   **Drag onto app**), then how DisplayWeave handles it and, if needed, its region on
   the right, and choose **Save**. The box beside the options explains the selected
   one; hover **ⓘ** for the same text. See [App rules](#app-rules).
6. **Release for now** returns the selected app to normal until you choose **Apply
   saved rules** or restart DisplayWeave; **Pause handling** suspends every rule.

Portable settings are saved beside the executable (`layout.xml`, `rules.xml`,
`shortcuts.xml`, `app-regions.xml` and the `profiles` folder); installed copies use the
shared `data` folder. From 0.6.0, what DisplayWeave did is written one line at a time to
`logs\activity.log` there, kept to about 1 MB plus one older file.
Saved rules apply on the next launch and to future matching application windows.
They match the full executable path and window class, with one target window and
region per process. From 0.6.0, a part of the class that changes on every run (a GUID, or the
suffix some frameworks add) is ignored, and Store apps, which all share one host
program, are told apart by their app ID. App rules shows when each rule
last applied. When Windows renames the display (`\\.\DISPLAYn`), as it can after a
restart or a driver update, the layout and its rules follow the monitor; if the
monitor itself is gone, they move only to the one display of the same size. A missing region or monitor is not silently substituted; the
app shows as **Error** in Status & help until you assign it a region again. If
`rules.xml` cannot be read, DisplayWeave starts without rules, says so, and copies
the file to `rules.xml.unreadable.bak` before a save can replace it.
Changing a layout pauses handling; **Save layout** saves and resumes it.
**Apply saved rules** also saves and resumes. Saved rules take priority over
automatic region selection. Automatic mode does not create saved
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
affected regions new identities. From 1.1.4, saving the layout then unpins the profile's
Force fullscreen and Maximize in region rules for those regions: they use the region
the app is in, and the message says how many changed. Borderless in region and Centered
fullscreen rules need a region, so they show it as missing until you choose one.
Unaffected regions keep their assignments. Old freehand layouts that do not form
a complete rectangular grid are replaced with a valid layout, with rules paused
for review. The previous file is backed up as `layout.xml.before-rulers.bak` on
the first migration. Ruler spacing is saved with the layout.

### Layout profiles

From 0.6.0, a profile is a whole layout with its own app rules, such as one for work and one for
games. On **Layout**, **New…** copies the current layout and its rules into a new
profile and switches to it; choose a profile in the list, or in the tray menu's
**Layout profile**, to switch. Switching saves the current layout, returns fitted
windows to their monitor and fits them again in the new profile's regions.
**Delete** removes the profile in use after switching to another one. If the switched
layout cannot be saved, the current profile stays in use and handling goes on as before. Ignore rules,
and rules saved before profiles existed, apply in every profile.

### Shortcuts

From 0.6.0, DisplayWeave has no keyboard shortcuts of its own; the Ctrl+Alt+Shift ones of
earlier versions are gone. On **Shortcuts**, **Add shortcut…**
records the keys you press (with Ctrl or Alt) and what they do for the app in front:
fit it into a region (a Maximize in region rule, and the window is maximized), show it
borderless in a region, force its fullscreen into a region, or release it; or pause and
resume handling, or switch to a layout profile. The list says when another program
already uses a shortcut.

Hold **Shift** while you maximize a window to leave it filling its display that once;
it is fitted again the next time it is maximized.

## Automatic handling and the component

From 0.5.11, automatic handling never loads anything into an app. When an app's window
is maximized, or switches to borderless (windowed) fullscreen, in a region, DisplayWeave
moves the window from outside so its visible frame fills that region. No rule is needed.
Earlier versions loaded DisplayWeave's component into every eligible app; an app that
received it from an earlier version keeps it, inactive, until the app restarts.

- **Maximized windows** keep their title bar, as with any maximized window.
- **Borderless fullscreen**: a window without a title bar that covers its whole
  monitor is fitted into its region. Always-on-top, click-through and never-activated
  windows, such as overlays and screen-capture tools, are left alone.
- **Exclusive fullscreen** takes over the display and is never confined. Switch the
  game to its borderless or windowed mode.

The app still sees the whole monitor. A game in borderless fullscreen that resizes
itself back to the monitor is left alone after one move (from 0.5.12), and a
maximized window that keeps changing its size after three, with a message in Status &
help. Such a game needs an [App rule](#app-rules), usually **Borderless in region**:
from 0.6.0, DisplayWeave offers it in a notification, once per game and session. Click it to save
that rule for the game's region, then set the game to Windowed.
Each move resizes the app's window; in-game overlays from other programs that hook a
game's graphics can crash some games on a resize, so disable them if that happens.

From 0.6.0, DisplayWeave remembers the region each app was last fitted into, per layout profile.
An app that starts maximized or in borderless fullscreen goes back to that region
instead of the one under its restored place; windows already open when DisplayWeave
starts stay where they are.

Un-maximizing or leaving fullscreen hands the window back to the app and Windows.
Pausing, releasing, removing the rule, turning automatic handling off or exiting
DisplayWeave returns a still-maximized window to the whole monitor, and a borderless
fullscreen window to cover its whole monitor again.

## App rules

A rule tells DisplayWeave how to handle one app, whatever automatic handling would
do. **App rules** lists apps on the left: **Saved** (apps with a rule, grayed when not
running) above **Not saved** (running apps handled automatically). Select one, choose one
of these on the right (and its region when it needs one), then **Save**:

| Option | Loads anything into the app? | Use it for |
| --- | --- | --- |
| **Maximize in region** | No | Pinning an app to one region. Maximize it, or use its borderless fullscreen, and its window is fitted from outside; a maximized window keeps its title bar. |
| **Borderless in region** | No | Games that resize themselves back. Set the game to **Windowed**; DisplayWeave removes its title bar and border from outside and sizes it to the region. |
| **Centered fullscreen** | No | A game in exclusive fullscreen in the middle region, centered by the graphics driver. |
| **Force fullscreen** | **Yes**, DisplayWeave's component | Apps whose own fullscreen modes must stay in the region, when the other options do not work and the app's terms allow it. |
| **Ignore this app** | No | An app DisplayWeave must never move or resize. |
| **Automatic (no rule)** | No | Removing an app's rule: saving it returns the app to automatic handling. |

**Borderless in region** (from 0.5.12) applies while the window is shown and not
maximized. Some games put their title bar back and keep their picture in the region;
DisplayWeave leaves that as it is, with the title bar above the region (off screen when
the region reaches the top of the display). If the game moves out of the region
instead, DisplayWeave stops after a few tries and says so; pause and resume handling to
retry. Pausing, releasing, removing the rule or exiting DisplayWeave gives the window its
title bar, border and first place back. If DisplayWeave 0.6.0 or later stops unexpectedly, it gives
them back as it closes, or its next start takes such windows over from a list it keeps
while they are shown this way. Automatic updates wait while a window is shown
this way. File Explorer folder windows can only use Maximize in region.

**Centered fullscreen** (from 0.5.12) needs a one-time setup that App rules spells out
for the chosen region: the resolution to use (the region's width and, for a region as
tall as the display, the display's full height), whether Windows offers it already, and
the steps for your graphics driver. On a 7680 × 2160 display with a 1980 | 3720 | 1980
layout, that is 3720 × 2160: turn on GPU scaling, set it to centered, add 3720 × 2160 as
a custom resolution, and choose Fullscreen at 3720 × 2160 in the game. DisplayWeave
leaves the game's window alone and changes no display settings. It works only for a
region in the middle of its display. While the game has focus, the other regions are
black, and Windows can move other windows while the display is smaller.

From 0.6.0, when the display changes size while a fullscreen game is in front, as exclusive
fullscreen does, DisplayWeave pauses and resumes by itself once the display is back to
the size the layout was made for; the layout is never rebuilt for the temporary size.
Meanwhile **Save layout** reads **Use this display size**: choose it to keep the new
size instead. Without such a game, for example after you change the resolution in
Windows, the change is kept after a few seconds and handling stays paused until you
review the layout and save it.

**Force fullscreen** (or a [shortcut](#shortcuts) you make for it) loads DisplayWeave's
component into that one app. The app then sees its region as its monitor, so its own
fullscreen modes stay in the region too. Anti-cheat software can treat a loaded
component as tampering: use Force rules only for apps whose terms allow it. An app
that refuses the component falls back to the outside fit.

Selecting a saved app shows its option, region and the day it last applied; with
several layout profiles, the Saved heading names the profile in use. From 1.1.4 the rule
of an app that is not running can be changed too; it applies the next time the app starts. A rules
file containing a Borderless in region or Centered fullscreen rule cannot be read by
0.5.11 or older, and one with a Maximize in region rule by 0.5.1 or older.

## Chromium-based apps

Browsers and desktop apps built on Chromium stop themselves (a deliberate crash) when
their window handling is hooked. From 0.5.9, DisplayWeave never loads anything into a
window of Chromium's `Chrome_WidgetWin_*` class on its own: automatic handling uses the
outside fit described above. From 1.1.0 a Force fullscreen rule may load the component
into such a window, for its display information only; its window procedure is never
changed. From 0.5.11 their own fullscreen mode
counts as borderless fullscreen and is fitted from outside too. Such a fullscreen window
(a video, for example) spreads over the whole display again whenever it is clicked or
loses or regains focus; from 1.0.5 DisplayWeave puts it back as soon as it moves, every
time, instead of after a pause or giving up. An app hooked by 0.5.6
keeps that component until it restarts; it does nothing while no DisplayWeave helper
controls it, but restart the app if it still crashes.

## Launcher exclusion

Version 0.4.3 excludes one game launcher from region hooks because crashes have been
reported while it is hooked; `LauncherProcessProtection` in
`src/DisplayWeave.Core/ProcessProtection.cs` lists its executable names. The
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
the copy when it exits. From 0.6.0 it copies only the helper files its build lists and checks each
copy against the SHA-256 recorded when it was built; a changed helper stops the
administrator start with a message. A copy still loaded by a running app is removed at the next
administrator start. Administrator mode never checks for or installs updates. The app itself still starts from
the install folder your account can change; [SECURITY.md](.github/SECURITY.md) lists what that allows.

While running with administrator access, reopen settings from DisplayWeave's
tray icon. Use **Exit DisplayWeave** from the tray before running the ordinary
updater. Windows can block messages from a normal launcher or updater to an
administrator instance. After exiting it, an ordinary launch runs normally again.

Matching permissions does not establish compatibility with a game's graphics API
or protections.

## Fullscreen engine

Version 0.5 uses a USER32 monitor/window engine. From 0.5.11 it is loaded only into
apps with a Force rule. Such an app sees the selected display as the configured virtual regions. Monitor enumeration,
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
browser window. From 1.1.3 this works for browser windows too: earlier versions
refused to reconnect to them (Windows error 1400), so a browser with a Force rule
had to be reopened after every update or restart of DisplayWeave.

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

## Scope

- Consistent virtual-monitor geometry for 32-bit and 64-bit Win32 applications.
- Automatic maximize and borderless fullscreen handling from outside the app, with
  Force rules that load the component for apps that need it.
- A Direct3D 11 windowed rendering fixture exercises monitor APIs, layout changes,
  restoration, loading, and continued rendering; see [docs/testing.md](docs/testing.md) for actual results.
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
The C# build uses Windows' .NET Framework compiler. Build the native files first:
`build.ps1` records their SHA-256 in the app for administrator mode. Native integration tests open
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

- `SwitchProfile`, `CreateProfile`, `DeleteProfile` and `Profiles` for layout profiles,
  and `RebindMonitor` after Windows renames a display

`Layout`, `Rules`, `Shortcuts` and `Engine` expose the full state, and save failures surface as
`IOException`. Call `ActivityLog.Open(data)` to keep the same activity log as the app. `Engine.HandledApps()` lists each handled app with its state, region
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
