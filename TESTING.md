# DisplayWeave validation

Commands recorded below before October 10, 2026 use the old root-level script paths. Since the repository reorganization, build/package scripts live in `scripts\` and fixtures in `tests\` (for example `.\scripts\build.ps1`, `.\tests\test-monitor.ps1`); their parameters are unchanged.

The virtual-display projection prototype and its evidence moved to the `prototype` branch on October 11, 2026. Entries below that mention prototype or projection checks record what was run at the time.

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

## Security and speed fixes (October 11, 2026)

Fixes from a repository audit:
- Legacy administrator mode never checks for updates. Its native helpers start from
  a copy in `%ProgramFiles%\DisplayWeave\<user SID>_<id>`, removed at exit or at the
  next administrator start.
- The prototype's administrator step is an inline bootstrap. It copies the display
  fixture, lease and driver into `%ProgramFiles%\DisplayWeavePrototype\sessions\<id>`,
  checks the fixture and lease against hashes compiled into the build, then runs
  the copies. Display reports are written there.
- `rules.xml` is written through to disk before it replaces the old file.
- `fetch-tools.ps1` pins Zig's SHA-256; the unused MinHook download was removed.
- An engine tick reuses the identity `ListWindows` read. With 3 app windows, one
  pass now costs 0.60 ms per tick; before, it was 0.58 ms plus a 0.15 ms second pass.
- `install.ps1` online mode picks the highest version, as the app's updater does.
  Checked on sample tags: `v0.5.10 > v0.5.10-preview > v0.5.9 > v0.5.4-hotfix`,
  with `prototype-v*` excluded.
- The prototype disposes its process objects and keeps the 20 newest session folders.

Checks:
- `scripts/release.ps1`: managed 100 (layout 19, rules 40, automatic 13,
  lifecycle 28). The new case covers the helper copy and pruning only this user's
  earlier copies. Native checks passed, installer tests 20, setup tests 6.
- `build-projection.ps1 -Architecture all -RunSelfTests`: presenter 118, lease
  219, plan 101 and input 57 on x64 and x86.
- `build-prototype.ps1 -RunSelfTests`: self-tests 40, including bootstrap quoting,
  pinned hashes, length and report location. `tests/prototype-elevation-tests.ps1`
  passed 6. It runs the bootstrap unelevated against a temporary root, with a
  stand-in fixture and an install path containing `'` and `’`. The copies ran with
  the session arguments, and week-old sessions were removed. A changed lease
  stopped the bootstrap before the fixture ran, and the mismatch was reported in
  `fatal.json`.
- `package-prototype.ps1`; prototype installer tests 10.
- Not run:
  - an elevated legacy session or a real prototype display session, because both
    need an administrator prompt and change the desktop;
  - the live controller integration fixture;
  - a PowerShell 7 build, because pwsh is not installed here. The bootstrap test
    always runs under Windows PowerShell.

## Engine library without the UI (October 11, 2026)

The region engine moved to `src/DisplayWeave.Core` and builds as
`DisplayWeave.Core.dll` with `/noconfig`, referencing only System, System.Core,
System.Drawing and System.Xml. `DisplayMonitor` replaces WinForms `Screen`;
`RegionService` holds the app's actions, and the window calls it. The unused
assignment code in `WindowManager` was removed. Region behavior is unchanged
apart from one addition: a new data folder now starts on the primary monitor
instead of an empty monitor name, which left a headless host doing nothing.

- Managed checks: 99 (layout 19, rules 40, automatic 13, lifecycle 27). New:
  monitor lookup matches WinForms `Screen` (all monitors, case-insensitive
  find, rectangle and window lookup); `RegionService` defaults to the primary
  monitor, rejects a missing region or window without saving, saves both files
  and raises `Saved` once, reloads cleanly, and reports a failed save as
  `IOException` without raising `Saved`.
- `scripts/release.ps1`: managed and native checks passed, installer tests 20
  passed, setup tests 6 passed. The app ZIP contains `DisplayWeave.Core.dll`;
  the installer requires it.
- `build-projection.ps1 -Architecture all -RunSelfTests`: presenter 118, lease
  219, plan 101 and input 57 on x64 and x86. `build-prototype.ps1
  -RunSelfTests` 35, `package-prototype.ps1`, prototype installer tests 10.
- The README's headless sample compiles against the built library. It was not
  run, and neither was the app: both would apply the saved rules to the
  desktop's windows.

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
