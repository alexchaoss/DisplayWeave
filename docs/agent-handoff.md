# DisplayWeave development handoff

Snapshot: October 11, 2026, America/Toronto. **Treat the "next steps" list as proposals, not as a request to start work.**

## User intent and current status

The goal is software that confines fullscreen applications to customizable, non-overlapping ultrawide regions while leaving the physical desktop and taskbar alone. The user asked to avoid interfering with games in ways that could violate their terms; no game compatibility, anti-cheat or terms-of-service approval has been established.

On October 11 the user moved the experimental virtual-display prototype off `main`: it lives on the `prototype` branch, with its own handoff, report, architecture notes and fixtures. `main` holds only the hook-based app.

| Area | State |
| --- | --- |
| Repository layout | `src/DisplayWeave`, `src/DisplayWeave.Core`, `native/legacy`, `scripts`, `installer`, `tests`. See the [runbook](development.md#repository-layout). |
| App | `0.5.5-preview`. Region editor, rules, tray, sign-in startup, setup and uninstaller (`DisplayWeave-Setup-<version>.exe`), and automatic updates through the user's signed-in `gh`. Updates are checked every six hours and installed only while the window is hidden and no app is maximized or fullscreen in a region. When the component does not load (e.g. Diablo IV), automatic handling fits a maximized window into its current region from outside; the **Maximize in region** rule does the same without any load attempt. Default install root: `%LOCALAPPDATA%\DisplayWeave`. |
| Engine library | `DisplayWeave.Core.dll` (`src/DisplayWeave.Core`) has no UI reference; `RegionService` holds the app's actions for the window or a headless host (README, "Use the engine without the UI"). |
| Administrator mode | Skips updates and starts native helpers from a copy under `%ProgramFiles%\DisplayWeave` that only administrators can change. Covered by self-tests; not yet run in a live elevated session. |
| Prototype | `prototype` branch only: DisplayWeave Prototype 0.1.0-preview plus the October 11 fix that runs its administrator step from hash-checked Program Files copies (verified unelevated only). A copy installed earlier in `%LOCALAPPDATA%\DisplayWeavePrototype` is untouched by this move. |

## Evidence

At the October 11 commits: managed 100 (layout 19, rules 40, automatic 13, lifecycle 28), native policy 225 + IPC on x64 and x86, installer 20, setup 6. Details in [TESTING.md](../TESTING.md).

Diablo IV refuses `RegionHook.dll` by its own protection; a clean retry showed no Windows mitigation policy blocking it. Do not try to get past that. The user plays Diablo IV **windowed** and maximizes it from the title bar, which the external maximize fit handles; it still needs a Diablo IV test by the user. Crash minidumps seen earlier pointed at the Discord overlay (`DiscordHook64.dll`) during Present after a resolution/output change.

## Known limits

- Apps that refuse the component get the outside maximize fit only, not fullscreen inside a region.
- GitHub Actions refuses jobs on this account because of billing, so CI neither tests nor publishes until that is fixed; the manual route is `scripts/release.ps1 -Publish`.
- The first automatic update can only be observed once a release newer than an installed 0.5.5 exists.

## Where things live

```text
Install: %LOCALAPPDATA%\DisplayWeave (versions, data, current.json)
Administrator-mode helper copies: %ProgramFiles%\DisplayWeave\<user SID>_<id>
Update downloads: %TEMP%\DisplayWeaveUpdate
```

Remote: [alexchaoss/DisplayWeave](https://github.com/alexchaoss/DisplayWeave) (`main`, `prototype`). Release tags: app `v*`, prototype `prototype-v*`. The development computer still has a pre-rename RegionScreen 0.5.4 build without an updater; exit it before installing DisplayWeave, so two copies never control the same windows.

## Proposed next steps

1. The user tests Diablo IV windowed and maximized with the installed app.
2. One **Restart as administrator** run to confirm the Program Files helper copy, attach and cleanup.
3. Fix GitHub billing or publish manually, then observe the first automatic update.
