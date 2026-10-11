# DisplayWeave validation

Commands recorded below before October 10, 2026 use the old root-level script paths. Since the repository reorganization, build/package scripts live in `scripts\` and fixtures in `tests\` (for example `.\scripts\build-projection.ps1`, `.\tests\test-monitor.ps1`); their parameters are unchanged.

## Isolated projection prototype

The next backend has separate native tools; it does not change the installed
0.5.1 application. Build and run its non-interactive checks with:

```powershell
.\build-projection.ps1 -Architecture all -RunSelfTests
```

At the tested revision, both architectures pass 111 presenter, 219 lease,
101 layout/protocol, and 49 input policy checks. These include cursor mask decoding, coordinate bounds, production
shader compilation on the CPU, conditional input restoration and cleanup races;
no window, capture, display device or input session is created by the checks.

### Prototype 0.1.0-preview (October 10, 2026)

Pure checks after the lease ACK integration: 118 presenter, 219 lease, 101
layout/protocol and 57 input checks on x64 and x86; 35 prototype self-tests;
legacy managed 92, native policy 225 + IPC, legacy installer 16 and prototype
installer 10 (pinned driver supplied locally).

Live, owned windowed `ProjectionProbe` as the selected app, region
`3000,300,1280,720`, `.\tests\test-prototype-session.ps1 -Seconds 30`:

| Run | Result |
| --- | --- |
| `prototype-session-20261010-135322-779`, view-only | PASS: 2339 presents, 0 errors; worker BEGIN acknowledged, COMPLETE accepted; physical state preserved since ready; window rectangle `200,200,1160,740` restored exactly; device/config removed; existing `oem106.inf` preserved. |
| `prototype-session-20261010-135511-883`, interactive | Harness FAIL: malformed tagged click (`INPUT` size); harness closed the probe mid-session. Worker still completed release and display cleanup. Fixed harness; worker now ends early when the app closes. |
| `prototype-session-20261010-135722-861`, `-Interactive -StopAfterSeconds 10` | PASS: one tagged click entered; presenter claimed BEGIN, ACK observed (error 0); focus returned, clip and cursor restored, no incomplete cleanup; 815 presents; lease release completed with no physical transition; window restored; full display cleanup. |

After code-review fixes (status-file sharing, frame/edge-style handling,
guardian BEGIN wait, close-while-running), both runs were repeated on the final
code and passed: `prototype-session-20261010-140544-440` view-only (2340
presents, 0 errors) and `prototype-session-20261010-140705-589` interactive
(817 presents; presenter BEGIN acknowledged; focus, clip and cursor restored).
Both preserved the physical state since ready, restored the window rectangle
exactly and removed the device and configuration.

The click is synthetic and the probe was borderless rather than exclusive, so
hardware input and exclusive-mode release transitions remain unverified for the
prototype.

First real game, installed 0.1.0-preview (session
`26b37968015342459378cb7232f55650`): Diablo IV running, interactive, 90 s,
region `3200,720,1280,720`. **FAIL: the game crashed during display setup**,
before its window was touched (no `window.json`, presenter never started). When
the virtual output arrived, Windows changed the 120 Hz primary's refresh
(`before=12000/100, observed=240`, 9 mismatches over 1984 ms); the lease's
startup refresh-restore `SetDisplayConfig` then succeeded (apply error 0). The
worker found the game gone and ended with "closed or changed before it could be
moved". Cleanup was complete: device and configuration removed, physical state
restored, `oem106.inf` preserved. No WER/Application Error event was logged.
The game's NVIDIA Streamline minidump (`C:\ProgramData\NVIDIA\Streamline\Diablo IV\1791657083111377`)
shows an access violation reading past a buffer in the Discord overlay,
`DiscordHook64.dll+0x10072b`, during Present after the output change.

The same fault ended a later Diablo IV run (15:05, legacy 0.5.1 and a third-party display
tool both running) right after the game switched its window to 7680 × 2071: Streamline
dump `1791659150504566`, then Present `E_ABORT` and Blizzard fatal `0xC0000025`.
`RegionHook.dll` was not in that process's module list; the other tool's DLL was.
Neither DisplayWeave component was involved in either fault.

Repeat with legacy 0.5.1, Discord closed, Diablo IV at medium integrity (PID
1496, 15:21): `LOADER_MODULE_NOT_OBSERVED`, thread result 0, target running,
`RegionHook.dll` copies 0, initialization not invoked. Read-only
`Get-ProcessMitigation` showed MicrosoftSignedOnly, image-load and dynamic-code
policies all OFF, so no Windows process mitigation rejected the load; the game
itself refuses the DLL. The third-party display tool's DLL was loaded.
No workaround was attempted.

An additional passive demo on October 10 passed for 30 seconds:
`test-results/virtual-projection-20261010-082440-062` recorded 2522 presents,
30 exclusive-state samples, matching fixture colors and restored source state.
Its paired `virtual-display-20261010-082413-101` fixture accepted controlled
release, observed native exit 0, removed its own device/configuration, preserved
the pre-existing package and confirmed exact final physical topology. There were
no cleanup errors. It exercised no interaction or physical input.

`RegionPresenter.exe --self-test-cursor-gpu` is a separate hardware-GPU diagnostic
using tiny owned textures and the production shader. It creates no window,
desktop capture, input session or display; its readback validates pointer shader
output rather than DDA metadata delivery or real hardware input.
The recorded offscreen GPU run `test-results/cursor-gpu-20261009-224911-959`
passed on x64 and x86: seven cases, 224 checked pixels, seven readbacks, no
failures, and 203 ms per run on the RX 7900 XT. Separate DDA pointer delivery
has not been observed in the live runs.

After exiting DisplayWeave, `test-projection.ps1 -Architecture all` exercises
GPU capture of an owned window. The recorded run verified four color patches
and four gray levels, zero failed presents, and unchanged display inventory on
both architectures. A third-party display tool's resident components were observed
in the test sources.
The timing measures presentation submission, not end-to-end game latency. A fresh
passive regression with the cursor-capable presenter also passed both architectures
in `test-results/projection-20261009-224404-261`: 705/686 successful presents,
zero errors, and eight exact color/gray matches. No separate pointer was visible,
so the cursor shader and input session were not exercised by that run.

The temporary virtual-display test is a separate, elevated fixture with explicit
driver-package verification and cleanup. See the [prototype report](docs/projection-prototype.md)
for commands, measured results, and the physical-state guard's live outcome.
An earlier lease attempt, `test-results/virtual-display-20261009-224224-747`,
failed physical-refresh validation with Windows error 31 before any apply call.
A later validated startup correction reached readiness. The passive virtual-output
run `test-results/virtual-projection-20261009-225525-609` then captured, released
and presented 468 frames in six seconds, matched all eight color/gray patches,
and recorded five samples of the owned source reporting exclusive fullscreen.
Rendering had no errors or recoveries. The overall run failed when source exit
changed the physical path refresh and triggered the then-strict active guard;
the paired lifecycle fixture removed its device/configuration and restored the
physical baseline. This is rendering evidence, not a complete lifecycle pass.

The presenter now composes color, monochrome and masked-color cursors on the GPU.
`test-virtual-projection.ps1 -Interactive` enables modal hardware-direct input for
its owned probe after verified readiness; Ctrl+Alt+F10 releases it. Optional
`-ExerciseSyntheticInput` validates only tagged synthetic fixture events, never
physical-device delivery. An input guardian and conditional restoration are
implemented. In `test-results/virtual-projection-20261009-230540-413`, active
readiness and exclusive fullscreen were observed together; the owned source
recorded tagged key-down/key-up and a relative mouse event with delta `(4, 2)`.
Its raw-input counters also advanced, but synthetic events do not prove physical
device delivery. The overall run failed at release because of a harness event-API
binding error; cursor and clip restored, while focus restoration failed. The
harness and bounded focus acknowledgment have since been corrected. The next run,
`test-results/virtual-projection-20261009-231155-841`, acknowledged BEGIN, captured
463 frames and presented 462 with eight exact patches and six exclusive samples.
Windows explicitly rejected the automatic focus-return request, so input cleanup
remained incomplete and COMPLETE was withheld. The lease timed out its release
window and observed the original topology after closing; the elevated lifecycle
fixture removed its device/configuration and verified physical restoration.
Registered-shortcut run `test-results/virtual-projection-20261009-231619-853`
then verified tagged keyboard/mouse input, four exclusive samples, 318 captured
and 317 presented frames, eight exact patches, and restored focus/cursor/clip with
reason `release_shortcut`. Its presenter and paired native/elevated lease fixture
(`test-results/virtual-display-20261009-231543-315`) passed: COMPLETE acknowledged,
owned device/configuration removed, existing package preserved, and original
physical topology restored. The projection harness still marked the run failed
at its exit-status check. With that check corrected, the x86 automated projection
sequence `test-results/virtual-projection-20261009-231943-361` passed: 316 frames
captured/released/presented, four exclusive samples, eight exact patches, verified
synthetic input and registered-shortcut release, all input state restored, COMPLETE
accepted, native exit code zero, owned virtual output removed, and matching final
physical inventory/topology. It had no rendering errors or recoveries. The detailed
[prototype controls and report fields](docs/projection-prototype.md#cursor-and-modal-input-controls)
include the direct presenter CLI.

The x64 guardian-stall sequence `test-results/virtual-projection-20261009-232049-844`
also passed. Its requested 3500 ms heartbeat stall lasted 3516 ms; the guardian
restored focus, cursor and clip with no incomplete cleanup. It recorded two
exclusive samples during interaction, nine captured/released frames, eight presents
and eight exact patches with no rendering errors. COMPLETE, native exit zero,
virtual-output removal and exact physical restoration passed. It exercised no
synthetic keyboard/mouse activity before the stall, so it does not establish
recovery after a different input/focus history or a process crash.

Both paired elevated lifecycle fixtures, `virtual-display-20261009-231910-148`
and `virtual-display-20261009-232022-290`, passed device/configuration cleanup,
preserved the existing pinned package, and confirmed original physical topology
with no cleanup errors. Windows rejected automatic deadline focus return in the
earlier test; successful shortcut and guardian cases do not guarantee all future
foreground requests.

`-ExerciseReleaseShortcut` requires `-Interactive` and can accompany
`-ExerciseSyntheticInput`. After BEGIN acknowledgment it sends a tagged
Ctrl+Alt+F10 sequence to the owned fixture and requires the real registered-hotkey
handler to report `release_shortcut`, with accepted/restored focus and restored
cursor/clip. It does not post a fake hotkey message or prove physical key delivery.

The coordinated release protocol requires BEGIN acknowledgment before input
cleanup or source fullscreen exit, and COMPLETE after both owned processes finish
cleanup. Its non-renewable five-second window permits only the specified physical
path-refresh transition to an unchanged physical target signal; other physical
invariants remain checked. Success requires the exact original physical topology
after removing the owned virtual display, plus the lifecycle fixture's device,
package and configuration checks. It does not claim unchanged state throughout
startup/release or an unchanged inventory while adding/removing an output.

Optional `-ExerciseGuardianRecovery` requires `-Interactive`, at least six seconds,
and neither synthetic-input nor release-shortcut exercise. It requests an owned
presenter heartbeat stall and requires reported guardian recovery plus cursor,
clip and focus restoration;
the recorded x64 case above passed. Hardware input, live separate-cursor delivery,
crash recovery and game compatibility remain unverified. The legacy 0.5.1
application is stopped for these tests; the prototype is not integrated into it.

## Version 0.5.2 Maximize in region

Adds a rule action that never attaches the native component: while a matching
window is maximized, the controller moves it with `SetWindowPos` so its DWM
extended frame equals the region, keeping the window's own invisible-border
overhang. Retries are spaced 2 s apart and stop after three moves. Rule removal,
release, pause and exit return a still-maximized window to its monitor's work area.

- Managed checks: 94 (layout 18, rules 39, automatic 13, lifecycle 24), including
  decision precedence, rule round trip and the overhang geometry.
- Native sources are byte-identical to 0.5.1; binaries were rebuilt from the new
  `native/legacy` path (embedded paths differ). Policy 225 cases and IPC passed on
  x64 and x86.
- Live owned-window check (scratch harness linking the production controller,
  DPI-aware WinForms tool window in a separate process, 7680 × 2100 work area,
  middle region `1920,0,3840,2100`): unmaximized window left alone; maximized
  window's frame fitted exactly to the region (window rect `1911,-9,3858,2118`);
  rule removal, pause and resume returned it to `0,0,7680,2100` and back;
  un-maximizing restored the exact normal rectangle `300,300,900,600`. The tool
  window maximized over the taskbar before fitting, unlike an ordinary window, so
  the harness's "Windows maximized to the work area" precondition failed; it does
  not affect the fit. The first attempt used a DPI-unaware PowerShell form that the
  user's running 0.5.1 attached to automatically, which invalidated that run.
- Not yet verified with Diablo IV or any real game.

## Rename to DisplayWeave

The project is now DisplayWeave. Probes and test scripts no longer look for a
specific third-party tool's DLL; run fixtures with other display tools exited.
Native binaries are linked with `-s`, so they carry no debug information or
local build paths. `package.ps1` retries replacing its outputs for up to 15
seconds, because antivirus scanning briefly locked the new archive on the
development computer.

- `scripts/release.ps1`: managed and native checks passed, installer tests 20
  passed, setup tests 6 passed.
- `build-projection.ps1 -RunSelfTests`: presenter 118, lease 219, plan 101 and
  input 57 checks on both architectures. Prototype self-tests 35, prototype
  installer tests 10.
- The app ZIP, setup and prototype ZIP contain no local paths.
- Live display and input tests were not rerun after the rename.

## Version 0.5.5 automatic updates and CI release

Adds `src/DisplayWeave/Updater.cs` (update check through the user's signed-in `gh`)
and a `release` job in `.github/workflows/build.yml`. Region handling is unchanged
from 0.5.3.

- Managed checks: 97. New cases cover choosing the highest `v<version>` tag while
  skipping `prototype-v*` tags, never offering the current or an older version, a
  final release replacing its own preview, refusing an unparsable current version,
  detecting only installed copies (`versions\<version>` beside `current.json`), and
  setup checksum verification (match, changed file, empty checksum file).
- Live `gh` check against the private repository (the scratch harness links
  `Updater.cs` and installs nothing). With current `0.5.5-preview`: no update.
  With current `0.5.0-preview`: it picked `v0.5.1-preview` over
  `prototype-v0.1.0-preview`, and the download failed with "no assets match"
  because releases before 0.5.5 have no setup asset.
- `scripts/release.ps1` (no publish) at 0.5.5: managed and native checks passed,
  installer tests 20 passed, setup tests 6 passed.
- Not run: the in-app hand-off (download, wait until idle, quiet setup, relaunch),
  because it needs a published newer release. The installed 0.5.4 has no updater,
  so the first real automatic update can be observed from 0.5.5 to 0.5.6. The
  `release` workflow job has not run yet; it runs on the first push to `main`
  that contains it.

## Version 0.5.4 setup and uninstall

Adds `DisplayWeave-Setup-<version>.exe` (built by `scripts/build-setup.ps1`) and
`install.ps1 -Uninstall [-RemoveSettings]`. App behavior is unchanged from 0.5.3.

- `tests/installer-tests.ps1`: 20 passed. New cases: uninstall removes program
  files, pointer, launcher and root shortcut while keeping `data`;
  `-RemoveSettings` removes the whole folder; a folder without an installation is
  refused untouched; a file held open (standing in for a DLL another app still has
  loaded) is left and a HKCU RunOnce cleanup is scheduled, then the test restores
  RunOnce. The first run of that case failed because the test root's long path
  exceeded the 260-character RunOnce limit while the script still promised a
  cleanup; the command now drops the folder removal when needed and the message
  says when nothing could be scheduled.
- `tests/setup-tests.ps1`: 6 passed, quiet mode against temporary roots: install
  with the uninstaller copy, repeat install, uninstall from the installed copy
  (settings kept, uninstaller deleted itself), full removal with
  `--remove-settings`, uninstall with nothing installed, and Settings > Apps /
  Run / RunOnce / Start menu unchanged.
- Not covered by automation: the interactive window and registration in Settings >
  Apps, which happen only for the default root.
- Interactive window, checked by screenshot at 125% scaling without clicking:
  install, update (detected 0.5.3) and uninstall layouts. The first build kept
  96-DPI positions with 125% fonts and clipped text; the form now scales its layout.
- Real update on the development computer, default root, quiet mode, startup
  Preserve: 0.5.3 closed, 0.5.4 installed and relaunched; Settings > Apps entry
  `DisplayWeave 0.5.4-preview` with `UninstallString`/`QuietUninstallString`
  pointing at `Uninstall DisplayWeave.exe`; startup value kept. Uninstalling the
  real installation was not run.

## Version 0.5.3 automatic maximize fallback

0.5.2 was installed locally only. 0.5.3 applies the same outside fit, without a
new rule, to a window whose component attempt failed (in the controller's failed
set) under automatic handling or a Force rule, using the region automatic
selection or the rule already chose. Such a window stays fitted only while that
mode still applies and the component remains unavailable; Resume clears failures
and retries the component.

- Managed checks: 94 (the precedence test gained the fallback-retention cases).
- Same live harness, automatic phase, no rules, `AutomaticExecutableFilter` set to
  the fixture: the component attempt failed because the scratch folder has no
  native helper; the unmaximized window was left alone; maximizing fitted it to
  its current region `0,0,1920,2100`; un-maximizing restored `300,300,900,600`;
  maximizing again refitted; turning automatic handling off returned it to
  `0,0,7680,2100`. A Force-rule phase with the same failing component fitted the
  rule's region, stayed fitted on later ticks, and controller disposal (Exit)
  returned it to the monitor. The rule phase above passed again on this code.
- Installer script unchanged since the 16/16 run for 0.5.2; not rerun.
- Not yet verified with Diablo IV or any real game.

## Version 0.5.1 loader diagnostics

The update distinguishes a verified module, an absent module, an unexpected image,
a target process that exited, and a real inspection API error. A completed thread's
raw result is never treated as a complete x64 module handle or its `GetLastError`.
In particular, raw zero or 259 is not used to infer thread liveness; the wait result
is authoritative. Existing timeout and remote-buffer lifetime rules are retained.

The rejecting-DLL fixture deliberately returns FALSE from DllMain only in its own
hidden test process. An immediate local LoadLibraryW/GetLastError call records the
actual 1114. The helper's separate load attempt must report its own missing-module
diagnostic, leave the fixture alive and unchanged, and never invoke initialization.
This fixture does not start a game or establish why Diablo's DLL load failed.

```powershell
.\test-loader-failure.ps1 -Architecture all -BuildDirectory .\release\build-0.5.1
.\test-monitor.ps1 -Architecture all -BuildDirectory .\release\build-0.5.1 -BootstrapLoader -Reconnect
```

Validated locally: 92 managed checks, 225 native policy checks per architecture,
and native IPC checks on both architectures. The rejecting-DLL fixture passed
on x64 and x86: two rejected loads (local and helper), local error 1114, an
application-specific missing-module diagnostic, zero initialization callbacks,
an inactive control mapping, and unchanged hidden window bounds/styles.
The successful-loader/reconnection fixture passed 132 checks per architecture.
The fullscreen engine DLLs are byte-for-byte unchanged from 0.5.0.

## Version 0.5 monitor engine

The current engine virtualizes USER32 monitor/window APIs; it does not intercept
DXGI. The previous DXGI results below are historical and do not certify the new
engine. The original fixture modes remain for older-resident handoff tests.

```powershell
.\test-monitor.ps1 -Architecture all -BuildDirectory .\release\build-0.5.0 -Reconnect
.\test-monitor.ps1 -Architecture all -BuildDirectory .\release\build-0.5.0 -DeferMessages
.\test-monitor.ps1 -Architecture all -BuildDirectory .\release\build-0.5.0 -BootstrapLoader -FilteredMessages -HeartbeatExpiry
.\release\build-0.5.0\DisplayWeave.exe --verify-monitor-controller .\release\build-0.5.0\native-v2-x64\FullscreenProbe.exe test-results\controller-x64.json
.\release\build-0.5.0\DisplayWeave.exe --verify-monitor-controller .\release\build-0.5.0\native-v2-x86\FullscreenProbe.exe test-results\controller-x86.json
```

The native monitor fixture checks coherent enumeration and monitor information,
point/rectangle/window lookup, clipping, callback cancellation, selected-screen
metrics, automatic attachment, maximize via both window APIs, restoration,
borderless fullscreen, live region/layout changes, manual Force, release,
heartbeat expiry, and cached monitor handles after release. It renders with a
real Direct3D 11 **windowed** swapchain and verifies actual DXGI state stays
windowed. `-BootstrapLoader` bypasses the message bootstrap entirely; the filtered
fixture deliberately does not retrieve ordinary posted messages. `-DeferMessages`
processes no messages until the hook is ready and requires evidence that the
helper automatically used its fallback loader. `-Reconnect`
starts a new helper against the same resident component and requires a fresh
acknowledgement and exactly one RegionHook DLL.

The managed integration test uses the production automatic-discovery controller
with an empty rule set. It independently compares control/layout data against the
intended region before asking the native fixture to verify its monitor view.
Only fixture processes launched by these tests are targeted. Run them with other
display tools exited; another tool's hook in a fixture makes its results ambiguous.
These tests do not establish game compatibility.

Local 0.5.0-preview validation (2026-10-09):

- 89 managed checks and 206 native policy cases per architecture passed.
- x64/x86 IPC access checks and all 16 installer regression checks passed.
- Normal bootstrap plus same-process reconnection: 132 monitor checks per architecture.
- Explicit loader plus filtered messages and controller-death restoration: 98 checks per architecture.
- Automatic loader fallback while the fixture processes no messages: 98 checks per architecture.
- Production managed automatic discovery, intended layout, fullscreen/maximize and Pause: 42 checks per architecture.
- All rendering runs reported zero failed presents. A third-party display tool's
  DLL was also loaded in the fixtures, so these runs do not isolate DisplayWeave.
- 0.4.0-resident-to-0.5.0-controller handoff, competing-controller refusal and
  incompatible-mapping refusal passed on both architectures, retaining the old DLL.
- All 12 launcher rejection checks passed without loading DisplayWeave in those fixtures.
- The desktop and taskbar remained at the physical display dimensions.
- Import inspection confirmed neither new hook DLL imports D3D or DXGI.

Diablo IV was closed and was not tested. No real launcher or game was started by
these checks. No claim of commercial-game compatibility follows from fixture tests.

## Reproducible noninteractive checks

Run on Windows 10/11 x64 from the source checkout:

```powershell
.\fetch-tools.ps1
.\build-native.ps1 -Architecture all
.\build.ps1
$app = Start-Process .\dist\DisplayWeave.exe -ArgumentList '--self-test test-results\managed.txt' -WindowStyle Hidden -PassThru -Wait
if ($app.ExitCode -ne 0) { throw 'Managed checks failed.' }
.\dist\native-v2-x64\RegionHookHost.exe --self-test-policy
.\dist\native-v2-x86\RegionHookHost.exe --self-test-policy
.\dist\native-v2-x64\RegionHookHost.exe --self-test-ipc
.\dist\native-v2-x86\RegionHookHost.exe --self-test-ipc
.\package.ps1
.\installer-tests.ps1
```

The managed suite covers non-overlapping ruler geometry, persistence, application
rules, shell exclusions, automatic region selection, startup command quoting,
application launch options, and elevation restart arguments. The native policy
suite checks shell protection, resident-component handoff, and access diagnostics
without attaching to real applications.
The installer suite uses local fixture ZIPs and temporary install roots. It
checks migration, settings preservation, immutable versions, rollback, checksums,
unsafe archive rejection, and leaving startup unchanged. It does not launch apps
or change the actual startup preference.

Local 0.4.0-preview validation: **65 managed checks**, **62 native policy cases
per architecture**, and **16 installer checks** passed. The actual release ZIP
also installed successfully to an isolated directory using Windows PowerShell
5.1. A real Windows sign-out/sign-in has not been performed as part of these tests.

Version 0.4.2 adds explicit administrator restart and distinguishes a confirmed
elevation mismatch from generic access denial. Local validation passed
**78 managed checks**: 18 layout, 25 rules/diagnostics, 11 automatic-region, and 24
lifecycle checks, plus **95 native policy cases per architecture**.
The separate IPC tests passed on x64 and x86: newly created test mappings and
mutexes preserve the creator's effective default DACL, carry a medium/no-write-up
label, and allow mapping read/write and mutex ownership. These run without
elevation and do not prove cross-integrity integration. The lifecycle tests inspect
constructed launch options, quoting, preserved paths, process-ID validation and
the bounded wait policy; they do not open UAC prompts or start an elevated app.

Read-only inspection confirmed that Diablo IV was elevated at high integrity
while DisplayWeave ran at medium integrity. This is a permission diagnosis, not
a successful game integration test. UAC acceptance/cancellation and elevated
game handling have not yet been validated interactively for 0.4.2.

Fresh x64 and x86 automatic-controller D3D11 runs passed fullscreen, maximize,
ordinary-window restoration and release with zero failed presents. The actual
0.4.0-to-0.4.2 resident-hook handoff also passed on both architectures, including
competing-controller exclusion and incompatible-mapping refusal. A third-party
display tool was also running. Desktop and taskbar dimensions remained full
width during the local validation.

GitHub Actions runs these checks on Windows and records reports as workflow
artifacts. Generated logs, personal layout/rule XML, build tools and binaries are
not committed to the repository.

## Interactive fullscreen checks

```powershell
.\test-native.ps1 -Architecture all
.\test-native.ps1 -Architecture all -ManagedController
```

These open temporary D3D11 probe windows and attach only to those test processes.
The managed-controller mode tests automatic preparation, maximize, fullscreen,
ordinary-window restoration and release with no manually saved app rules.
The probes wait for hook readiness before requesting fullscreen. Exit other
display tools first so that results reflect DisplayWeave alone.

Previous local validation of version 0.3.3 passed all 49 managed checks and 47
native shell checks per architecture. Managed automatic D3D11 integration passed
on x64 and x86, including fullscreen rendering in the region and restoration,
with zero failed presents. A third-party display tool was also running, so these
runs show behavior on that machine, not compatibility with every app.

Version 0.4.1 replaces the old cross-installation refusal with compatible-worker
reconnection. Local validation passed **67 managed checks** and **75 native policy
cases per architecture**. Fresh x64 and x86 automatic-controller integration also
passed fullscreen, maximize, normal restoration and release, with zero failed
presents. A third-party display tool was also running.

The handoff harness uses an actual 0.4.0 helper and DLL to prepare a test app,
stops only that helper, then starts the 0.4.1 helper against the same app process:

```powershell
.\test-handoff.ps1 -Architecture all -OldBuildDirectory .\release\build-0.4.0 -NewBuildDirectory .\release\build-0.4.1
```

Build/extract the two releases in the specified directories first. The harness
controls only the probe processes it launches and keeps finite waits and explicit
process/window identity checks.
It checks region changes from 800x450 to 960x540, rendered fullscreen, normal
restoration, one unchanged resident DLL, and no target process restart. It also
checks that a competing controller and an incompatible mapping are rejected
without altering the existing configuration.

Through 0.4.3 the fullscreen DLL behavior was unchanged from 0.3.3. Version 0.4.4
adds a sent-message bootstrap and startup diagnostics. The new host can still
reuse an already initialized older component; it does not replace its code.
Release notes list checks actually run for each release. Successful compilation
alone is not evidence of application compatibility.

## Filtered message startup regression

Version 0.4.4 adds a fixture that pumps sent messages while filtering out posted
WM_NULL startup messages. It never renders before its own hook acknowledges
readiness, avoiding driver message pumping that could hide the regression.
An old 0.4.3 helper reproduces error 1460 on both architectures; the new bootstrap
passes fullscreen, maximize and restoration on the same fixture. No game process
is opened or modified by these tests:

```powershell
.\test-native.ps1 -Architecture all -BuildDirectory .\release\build-0.4.4 -HookBuildDirectory .\release\build-0.4.3 -FilteredMessages -ExpectStartupTimeout -Automatic
.\test-native.ps1 -Architecture all -BuildDirectory .\release\build-0.4.4 -FilteredMessages -Automatic
```

Local validation also passed 84 managed checks, 132 native policy/atomic cases per
architecture, IPC security tests, ordinary managed automatic rendering tests,
0.4.0-to-0.4.4 resident handoff and all 12 launcher exclusion fixtures. Both normal
and filtered rendering tests had zero failed presents. A third-party display
tool's DLL was also loaded in the probes. Diablo IV startup and fullscreen behavior remain unverified. Its
missing control mapping after timeout motivated this investigation but does not
prove it uses the fixture's exact message filter.

## Manual release checks

Version 0.4.3 excludes the Battle.net launcher executables before native
attachment, including attempts through manual and saved rules. Local validation
passed **82 managed checks**, **114 native policy cases per architecture**, and
the existing x64/x86 IPC checks. The launcher fixture harness passed **12 native
attachment rejection checks** across three exact image names, two architectures,
and automatic/manual helper modes. Hidden copies of our own D3D11 probe confirm
rejection leaves the target alive without a DisplayWeave DLL or control mapping.
The harness never starts or hooks the user's Battle.net launcher:

```powershell
.\test-compatibility.ps1 -BuildDirectory .\release\build-0.4.3
```

Observed Battle.net reports contain a Chromium breakpoint with both DisplayWeave
and a third-party display tool's components loaded. A loaded DLL does not establish that its controller
was active or identify the cause of the crash. This release mitigates the
DisplayWeave attachment risk by leaving the launcher unmanaged; it does not
claim a Chromium-level correction.

After updating, reopen any launcher that previously loaded an older hook before
assessing stability. Confirm Battle.net remains usable while game windows can
still use region handling. Real-launcher stability remains a user verification.

- Start with `--minimized`: the tray icon should appear without opening settings.
  Open the shortcut again: settings should appear. X should return to the tray;
  Exit should save settings, release handling, and quit.
- Toggle **Start DisplayWeave with Windows** under **Status & help**. The change
  should persist after reopening settings. A Windows sign-in should start one
  instance quietly in the tray.
- Install a release, customize the layout, then install another version. Verify
  layout/rules remain unchanged, the stable shortcut selects the new version,
  and already-hooked applications show a restart message when required.
- Observe that the desktop and taskbar retain their full display dimensions.
  `RegionHookHost.exe --shell-status` provides a read-only diagnostic snapshot.
- From an ordinary DisplayWeave instance, select **Status & help > Restart as
  administrator** and cancel the Windows prompt. Verify the existing instance
  and its handling remain active, with no second instance taking control.
- Repeat and approve UAC. Verify the old instance exits, the new instance shows
  administrator access, and the same layout and app rules load from the same
  settings directory. Check that the Windows startup preference is unchanged.
- With administrator access active, test an authorized elevated game and an
  ordinary application separately. Check preparation, the chosen region,
  maximize/fullscreen, leaving fullscreen, and restoration on release. Record
  the graphics API and actual results; include Diablo IV before claiming support.
- Reopen the administrator instance from its tray icon, then exit it from the
  tray before using the ordinary updater. Verify that the normal shortcut can
  launch DisplayWeave without elevation after that exit.

Elevation alone does not establish fullscreen compatibility. Protected apps,
Direct3D 9, OpenGL, Vulkan, and individual commercial applications, including
Diablo IV, have not been certified. Physical exclusive scanout within only
part of a monitor is not implemented. Versions through 0.4 translated DXGI requests
to a windowed swapchain; new 0.5 components do not change graphics-API state.
