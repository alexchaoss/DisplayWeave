# DisplayWeave development handoff

Snapshot: October 10, 2026 (afternoon), America/Toronto. This replaces the morning snapshot that stopped before the own-app prototype existed. **Treat the "next steps" list as proposals, not as a request to start work.**

## User intent and current status

The goal is software that confines fullscreen applications to customizable, non-overlapping ultrawide regions while leaving the physical desktop and taskbar alone. The legacy hook-based app (0.5.x-preview) implements the region editor, rules, tray and updater, but Diablo IV refuses its component; 0.5.3 fits such apps' maximized windows from outside, automatically or by rule. The selected replacement uses a real virtual Windows output, GPU projection and modal input, with no injection into the user's app. The user asked to avoid interfering with games in ways that could violate their terms; no game compatibility, anti-cheat or terms-of-service approval has been established.

On October 10 the user asked to finish the prototype, make a release, and reorganize the repository into a proper folder hierarchy. That work is done:

| Area | State |
| --- | --- |
| Repository layout | `src/DisplayWeave`, `src/DisplayWeave.Core`, `src/DisplayWeave.Prototype`, `native/legacy`, `native/projection`, `scripts`, `installer`, `tests`. See the [runbook](development.md#repository-layout). |
| Legacy app | `0.5.5-preview` adds automatic updates through the user's signed-in `gh` (private repository): it checks every six hours and installs a newer release's verified setup only while the window is hidden and no app is maximized or fullscreen in a region. CI's `release` job publishes `v<version>` when a push to `main` carries an unreleased version. `0.5.4-preview` added `DisplayWeave-Setup-<version>.exe` (per-user install/update, Settings > Apps entry, uninstaller) and `install.ps1 -Uninstall`; `scripts/release.ps1` runs the whole build/check/package/setup flow and `-Publish` creates the GitHub prerelease. App behavior as in `0.5.3-preview`: when the component does not load (e.g. Diablo IV), automatic handling fits a maximized window into its current region from outside; the **Maximize in region** rule does the same without any load attempt. Otherwise unchanged from 0.5.1. Default install root: `%LOCALAPPDATA%\DisplayWeave`. Since October 11 the engine is a separate UI-free library, `DisplayWeave.Core.dll` (`src/DisplayWeave.Core`); `RegionService` holds the app's actions for the window or a headless host (README, "Use the engine without the UI"). Administrator mode skips updates and starts helpers from a copy under `%ProgramFiles%\DisplayWeave` (not yet verified in a live elevated session). |
| Prototype app | **DisplayWeave Prototype 0.1.0-preview**: WinForms UI + session worker (`DisplayWeavePrototype.exe`), x64 presenter and display lease, display fixture script. Lets the user pick their own window, a region (saved legacy regions are listed), 30/60/90 s, view-only or keyboard/mouse control, Start/Stop and Restore window. Since October 11 the administrator step runs hash-checked copies from `%ProgramFiles%\DisplayWeavePrototype\sessions\<id>`, where display reports now live; verified unelevated only, so the next real session is its first live run. |
| Lease release ACK | Integrated. The lease publishes a manual-reset ACK event; the presenter (`--lease-release-begin-event/--lease-release-ack-event`) and its guardian share one atomic BEGIN claim, wait ≤750 ms for ACK, then restore focus/cursor. The worker sends BEGIN only when nobody else claimed it, and COMPLETE only after presenter exit, clean input restoration and the window leaving the virtual output. |
| Real-app entry | `--interactive-click-to-enter`: the projected image accepts a click (it is click-through otherwise); entry happens only when the source exactly fills the virtual output. The click is genuine user input to the presenter, which is what lets Windows grant the source focus without a focus-lock bypass. |
| Installer and package | `installer/install-prototype.ps1` installs from the extracted ZIP to `%LOCALAPPDATA%\DisplayWeavePrototype\versions\<ver>`, verifies `payload.sha256`, downloads the pinned upstream VDD asset (or takes `-DriverPackageDirectory`) and verifies archive/file hashes and SignPath signatures, writes `current.json` and a Start-menu shortcut. No startup entry, no legacy settings changes. The release ZIP contains **no driver**. |

## Evidence

Pure checks (both architectures unless noted): presenter 118, lease 219, plan/protocol 101, input 57 per architecture; prototype self-tests 35 (x64); managed 94 (0.5.3); native policy 225 + IPC; legacy installer 16; prototype installer 10 (with the local pinned driver; CI runs the 4 driver-independent cases).

Live sessions on this computer (7680 × 2160 primary, AMD RX 7900 XT), owned windowed `ProjectionProbe` standing in for the user's app, region `3000,300,1280,720`, 30 s requested:

| Run | Result |
| --- | --- |
| View-only (`test-results/prototype-session-20261010-135322-779`) | PASS. 2339 frames presented, 0 capture/present errors. Worker BEGIN acknowledged, COMPLETE accepted, physical state preserved since ready, window back at its exact original rectangle, device and config removed, existing `oem106.inf` preserved. |
| Interactive with Stop after 10 s (`…-135722-861`) | PASS. One tagged synthetic click entered; presenter claimed BEGIN, ACK observed (error 0), focus request accepted and returned, clip and cursor restored, no incomplete cleanup; 815 frames; lease release completed with no physical transition; window rectangle restored; full display cleanup. |
| Interactive harness failure (`…-135511-…`, session `c883e5d8…`) | Harness bug (wrong `INPUT` size) prevented the click and the harness closed the probe mid-session. The worker still completed release and display cleanup. Led to two fixes: the worker now ends a session as soon as the selected app closes, and reports that case accurately. |
| Repeat on final code (`…-140544-440`, `…-140705-589`) | PASS for both view-only (2340 frames) and interactive (817 frames, presenter BEGIN acknowledged, focus/clip/cursor restored) after the code-review fixes. |

Synthetic entry is not hardware input. The probe was windowed/borderless, not DXGI-exclusive, so these runs exercised no physical refresh transition.

**First real game: Diablo IV crashed during display setup** (details in [TESTING.md](../TESTING.md)). Adding the virtual output changed the primary display's refresh and the lease restored it with `SetDisplayConfig`; the game died right after, before its window was touched. Minidumps place that crash, and a later one with legacy 0.5.1 and a third-party display tool, inside the Discord overlay (`DiscordHook64.dll`) during Present after a resolution/output change. Cleanup completed. In the 0.5.1 run Diablo IV filled the whole monitor because `RegionHook.dll` never loaded into it (the third-party tool's DLL did); a clean retry showed no Windows mitigation policy blocking it, so the game itself refuses the DLL. Do not try to get past that. The user plays Diablo IV **windowed** and maximizes it from the title bar, so legacy **0.5.3-preview** fits such maximized windows from outside automatically (the user wanted no per-app rules), plus a **Maximize in region** rule that never attempts a load (see [TESTING.md](../TESTING.md#version-052-maximize-in-region)). It still needs a Diablo IV test by the user. On 2026-10-10 the project was renamed DisplayWeave and its history was squashed into one commit; nothing is published under the new name yet. CI's `release` job publishes `v0.5.5-preview` from `main` (manual route: `scripts/release.ps1 -Publish`). The development computer still has a pre-rename 0.5.4 build with no updater: install the DisplayWeave setup by hand and exit that build first, so two copies never control the same windows. The first automatic update can be observed from 0.5.5 to 0.5.6. Do not resume prototype work without a new request.

## Known limits (also in the [user guide](prototype-guide.md))

- One session per lease (≤120 s total; projection gets what remains after setup minus a 15 s cleanup reserve). Release ends the session; no re-entry.
- Virtual output is fixed at 1280 × 720 @ 60 Hz SDR; aspect-fit letterboxing into other regions.
- Alt+Tab/Windows key while controlling can make an exclusive app leave fullscreen before BEGIN reaches the lease; cleanup still runs but the lease reports failure.
- Admin-elevated apps cannot be moved (UIPI). The return-focus window passed by the UI must be on the region's output; otherwise entry fails with a message.
- Display setup takes ~25–30 s on this computer, mostly `Get-WindowsDriver` in the elevated fixture.
- A worker crash leaves restoration to the **Restore window** button (`window.json` snapshot); the lease removes its display at its deadline regardless.

## Where things live

```text
Legacy install: %LOCALAPPDATA%\DisplayWeave (versions, data, current.json)
Prototype install: %LOCALAPPDATA%\DisplayWeavePrototype\versions\0.1.0-preview, current.json
Prototype sessions: %LOCALAPPDATA%\DisplayWeavePrototype\sessions\<id> (request, status, window snapshot, presenter and display reports)
Pinned driver (dev): .tools\research\vdd-signed-24.12.24\package
```

Remote: [alexchaoss/DisplayWeave](https://github.com/alexchaoss/DisplayWeave). Release tags: app `v*`, prototype `prototype-v*`. Before the rename, GitHub Actions could not start jobs because of account billing; check the latest run before treating CI as passing.

## Proposed next steps

1. Have the user run their own app through the installed prototype and record what happens (exclusive vs borderless, focus, input feel). Physical keyboard/mouse remain unverified.
2. Re-entry after release within one lease, and longer sessions (needs a lease that is renewed or released per interaction instead of once).
3. Region-sized virtual modes instead of fixed 1280 × 720, with validation that the applied mode matches.
4. A production IDD/broker replacing the upstream VDD fixture (see [architecture](virtual-display-architecture.md)).
