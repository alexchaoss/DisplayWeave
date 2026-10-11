# DisplayWeave development handoff

Snapshot: October 11, 2026, America/Toronto. **Treat the "next steps" list as proposals, not as a request to start work.**

## User intent and current status

The goal is software that confines fullscreen applications to customizable, non-overlapping ultrawide regions while leaving the physical desktop and taskbar alone. The user asked to avoid interfering with games in ways that could violate their terms; no game compatibility, anti-cheat or terms-of-service approval has been established.

On October 11 the user moved the experimental virtual-display prototype off `main`: it lives on the `prototype` branch, with its own handoff, report, architecture notes and fixtures. `main` holds only the hook-based app.

| Area | State |
| --- | --- |
| Repository layout | `src/DisplayWeave`, `src/DisplayWeave.Core`, `native/legacy`, `scripts`, `installer`, `tests`. See the [runbook](development.md#repository-layout). |
| App | `0.5.6-preview`. Region editor, rules, tray, sign-in startup, setup and uninstaller (`DisplayWeave-Setup-<version>.exe`), and automatic updates through the user's signed-in `gh`. Updates are checked every six hours and installed only while the window is hidden and no app is maximized or fullscreen in a region. When the component does not load, automatic handling fits a maximized window into its current region from outside; the **Maximize in region** rule does the same without any load attempt. Default install root: `%LOCALAPPDATA%\DisplayWeave`. |
| Engine library | `DisplayWeave.Core.dll` (`src/DisplayWeave.Core`) has no UI reference; `RegionService` holds the app's actions for the window or a headless host (README, "Use the engine without the UI"). |
| Administrator mode | Skips updates and starts native helpers from a copy under `%ProgramFiles%\DisplayWeave` that only administrators can change. Covered by self-tests; not yet run in a live elevated session. |
| Prototype | `prototype` branch only: DisplayWeave Prototype 0.1.0-preview plus the October 11 fix that runs its administrator step from hash-checked Program Files copies (verified unelevated only). A copy installed earlier in `%LOCALAPPDATA%\DisplayWeavePrototype` is untouched by this move. |

## Evidence

After the October 11 preset and resident-component changes: managed 103 (layout 20, rules 42, automatic 13, lifecycle 28), native policy 223 + IPC on x64 and x86, installer 20, setup 6, plus live checks on the real display with owned probes. Details in [TESTING.md](../TESTING.md).

A resident component is reused only when its control mapping (`Local\DisplayWeave_<pid>`) exists, never because a module is named `RegionHook.dll`. Any other `RegionHook.dll` (pre-rename RegionScreen builds; a later protocol, once its names change as `RegionProtocol.h` asks) stays inactive beside a freshly loaded component. A live resident worker also takes over another window of its process. The installed 0.5.5 refused the user's browser with error 1306 because it held an October 9 pre-rename hook; after installing 0.5.6 a helper attached there without a restart. Presets are computed from the display's size, aspect ratio and scaling (`RulerLayout.PresetPins`): Wide center on the user's 7680 × 2100 work area at 125% is 1980 | 3720 | 1980.

Status & help lists handled apps with their display, region and status; selecting a row shows the exact error. An error while handling one app leaves only that app alone, and the others stay handled. Only an engine error outside any app pauses handling; window errors are reported without pausing. A layout whose display is missing says so instead of showing "Handling active". A first start no longer pauses itself. While RegionScreen 0.5.4 runs, DisplayWeave sees its renamed displays (`\\.\REGIONSCREEN1..3`) and cannot resolve its layout. On October 11 RegionScreen 0.5.4 was uninstalled at the user's request with its own quiet uninstaller: its sign-in and uninstall entries are gone, its settings folder was kept, and two hook DLLs still loaded in running apps are deleted at the next sign-in. The pre-rename RegionScreen Prototype (`%LOCALAPPDATA%\RegionScreenPrototype`) is still installed. DisplayWeave 0.5.6-preview is installed and runs with automatic regions and a 1980 | 3720 | 1980 layout.

The user's main game refuses `RegionHook.dll` by its own protection; a clean retry showed no Windows mitigation policy blocking it. Do not try to get past that. The user plays it **windowed** and maximizes it from the title bar, which the external maximize fit handles; that still needs a test by the user. Crash minidumps seen earlier pointed at a third-party in-game overlay during Present after a resolution/output change.

## Known limits

- Apps that refuse the component get the outside maximize fit only, not fullscreen inside a region.
- The repository became public on October 11, 2026. Every earlier Actions run failed on account billing; whether public runs succeed has not been observed yet. The manual route is `scripts/release.ps1 -Publish`.
- Apps that are not DPI-aware are confined at the wrong size above 100% scaling: the native hook reports physical region coordinates to a process whose coordinates Windows scales.
- The first automatic update can only be observed once a release newer than the installed 0.5.6 exists.
- `tests/test-handoff.ps1` still expects the 0.4 DXGI capability (`CAPS 3`), so it fails on 0.5.x helpers before reaching a handoff. The October 11 resident checks used owned WinForms probes instead (TESTING.md).

## Where things live

```text
Install: %LOCALAPPDATA%\DisplayWeave (versions, data, current.json)
Administrator-mode helper copies: %ProgramFiles%\DisplayWeave\<user SID>_<id>
Update downloads: %TEMP%\DisplayWeaveUpdate
```

Remote: [alexchaoss/DisplayWeave](https://github.com/alexchaoss/DisplayWeave) (`main`, `prototype`). Release tags: app `v*`, prototype `prototype-v*`. RegionScreen 0.5.4 was uninstalled from the development computer on October 11.

## Proposed next steps

1. The user tests their game windowed and maximized with the installed app.
2. One **Restart as administrator** run to confirm the Program Files helper copy, attach and cleanup.
3. Confirm CI built and published `v0.5.6-preview` from the push to the public repository (or publish manually), then observe the first automatic update from 0.5.6.
4. Make the native hook convert region coordinates for apps that are not DPI-aware.
