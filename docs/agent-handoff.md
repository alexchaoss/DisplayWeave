# DisplayWeave development handoff

Snapshot: October 11, 2026, America/Toronto. **Treat the "next steps" list as proposals, not as a request to start work.**

## User intent and current status

The goal is software that confines fullscreen applications to customizable, non-overlapping ultrawide regions while leaving the physical desktop and taskbar alone. The user asked to avoid interfering with games in ways that could violate their terms; no game compatibility, anti-cheat or terms-of-service approval has been established.

On October 11 the user moved the experimental virtual-display prototype off `main`: it lives on the `prototype` branch, with its own handoff, report, architecture notes and fixtures. `main` holds only the hook-based app.

| Area | State |
| --- | --- |
| Repository layout | `src/DisplayWeave`, `src/DisplayWeave.Core`, `native/legacy`, `scripts`, `installer`, `tests`. See the [runbook](development.md#repository-layout). |
| App | `0.5.10-preview`. Region editor, rules, tray, sign-in startup, setup and uninstaller (`DisplayWeave-Setup-<version>.exe`), and signed automatic updates: the updater and `install.ps1` read the public releases anonymously over HTTPS (0.5.5 to 0.5.9 used the user's signed-in `gh`) and install only releases whose `.sha256` carries a pinned release-key signature (runbook, release flow). Status & help has **Diagnostic report…**. Updates are checked every six hours and installed only while the window is hidden and no app is maximized or fullscreen in a region. When the component does not load, automatic handling fits a maximized window into its current region from outside; the **Maximize in region** rule does the same without any load attempt. Default install root: `%LOCALAPPDATA%\DisplayWeave`. |
| Engine library | `DisplayWeave.Core.dll` (`src/DisplayWeave.Core`) has no UI reference; `RegionService` holds the app's actions for the window or a headless host (README, "Use the engine without the UI"). |
| Administrator mode | Skips updates and starts native helpers from a copy under `%ProgramFiles%\DisplayWeave` that only administrators can change. Covered by self-tests; not yet run in a live elevated session. |
| Prototype | `prototype` branch only: DisplayWeave Prototype 0.1.0-preview plus the October 11 fix that runs its administrator step from hash-checked Program Files copies (verified unelevated only). A copy installed earlier in `%LOCALAPPDATA%\DisplayWeavePrototype` is untouched by this move. |

## Evidence

After the October 11 preset and resident-component changes: managed 103 (layout 20, rules 42, automatic 13, lifecycle 28), native policy 223 + IPC on x64 and x86, installer 20, setup 6, plus live checks on the real display with owned probes. Details in [TESTING.md](../TESTING.md).

0.5.6 crashed the user's browser: once the resident-hook fix let automatic handling attach, Chromium stopped itself with a deliberate breakpoint. Chromium windows (class `Chrome_WidgetWin_*`) are never hooked from 0.5.8 and get only the outside maximize fit; Status & help lists them as **Ready** before they are maximized (`ChromiumWindowProtection`, native `RS_IsChromiumWindowClass`). Chromium resets any move of its maximized window in `WM_WINDOWPOSCHANGING`, so the outside fit passes `SWP_NOSENDCHANGING` (`FitFlags`). Without it, the user's Chromium-based apps filled the whole display when maximized. At the user's request, File Explorer folder windows (`CabinetWClass`, `ExploreWClass`) get the same outside-only fit, even in the shell's process (`ShellProcessProtection.IsFolderWindow`, `OutsideOnlyReason`); the desktop, taskbar and all other shell windows stay excluded and are never hooked. The outside fit cannot avoid one full-width frame (Windows maximizes before anyone outside the app can act); `MaximizeWatcher`, an out-of-context `EVENT_OBJECT_LOCATIONCHANGE` hook, ticks as soon as a window maximizes, which cut the flash from up to 250 ms to about 16 ms. Measured on screen, Windows' maximize animation then hides that frame; disabling the window's DWM transitions shows it, so animations are left alone (user decision, 2026-10-11). `v0.5.6-preview` was published by CI and has this problem.

A resident component is reused only when its control mapping (`Local\DisplayWeave_<pid>`) exists, never because a module is named `RegionHook.dll`. Any other `RegionHook.dll` (pre-rename RegionScreen builds; a later protocol, once its names change as `RegionProtocol.h` asks) stays inactive beside a freshly loaded component. A live resident worker also takes over another window of its process. The installed 0.5.5 refused the user's browser with error 1306 because it held an October 9 pre-rename hook; after installing 0.5.6 a helper attached there without a restart. Presets are computed from the display's size, aspect ratio and scaling (`RulerLayout.PresetPins`): Wide center on the user's 7680 × 2100 work area at 125% is 1980 | 3720 | 1980.

Status & help lists handled apps with their display, region and status; selecting a row shows the exact error. An error while handling one app leaves only that app alone, and the others stay handled. Only an engine error outside any app pauses handling; window errors are reported without pausing. A layout whose display is missing says so instead of showing "Handling active". A first start no longer pauses itself. While RegionScreen 0.5.4 runs, DisplayWeave sees its renamed displays (`\\.\REGIONSCREEN1..3`) and cannot resolve its layout. On October 11 RegionScreen 0.5.4 was uninstalled at the user's request with its own quiet uninstaller: its sign-in and uninstall entries are gone, its settings folder was kept, and two hook DLLs still loaded in running apps are deleted at the next sign-in. The pre-rename RegionScreen Prototype (`%LOCALAPPDATA%\RegionScreenPrototype`) is still installed. DisplayWeave 0.5.9-preview (the 0.5.8 work plus the new app icon; 0.5.8 was never published) is installed from the local release build and runs with automatic regions and a 1980 | 3720 | 1980 layout.

The user's main game refuses `RegionHook.dll` by its own protection; a clean retry showed no Windows mitigation policy blocking it. Do not try to get past that. The user plays it **windowed** and maximizes it from the title bar, which the external maximize fit handles; that still needs a test by the user. Crash minidumps seen earlier pointed at a third-party in-game overlay during Present after a resolution/output change.

## Known limits

- Apps that refuse the component get the outside maximize fit only, not fullscreen inside a region.
- The repository became public on October 11, 2026. Every earlier Actions run failed on account billing; whether public runs succeed has not been observed yet. The manual route is `scripts/release.ps1 -Publish`.
- Apps that are not DPI-aware are confined at the wrong size above 100% scaling: the native hook reports physical region coordinates to a process whose coordinates Windows scales.
- The first automatic update can only be observed once a release newer than the installed 0.5.6 exists.

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
5. Finish signed releases: the user runs `.\scripts\sign-release.ps1 -NewKey`, rebuilds and reruns the self-tests, commits, bumps the version, pushes, waits for CI's draft, then runs `.\scripts\sign-release.ps1 -Version <version>`. The first push runs the new CI matrix, Windows PowerShell 5.1 installer leg and attestation for the first time; the draft download, attestation check and publish steps of `sign-release.ps1` have never run. A backup key on another computer (`-PinKey`) is still missing.
6. GitHub settings the agent was not allowed to change (Settings → Code security, Settings → General → Releases, Settings → Rules): private vulnerability reporting, immutable releases, secret scanning with push protection, and a ruleset blocking force-pushes and deletion of `main` and `prototype`.
7. Administrator mode copies helpers from the user-writable install folder without checking them. A real check needs code signing (all PE files, then a same-signer check on the Program Files copy); it needs a certificate the user would have to buy.
