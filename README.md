# DisplayWeave

[![Build](https://img.shields.io/github/actions/workflow/status/alexchaoss/DisplayWeave/build.yml?branch=main&label=build)](https://github.com/alexchaoss/DisplayWeave/actions/workflows/build.yml)
[![Release](https://img.shields.io/github/v/release/alexchaoss/DisplayWeave)](https://github.com/alexchaoss/DisplayWeave/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/alexchaoss/DisplayWeave/total)](https://github.com/alexchaoss/DisplayWeave/releases)
![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)
![Platform: Windows 10/11 x64](https://img.shields.io/badge/platform-Windows%2010%2F11%20x64-0078D6)

Split an ultrawide monitor into custom screen regions. Maximize or borderless-fullscreen
an app inside a region and DisplayWeave fits it there, while the desktop and taskbar
keep their normal size. Regions, per-app rules and layout profiles are all configurable.

## Install

Windows 10/11 x64 is required.

Download `DisplayWeave-Setup-<version>.exe` from the
[releases page](https://github.com/alexchaoss/DisplayWeave/releases) and run it. It
installs for your Windows account (no administrator rights), keeps your regions and app
rules across updates, and can start DisplayWeave when you sign in. A portable ZIP is
also published; extract it and open `DisplayWeave.exe`.

The setup, the app and its helpers are code-signed by Alexandre St-Pierre. Windows
SmartScreen may still show "Windows protected your PC" while the publisher builds
reputation: choose **More info → Run anyway** only when it names Alexandre St-Pierre
as the publisher and the file came from the releases page. Each file's SHA-256 is in
the matching `.sha256` file.

**Updates** arrive by themselves: an installed DisplayWeave checks the releases page
hourly, verifies the signature and checksum, and installs the new version once its
window is closed and no game is being held in a region by a rule. The tray menu offers
**Install DisplayWeave <version> now**, and **Go back to DisplayWeave <version>**
returns to the earlier version if a new one misbehaves.

**Uninstall** from **Settings → Apps → Installed apps → DisplayWeave**. It asks whether to
also delete your regions and app rules.

## Quick start

1. On **Layout**, choose a display and optionally a preset (halves, columns, quadrants
   or **Wide center**). Set **Grid every (px)** to the ruler spacing you want.
2. Drag the pins along the rulers to set column widths and row heights. Double-click a
   ruler to add a divider; select a pin and choose **Remove selected pin** to merge
   regions. Regions never overlap, and there can be up to nine.
3. Choose **Save layout**.
4. Move an app into a region, then maximize it or switch it to borderless (windowed)
   fullscreen. DisplayWeave fits it into that region from outside; nothing is loaded
   into the app. Normal windows stay freely movable. A window that straddles regions
   goes to the one it overlaps most. Hold **Shift** while maximizing to leave a window
   filling its display once.

Exclusive fullscreen is never confined: set the game to borderless or windowed.
Closing the window with **X** keeps DisplayWeave running in the tray; use **Exit
DisplayWeave** to quit and return every window to normal.

The Windows desktop, taskbar, Start menu and other shell processes are always left
alone.

### Tabs

- **Layout**: display, ruler editor and layout profiles.
- **App rules**: how each app is handled (below).
- **Shortcuts**: keyboard shortcuts you record yourself; none are built in.
- **Status & help**: every handled app with its status (**Ready**, **Fitted from
  outside**, **Error**, ...), the activity log, startup option and **Diagnostic
  report…**, which builds a Markdown summary you can review and paste into a bug
  report. It contains no window titles, paths or user names.

### Layout profiles

A profile is a whole layout with its own app rules, such as one for work and one for
games. On **Layout**, **New…** copies the current layout into a new profile; switch from
the list or the tray menu's **Layout profile**.

If the layout's display is not connected at start, DisplayWeave keeps the layout,
handles nothing and resumes when the display is back.

## App rules

A rule overrides automatic handling for one app. On **App rules**, select an app, pick
an option (and a region if it needs one) and choose **Save**.

| Option | Loads anything into the app? | Use it for |
| --- | --- | --- |
| **Maximize in region** | No | Pinning an app to one region. A maximized window keeps its title bar. |
| **Borderless in region** | No | Games that resize themselves back to the whole monitor. Set the game to **Windowed**; DisplayWeave removes its title bar and sizes it to the region. |
| **Centered fullscreen** | No | A game in exclusive fullscreen in the middle region, centered by the graphics driver (App rules spells out the one-time setup). |
| **Force fullscreen** | **Yes**, DisplayWeave's component | Apps whose own fullscreen modes must stay in the region, when the other options do not work and the app's terms allow it. |
| **Ignore this app** | No | An app DisplayWeave must never move or resize. |
| **Automatic (no rule)** | No | Removing an app's rule. |

**Force fullscreen** is the only option that loads code into an app. Anti-cheat software
can treat that as tampering, so use it only for apps whose terms allow it. Chromium-based
apps (browsers and many desktop apps) are fitted from outside automatically and are
never hooked unless you add a Force rule yourself.

File Explorer folder windows can only use the outside fit (Maximize in region).

## Apps running as administrator

DisplayWeave runs with normal permissions. To handle an app that runs as administrator,
open **Status & help** and choose **Restart as administrator** (a normal UAC prompt
appears). It lasts for that session and never changes your startup choice. Reopen
settings from the tray icon while it runs, and exit it from the tray before updating.

## Limits

- Hardware-exclusive fullscreen is not divided into regions, and Force rules do not
  intercept DirectX.
- Apps that are not DPI-aware are fitted at the wrong size when Windows scaling is above
  100%; most games are DPI-aware.
- One window per process. Compatibility with every app is not claimed, and no
  protection bypass is attempted. Exit other display or window managers while using
  DisplayWeave.

## Build and development

```powershell
.\scripts\fetch-tools.ps1
.\scripts\build-native.ps1 -Architecture all
.\scripts\build.ps1
.\scripts\package.ps1
```

The app is in `src/DisplayWeave`, the reusable engine (`DisplayWeave.Core.dll`) in
`src/DisplayWeave.Core`, and native code in `native/legacy`. See the
[development runbook](docs/development.md), the [engine notes](docs/engine.md) for the
hooking internals and a headless host example, [docs/testing.md](docs/testing.md) for
test results, [CONTRIBUTING.md](.github/CONTRIBUTING.md) if you want to help, and
[AGENTS.md](AGENTS.md) for AI-agent continuation. Report security
problems privately, as [SECURITY.md](.github/SECURITY.md) explains.

## License

DisplayWeave is released under the [MIT License](LICENSE). It includes MinHook, which
has its own BSD 2-Clause license in `third_party/minhook/LICENSE.txt`.
