# DisplayWeave development runbook

Start with [AGENTS.md](../AGENTS.md) and the [dated handoff](agent-handoff.md). This runbook separates compilation from actions that affect the desktop. Commands run from the repository root on Windows 10/11 x64 unless stated otherwise.

## Repository layout

| Folder | Contents |
| --- | --- |
| `src/DisplayWeave.Core` | Legacy region engine, `DisplayWeave.Core.dll`: layout, rules, window listing, controller and the `RegionService` facade. Compiled without any UI reference. |
| `src/DisplayWeave` | Legacy WinForms app over the engine (`version.txt`, `release.json`, `app.manifest`); managed checks under `Tests/`. |
| `src/DisplayWeave.Prototype` | Separate projection prototype: UI, session worker, Win32 helpers and pure self-tests; its own `version.txt`. Reuses only `src/DisplayWeave.Core/Layout.cs`. |
| `native/legacy` | Legacy hook host, RegionHook DLL, fullscreen probe and protocols; loader fixtures under `tests/`. |
| `native/projection` | Presenter, input session, display lease, probe, layout/protocol models and their pure tests. |
| `scripts` | `fetch-tools`, `build`, `build-native`, `build-projection`, `build-prototype`, `package`, `package-prototype`. |
| `installer` | `install.ps1` (legacy) and `install-prototype.ps1`; each ships at the root of its own package. |
| `tests` | Installer regression checks and all live fixtures (`test-*.ps1`). |
| `third_party`, `docs` | Vendored MinHook; documentation. |

Scripts locate the repository from their parent folder, so run them from anywhere. `dist`, `release`, `.tools` and `test-results` are ignored local outputs.

## Source map

| Concern | Files and existing behavior |
| --- | --- |
| Legacy entry and lifecycle | `src/DisplayWeave/Program.cs`, `AppOptions.cs`, `ElevationRestart.cs`, `StartupRegistration.cs`, `Updater.cs`: single instance, settings location, explicit elevation, sign-in startup and release updates through `gh`. |
| Legacy interface | `MainForm.cs`, `MainForm.Interface.cs`, `RulerCanvas.cs`: tabs, ruler editor, app picker, status and tray. Closing X hides to tray; Exit stops handling. Actions call `RegionService`; keep business logic out of the form. |
| Layout and rules | `src/DisplayWeave.Core`: `Layout.cs`, `RulerLayout.cs`, `ApplicationRules.cs`, `AutomaticRegionPolicy.cs`: non-overlapping grid, saved identities, rule precedence and automatic overlap selection. |
| Window/controller ownership | `WindowManager.cs` lists eligible windows; `DisplayMonitor.cs` reads monitors; `FullscreenController.cs` manages the legacy native component; `RegionService.cs` loads/saves settings and exposes the app's actions to any host. Do not pull the controller into a non-injecting prototype. |
| Legacy native code | `native/legacy/HookHost.cpp`, `RegionHook.cpp`, `RegionProtocol.h`, `RegionLayoutProtocol.h`, `VirtualMonitorModel.h`: delivery, resident-hook reconnection and USER32 monitor/window compatibility. `ShellPolicy.h` and `CompatibilityPolicy.h` contain exclusions. |
| Prototype app | `src/DisplayWeave.Prototype/PrototypeApp.cs` (UI, entry modes `--worker`, `--self-test`), `PrototypeSession.cs` (request/status model, policy, worker, recovery), `PrototypeNative.cs` (window list, snapshot/move/restore, release events), `PrototypeTests.cs`. |
| Projection rendering | `native/projection/RegionPresenter.cpp`, `ProjectionCursor.h`, `ProjectionPolicy.h`: DDA/D3D11, crop/aspect fit, cursor shader, CLI and measurements. |
| Projection interaction | `ProjectionInput.h/.cpp`, `ProjectionInputTests.cpp`: source identity, modal entry, focus/cursor/clip restoration and separate guardian. |
| Display fixture | `native/projection/VirtualDisplayLease.cpp`: preflight, exact PnP ownership, physical-state checks and bounded two-phase release. Compiles to `RegionDisplayLease.exe`. |
| Owned application fixture | `native/projection/ProjectionProbe.cpp`: colors, DXGI exclusive-state samples and test input counters. It is not a launcher for a user's app. |
| Proposed layout protocol | `ProjectionPlan.h`, `ProjectionProtocol.h`, `ProjectionPlanTests.cpp`: pure model validation, no live broker or IDD handler. |
| Managed checks | `src/DisplayWeave/Tests/LayoutTests.cs`, `RuleTests.cs`, `AutomaticRegionTests.cs`, `LifecycleTests.cs`; `AutomaticControllerIntegration.cs` has separate live fixture integration. |
| Packaging and CI | Legacy: `scripts/package.ps1`, `installer/install.ps1` (install and `-Uninstall`), `tests/installer-tests.ps1`; setup: `installer/setup/Setup.cs`, `scripts/build-setup.ps1`, `tests/setup-tests.ps1`; full flow: `scripts/release.ps1`. Prototype: `scripts/build-prototype.ps1`, `scripts/package-prototype.ps1`, `installer/install-prototype.ps1`, `tests/prototype-installer-tests.ps1`. CI: `.github/workflows/build.yml`. |

## Build and checks without desktop interaction

The C# compiler comes with Windows .NET Framework at `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`; a modern .NET SDK is not required. `fetch-tools.ps1` downloads pinned Zig 0.13.0 and verifies its archive checksum. It does not acquire the upstream display driver.

Inspect the working tree first and preserve local work. The lease ACK integration that was an uncommitted patch in the morning snapshot is now committed with the prototype; see the [handoff](agent-handoff.md) for the tested state.

```powershell
# Once per new clone, when .tools does not already contain the compiler:
.\scripts\fetch-tools.ps1

# New projection tools and pure tests; no display or input sessions:
.\scripts\build-projection.ps1 -Architecture all -RunSelfTests

# Legacy managed application and native components:
.\scripts\build.ps1
.\scripts\build-native.ps1 -Architecture all
```

Projection output is `dist\projection-x64` and `dist\projection-x86`. An optional `-OutputDirectory` changes the common output root. The known baseline is 118 presenter, 219 lease, 101 plan/protocol and 57 input checks per architecture (111/49 before the lease release gate and click-to-enter options). A changed count needs explanation, not an automatic update of the claimed baseline.

Run managed checks by waiting for the GUI-subsystem process and inspecting both exit status and reports, as CI does:

```powershell
$reportDirectory = Join-Path $PWD 'test-results\manual'
New-Item -ItemType Directory -Path $reportDirectory -Force | Out-Null
$managedReport = Join-Path $reportDirectory 'managed.txt'
$managedTest = Start-Process -FilePath .\dist\DisplayWeave.exe `
    -ArgumentList @('--self-test', ('"{0}"' -f $managedReport)) `
    -WindowStyle Hidden -Wait -PassThru
if ($managedTest.ExitCode -ne 0) { throw 'Managed checks failed.' }
Get-Content (Join-Path $reportDirectory 'managed*.txt')
foreach ($architecture in @('x64', 'x86')) {
    & ".\dist\native-v2-$architecture\RegionHookHost.exe" --self-test-policy
    if ($LASTEXITCODE -ne 0) { throw 'Native policy checks failed.' }
    & ".\dist\native-v2-$architecture\RegionHookHost.exe" --self-test-ipc
    if ($LASTEXITCODE -ne 0) { throw 'Native IPC checks failed.' }
}
```

Managed self-test writes four reports (layout, rules, automatic, lifecycle). Require each passing summary and no FAIL lines; CI also checks the total. Policy/IPC self-tests do not attach to applications. See [the workflow](../.github/workflows/build.yml) for its complete assertions.

Read-only projection diagnostics:

```powershell
.\dist\projection-x64\RegionPresenter.exe --list-outputs
.\dist\projection-x64\RegionDisplayLease.exe --preflight
.\dist\projection-x64\RegionDisplayLease.exe --preflight-refresh-aware
.\dist\projection-x64\RegionDisplayLease.exe --validate-current
```

`--validate-current` asks Windows to validate, not apply, the observed configuration. `RegionPresenter.exe --self-test-cursor-gpu` is a separate hardware-GPU test of tiny owned textures with readback; it creates no window or desktop capture and is not part of `-RunSelfTests`.

## Tests that affect the desktop

Read the [prototype fixture instructions](projection-prototype.md#temporary-virtual-display-fixture) before running these. They are not required for documentation changes. Keep the user informed when a requested test will take focus, confine the pointer or create a display.

| Script | Effects |
| --- | --- |
| `test-projection.ps1 -Architecture all` | Opens and captures owned windows on an existing output; does not add a display or exercise exclusive fullscreen/input. |
| `test-virtual-display.ps1` | Requires elevation and an extracted pinned driver. Creates a temporary config/device, validates physical state and later removes only what it owns. |
| `test-virtual-projection.ps1 -LeaseReport <absolute lease.json>` | Consumes that exact ready lease, opens its owned exclusive source and presenter, coordinates release and verifies restoration. |
| `test-monitor.ps1`, `test-handoff.ps1`, `test-loader-failure.ps1`, `test-compatibility.ps1`, `test-native.ps1` | Legacy interactive/native-delivery fixtures. Inspect their parameters and isolation guards first; not interchangeable with the projection checks. |

For virtual-output tests:

1. Confirm DisplayWeave has exited through **Exit DisplayWeave**; X only hides it. Check again for Diablo IV and other active fixtures. Current scripts reject any running DisplayWeave or Diablo IV. Do not kill user applications or disable foreign services to satisfy a guard.
2. Supply the original pinned VDD package: upstream release `25.5.2`, asset `Signed-Driver-v24.12.24-x64.zip`. Exact file hashes and publisher checks live in `test-virtual-display.ps1`. The fixture refuses an existing vendor config/device. Use `-UseExistingPinnedPackage` only when the exact already-staged package is verified and must be preserved. Never change signature enforcement.
3. Run the display fixture in an elevated PowerShell session, allowing normal UAC if needed. When launching a background helper use `Start-Process -Verb RunAs -WindowStyle Hidden`. `-Seconds 120` gives the native lease its maximum lifetime; inventory work adds time outside that interval. Do not reuse an old report folder.
4. Wait for this run's `lease.json.ready.json`. Pass the absolute **base** `lease.json` path to the projection harness. Verify ready state, process lifetime/path and exact created device; the harness performs its own identity checks. Do not select a stale journal merely because it has the newest timestamp.
5. Run a bounded projection case. `-Seconds` accepts 3–30 and architecture x64/x86. `-Interactive` takes modal focus/pointer confinement. `-ExerciseSyntheticInput` and `-ExerciseReleaseShortcut` require it. `-ExerciseGuardianRecovery` also requires at least six seconds and cannot accompany those synthetic flags. All are owned-fixture modes.
6. Read both projection and elevated fixture results after cleanup. Require actual native lease exit 0, no incomplete input restoration, owned output/config removal, preserved pre-existing driver and matching physical baseline. A successful renderer alone is not a successful test.

Existing examples with a separately started, verified lease:

```powershell
# Elevated terminal; supply the actual extracted package path:
.\tests\test-virtual-display.ps1 -DriverPackageDirectory 'C:\path\to\pinned-package' `
    -BuildDirectory .\dist -Seconds 120 -UseExistingPinnedPackage

# Ordinary second terminal, while that exact lease is ready:
.\tests\test-virtual-projection.ps1 -LeaseReport 'C:\absolute\run\lease.json' `
    -BuildDirectory .\dist -Architecture x86 -Seconds 6 `
    -Interactive -ExerciseSyntheticInput -ExerciseReleaseShortcut
```

The first command waits through the lease, so the second must run concurrently in a different terminal/process. Omit `-UseExistingPinnedPackage` when the package is absent. Scripts currently place mutable reports under the checkout; they are not ready to run unchanged from an immutable installed version folder.

## Release and recovery rules

The active physical guard is strict. Before deliberately ending exclusive operation or returning focus, the harness signals private BEGIN and waits for the release journal acknowledging a bounded five-second release. Only the known physical path-refresh transition is tolerated, with the target signal and other physical invariants checked. COMPLETE follows confirmed presenter/input/source cleanup. Device removal must restore the exact original physical topology.

The lease also publishes a manual-reset ACK event after its durable BEGIN journal. Given `--lease-release-begin-event`/`--lease-release-ack-event`, the presenter and its guardian share one atomic BEGIN claim (the lease rejects a repeated BEGIN), wait at most 750 ms for ACK, and only then restore focus and cursor; a missing ACK is reported, never allowed to block input restoration. The prototype worker sends BEGIN only when no one claimed it and the ACK is still clear. `tests/test-virtual-projection.ps1` still uses the journal and does not pass the events.

On failure, use this session's journals and exact process/PnP identities. Allow conditional input restoration and handle-bound device cleanup, then verify the final state independently. Preserve a failure even when cleanup succeeds. Do not use blanket display resets, global work-area changes, arbitrary registry edits, forced game termination, foreign hook unloading or removal of pre-existing packages as cleanup shortcuts. If ownership cannot be verified, report the specific unresolved state rather than guessing a device to remove.

## Traps already investigated

- **Physical refresh representations differ.** On the development host, DXGI/legacy view showed 120 Hz; refresh-aware QueryDisplayConfig (`0x52`) reported a 120 Hz path and 240 Hz target signal simultaneously. BOOST_REFRESH_RATE was unset. This is not proof of DRR or changed physical scanout. Keep one consistent query representation across each lease.
- **Requested VDD mode is not applied mode.** The XML requested 1280 x 720; the arriving source initially had an 800 x 600 desktop with a 1280 x 720 target. The validated startup transaction normalizes the owned virtual desktop/image metadata while restoring the original physical path refresh. Validation failure must prevent apply. Do not add ALLOW_CHANGES or SAVE_DATABASE to make a test pass.
- **Physical state can change during controlled release.** `physicalPreservedSinceReady:false` can accompany an accepted release and final restoration. Never describe that as unchanged throughout the session.
- **Connector labels do not identify ownership.** This virtual driver advertised HDMI. Use exact PnP ancestry. `oem106.inf` was the pre-existing pinned package on this computer only, not a cleanup target or portable constant.
- **PowerShell event overload binding was wrong.** `EventWaitHandle.OpenExisting(name, rights)` selected an unintended overload in PS7. The working harness uses Win32 `OpenEventW(EVENT_MODIFY_STATE)`, `SetEvent`, `CloseHandle`; it was checked in PS5.1 and PS7.
- **External process ExitCode can be null.** An externally observed/elevated lease returned null through `Get-Process`. The harness retains the verified native process handle and reads real termination status. Never convert null to zero.
- **Focus restoration can be denied.** One ordinary SetForegroundWindow request plus bounded observation is intentional. Do not force focus through unrelated threads or claim universal guardian recovery. A live presenter hung inside cleanup is a remaining limitation because simultaneous global-cursor restoration would race.
- **SDR fixture conversion is not HDR support.** Ordinary projection supports SDR. The opt-in owned physical fixture converted FP16 scRGB using the observed SDR white level (240 nits on this host); no general HDR preservation was validated.

## Packaging and repository updates

```powershell
# Requires the legacy managed and both native builds above:
.\scripts\package.ps1
.\tests\installer-tests.ps1 -OutputDirectory .\test-results\installer-check
```

`version.txt` and `release.json` must agree. Packaging uses an allowlist and generates ZIP/checksum output under `release`; it does not include the new projection backend. Installer regression checks use temporary fixtures without GitHub calls or application launches; the only registry write is a temporary HKCU RunOnce value in the in-use uninstall case, restored afterwards. Publishing a release is distinct from pushing source; do not imply one accomplished the other.

`install.ps1` supports private GitHub releases via authenticated `gh` or a local ZIP/checksum. Default legacy root is `%LOCALAPPDATA%\DisplayWeave`, with `versions`, `data` and `current.json`. `-Startup Preserve -NoLaunch` preserves startup and does not launch the app. Keep old version folders while applications may retain their DLLs. See [README install guidance](../README.md#install-or-update) for migration and elevated-instance behavior.

### Setup and release flow

`installer/setup/Setup.cs` builds `DisplayWeave-Setup-<version>.exe` (C# 5, `csc`, no extra tools). It embeds the portable ZIP, its `.sha256` and `installer/install.ps1`; all file work stays in that script, including `-Uninstall [-RemoveSettings]`. The setup only adds a window, the HKCU `Uninstall\DisplayWeave` entry and the `Uninstall DisplayWeave.exe` copy in the install root. Non-default roots (`--install-root`, used by tests) are never registered. Its manifest is `asInvoker`, which also stops Windows' installer detection from prompting for elevation. It is unsigned.

**Publishing is automatic:** bump `src/DisplayWeave/version.txt` and `release.json`, then merge to `main`. The `release` job in `.github/workflows/build.yml` runs after the Windows build job passes. If `v<version>` has no release yet, it publishes the ZIP, the setup and their `.sha256` files that the build job tested, as a prerelease with generated notes. A push to `main` with an already released version publishes nothing. Locally:

```powershell
# Everything except publishing: build, managed/native checks, package, installer and setup tests.
.\scripts\release.ps1
# Manual fallback for CI: after committing and pushing the version bump, the same, then the GitHub prerelease v<version>.
.\scripts\release.ps1 -Publish -NotesFile .\path\to\notes.md
```

**Updates:** `src/DisplayWeave/Updater.cs` runs only in an installed copy (`<root>\versions\<version>\` next to `<root>\current.json`). A minute after start and every six hours, it runs the user's `gh`, which keeps the GitHub credentials for the private repository:
- `gh release list` reads the tags. `prototype-v*` tags are skipped and the highest `v<version>` wins.
- `gh release download` fetches `DisplayWeave-Setup-<version>.exe` and its `.sha256` into `%TEMP%\DisplayWeaveUpdate`.
- `MainForm` then starts `--quiet --install-root <root> --log <setup>.log`, but only while its window is hidden and `FullscreenController.HasExpandedApp` is false. That property is true while any app is maximized or fullscreen in a region, or hasn't yet reported its state. The setup closes the app through the existing `--exit-for-update` path and relaunches the new version.
- A release without a setup asset (any release before 0.5.5) makes the check fail with "no assets match", so every app release must carry the setup.

`release.ps1` builds native files into `release\build-<version>` instead of `dist`, because running apps can keep a `dist` DLL loaded (Chrome did on the development computer). `tests/setup-tests.ps1` runs the real setup quietly against a temporary root and checks that Settings > Apps, startup and the Start menu stay unchanged. A version folder is immutable once installed, so any package change needs a new version. ZIPs are not byte-reproducible, so a version installed from a local build cannot later be installed from its published rebuild on the same computer; bump the version before publishing in that case. Do not time a setup that relaunches DisplayWeave with Windows PowerShell's `Start-Process -Wait`: it also waits for the relaunched app.

For prereleases, use `gh release list`; an unqualified `gh release view` can report no release when only prereleases exist. Never print authentication tokens or include personal layout/rule files or raw local reports in a commit. Stage explicit intended paths when unfinished source is present. After a docs-only commit, verify the remaining source diff is unchanged, then push and verify the remote commit.

When handing off again, update the tested commit, implemented/proposed distinction, remaining changes, installation reality and test limitations. This lets the next agent continue from evidence without repeating risky desktop experiments.
