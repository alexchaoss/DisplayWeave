# DisplayWeave validation

Commands recorded below before October 10, 2026 use the old root-level script paths. Since the repository reorganization, build/package scripts live in `scripts\` and fixtures in `tests\` (for example `.\scripts\build.ps1`, `.\tests\test-monitor.ps1`); their parameters are unchanged.

The virtual-display projection prototype and its evidence moved to the `prototype` branch on October 11, 2026. Entries below that mention prototype or projection checks record what was run at the time.

## Installer without Bypass or a self-delete command (1.0.3, October 13, 2026)

Five VirusTotal engines flagged the 1.0.2 setup (Kaspersky Trojan.Win32.Monder.gen, Ikarus Trojan.Win64.Krypt, Elastic,
Google, SecureAge); Defender on the development computer, with cloud protection, found nothing. 1.0.3 removes the
installer patterns that look like a dropper, without hiding anything: PowerShell runs with `-ExecutionPolicy RemoteSigned`
instead of `Bypass` (setup, shortcut, startup entry, launch, app), CI signs `install.ps1` and the launcher, which is now
a packaged file, and the uninstaller is removed by a copy of itself in `%TEMP%` instead of `cmd ping … & del`.
Whether the engines still flag it is checked on VirusTotal after the release.

- `scripts/release.ps1` (x64, unsigned local build): managed and native checks passed, installer 22 of 22 (new: a
  package without the launcher is refused and nothing is made current), setup 6 of 6, including the quiet uninstall
  whose uninstaller copy disappears after it exits. Test packages now carry the real launcher.
- The built setup and every `.exe`, `.dll` and `.ps1` in the ZIP contain none of `ExecutionPolicy Bypass`, `ping -n`,
  `127.0.0.1` or `del /f`, as ASCII or UTF-16.
- Not run here: signing the two scripts, which only CI does; the release build checks their signatures.

## Faster update checks and the first automated release (1.0.2, October 13, 2026)

1.0.2 checks for updates about a second after start instead of a minute, then every hour instead of every six,
and tries a failed check again after five minutes (the network may not be up right after sign-in). It is also the
first version that CI signs with the Key Vault release key and publishes by itself after the maintainer's approval;
that run is recorded in the GitHub Actions history and the next entry, because this file ships inside it.

- Run 38408311901 on `01fb366` (added after the build): after the maintainer's approval, every step passed, including
  the Key Vault sign-in and checksum signing and the `publish` job, which published v1.0.2 as the latest full release.
  The published files, downloaded to an empty folder and not run: both `.sha256` files match; both `.sig` files verify
  with the Key Vault key from `Updater.ReleaseKeys`; all nine binaries are validly signed and timestamped by
  `CN=Alexandre St-Pierre`; the embedded helper hashes match the six signed helpers; the ZIP holds `testing.md`.
- Copies older than 1.0.1 pin only the local key, so they refuse 1.0.2 and, picking the newest release, do not fall
  back to 1.0.1; they need the newest setup installed by hand once (README). The maintainer's installed 0.6.0 is one.

## Release-key signing in CI (after 1.0.1, October 13, 2026)

From the next version, the approved windows-2022 release build signs the `.sha256` files with the Key Vault key
(`sign-release.ps1 -Directory release -KeyVault displayweave-release/release-signing`) and a new `publish` job
publishes the draft with them.

- `sign-release.ps1 -KeyVault` on throwaway `0.0.0-test` files, with an `az` stand-in built on Az PowerShell that
  makes the same Key Vault calls and prints the same JSON: two 64-byte signatures, each verified against the pinned
  key before it was written; a changed `.sha256` was refused and nothing was signed.
- Not yet run: the real Azure CLI on the runner, the second OIDC sign-in and the `publish` job. The first release
  after 1.0.1 exercises them; if a step fails, the version stays a draft.

## Second release key in Azure Key Vault (1.0.1, October 13, 2026)

1.0.1 pins a second release key next to the maintainer's local one, so that CI can sign later versions; the app is
otherwise 1.0.0. 1.0.1 itself is signed with the local key, because installed 1.0.0 copies trust only that one.

- Key `release-signing` in Key Vault `displayweave-release` (Standard, East US, RBAC, 90-day soft delete): EC P-256,
  operations sign and verify, not exportable. Pinned with `sign-release.ps1 -PinKey RUNTMSAAAABogT4jkDtu/qbfUCsR8ytE7STQ0FLahj7oxAj94s5KxTvK1V281m1RpTA09IOntRGyROecHzkhWVGtl2w/UZf0`.
- Key Vault's ES256 signature (64 bytes, r then s) over the SHA-256 of a sample checksum line verified with
  `ECDsaCng.VerifyData` and that public key, as `Updater.IsSignedRelease` does; the same signature failed for a
  changed line.
- `sign-release.ps1` now publishes a version without a suffix as a full, latest release.

## Code signing (1.0.0, October 13, 2026)

1.0.0 is 0.6.0 plus Authenticode signing of the release build in CI (`67b5e37`); the app's code is unchanged.

- Azure Artifact Signing account `displayweave` (East US, Basic) with the Public Trust certificate profile
  `displayweave-public` (`CN=Alexandre St-Pierre, O=Alexandre St-Pierre, L=Drummondville, S=qc, C=CA`); individual
  identity validation completed October 13. CI signs in through the GitHub `release` environment (branch `main`
  only, the maintainer as required reviewer) with an OIDC federated credential for that environment; the identity
  holds only the Certificate Profile Signer role on that profile.
- CI run 38396879055 on `67b5e37`, while 0.6.0 was already released: the `version` job, both Windows builds and
  the release job passed; all five signing steps were skipped and no deployment was made, so ordinary builds never
  wait for approval.
- The 1.0.0 release build's **Check the signatures** step fails the build unless every `.exe` and `.dll` in `dist`
  (native helpers, app, engine) and the setup has a valid signature; that run is recorded in the GitHub Actions
  history, not here, because this file ships inside it.
- Run 38398131641 on `9f70bb0`: the first attempt failed at the Azure sign-in (AADSTS700213) because GitHub's subject
  carries the owner and repository IDs; after the federated credential's subject was changed to that form, the
  re-run of the failed jobs signed, passed every check and created the v1.0.0 draft. The draft, downloaded to an
  empty folder and not run: both `.sha256` files match; all eight binaries in the ZIP and the setup have a Valid
  signature from `CN=Alexandre St-Pierre` with a timestamp; the helper hashes embedded in `DisplayWeave.exe` match
  the six signed helpers. (This bullet was added after the build, so the 1.0.0 package does not contain it.)
- Not tested: SmartScreen's reaction to the new publisher, and administrator mode with signed helpers on this
  computer.

## Shortcuts, profiles, recovery and update safety (0.6.0, October 12, 2026)

Changes on top of `e5e05cf`; x64, same computer. The checks below ran with `version.txt` still at
0.5.12-preview; the 0.6.0 run is recorded under Results.

- **Activity log** (`ActivityLog`): engine messages, layout problems, notices and update events, one
  line each, in `data\logs\activity.log` (rolled at 1 MB). The Diagnostic report adds the
  last 40 lines, scrubbed; without **Include app file names**, any `name.exe` left in the text is
  replaced too.
- **Timer**: `WindowManager.ListWindows` measured 0.6 ms with 3 app windows (about 0.2 ms per
  window, 100 calls), so no identity cache was added. The timer runs every second while the window
  is hidden and nothing moves, and every 250 ms for 2 s after any top-level window moves.
- **Rules**: window classes compare without per-run parts (GUIDs, the WinForms suffix, MFC/ATL
  addresses); Store apps (`ApplicationFrameHost.exe`) also compare their AppUserModelID; each rule
  records the day it last applied (rules saved at most once a day for it). A Store-app rule saved
  before this matches no Store app now.
- **Display renaming**: the layout stores the monitor's device path and size when saved. At start,
  on a lasting display change and on a profile switch it follows that device path, or, when the
  named display is gone, the only display of the same size.
- **Borderless journal**: windows shown without their title bar are listed in
  `data\borderless.journal` while they are; the next start takes them over (their rule keeps them,
  otherwise the title bar returns) and a fatal error restores them.
- **Display change**: the wait for the display to return applies only while Windows reports a
  fullscreen app in front (`SHQueryUserNotificationState` 3 or 2); otherwise, after 3 s, the change
  is kept and the layout is read again, paused. While waiting, Save layout reads **Use this display size**.
- **Shift** while maximizing leaves the window filling its display until it is restored.
- **Shortcuts tab**: user-made shortcuts only (fit, borderless, force into region N, release,
  pause/resume, switch profile) in `shortcuts.xml`; the built-in Ctrl+Alt+Shift shortcuts are gone.
- **One-click rule**: when a borderless game resizes itself back, a notification offers a Borderless
  in region rule for its region, once per app per session; clicking it re-checks the window first.
- **Remembered regions** (`app-regions.xml`, 256 apps): an app that starts maximized or fullscreen
  goes to the region automatic handling last fitted it into, per profile.
- **Layout profiles**: `data\profiles\profile-<name>.xml`; rules carry a profile (Ignore and older
  rules apply in all); New copies the layout and its rules.
- **Updates**: the setup is held open and re-verified before it starts; `current.json` records
  `previousVersion`; the launcher gives up a version after two starts without `health\<version>.ok`
  (written after a minute or on a clean exit); updates skip given-up versions; the tray offers
  **Go back to DisplayWeave <version>**.
- **Administrator helpers**: `build.ps1` embeds `helpers.sha256` for the six native files;
  `CopyHelpers` copies only those and checks each copy's hash in Program Files. `release.ps1` and CI
  now build the native files before the app.

Results:

- `.\scripts\release.ps1` (no `-Publish`, the local 0.5.12 release files restored afterwards):
  layout 20, rules 66, automatic 14, lifecycle 34 (134 passed, 0 failed), native policy and IPC on
  x64 and x86, installer 21 of 21 (new: the launcher goes back after two failed starts, a clean run
  is never given up, reinstalling resets the count), setup 6 of 6. The embedded helper list matched
  the native build's hashes.
- Again with `version.txt` at 0.6.0 (same source): layout 20, rules 66, automatic 14, lifecycle 34,
  native policy and IPC on x64 and x86, installer 21 of 21, setup 6 of 6. Setup SHA-256
  `c007baf5…b9e4b5` (local build; CI builds the published one).
- Interface captured with the in-process harness (an executable filter that matches nothing): the
  Layout profile row, the Shortcuts tab and its dialog, the saved-rules **Last used** column and
  Status & help.
- Not covered: any live handling of a real app with this build; the rule offer, journal adoption
  and returning a window to its monitor (the engine never inspects its own process, so self-tests
  cannot re-inspect a test window); a real display renaming; an administrator start; a real update
  and rollback; the Store app ID on a real Store app window.

## Source split into files under 500 lines (after 0.5.12-preview, October 12, 2026)

Changes on top of `7131b7d`; x64, same computer. No behavior change intended.

- `FullscreenController.cs` (1,528 lines) became a partial class in four files (state and `Tick`,
  outside fits, helper sessions, window inspection and Win32 declarations) plus separate classes
  for the parts without controller state: `HelperSession`, `HelperProtocol`, `FailureMessages`,
  `FullscreenFailureHistory`, `RegionGeometry`, the process protections, `ApplicationIdentity` and
  `HandledApp`. `Tick` calls `TickSessions` and `TickWindows`. Callers of the moved static members
  (`RegionGeometry.TryResolveRegion`, `FailureMessages.FormatFailure` and others) were updated.
  The window-style reads use one helper and named constants instead of repeated inline reads and
  raw numbers. The largest Core file is now 415 lines (`Layout.cs`).
- The app: `MainForm` became one partial file per feature (main, interface, layout, rules, rule
  options, status, updates), `RulerCanvas` a drawing and an input file, and `RuleTests.Run` six
  group methods in their own files, called in the old order so the report lists the tests as
  before. `scripts/build.ps1` now compiles every `.cs` file in `src/DisplayWeave` and `Tests`
  instead of a fixed list. The largest app source is now 340 lines (`MainForm.Rules.cs`).
  `LayoutTests.cs` (485), `AutomaticControllerIntegration.cs` (465) and the native sources were
  not split.
- The splits were made by scripts that assign every member (or test) of the old file to one new
  file and stop if any is left over; the bodies are unchanged apart from the renames above.

Results:

- `.\scripts\release.ps1` (no `-Publish`): layout 20, rules 54, automatic 14, lifecycle 33 (121 passed,
  0 failed), native policy and IPC on x64 and x86, installer 20 of 20, setup 6 of 6.
- Not covered: live handling of a real app with this build.

## App rules redesign, Borderless in region and Centered fullscreen (0.5.12-preview, October 12, 2026)

Changes on top of `deff6da`; x64, same computer and display as below.

- **App rules**: one "Add a rule for one app" flow (app, region, then one option with a short
  summary, an ⓘ tooltip and an explanation box beside the list) and a saved-rules list with app,
  region and option. Hotkeys still save Force fullscreen rules.
- **Borderless in region** (`ShowBorderless`): removes `WS_CAPTION`, `WS_THICKFRAME` and the edge
  extended styles from outside and sizes the window to the region; skips minimized, maximized and
  hung windows; restores the removed styles and the first place on release (`RestoreBorderless`).
  `HasExpandedApp` counts these windows and `LeaveFittedWindows` keeps them, so an update waits and
  never leaves a window without its title bar. Folder windows get the maximize fit instead.
- **Centered fullscreen**: the rule leaves the app alone. App rules shows the mode
  (`DisplayMonitor.TryCenteredMode`), whether Windows offers it (`OffersMode`) and steps for the
  adapter's vendor.
- **Display changes**: a display change while handling runs pauses and keeps the layout; the timer
  resumes once the layout's monitor is its layout size again. Resume handling, the tray item and
  Apply saved rules wait instead; Save layout keeps the display as it is.
- An automatically fitted borderless window that resizes itself back is left alone after one move.
  Outside-fit failures carry their own advice instead of "Use Force fullscreen".
- A Borderless in region window whose client area fills the region counts as fitted even when the
  app put its title bar back (`ClientBounds`), so DisplayWeave no longer fights such an app.
- App rules reloads the app list whenever it opens (an app started after DisplayWeave was missing),
  and the line beside **Save rule** says what is missing while the button is disabled (now gray).

Results:

- `.\scripts\build.ps1 -OutputDirectory <scratch>`, then `--self-test`: layout 20, rules 53,
  automatic 14, lifecycle 33 (120 passed, 0 failed); rules 54 (121) with the client-area case. New cases: rule decisions for the two new
  actions; a hidden captioned window under a Borderless in region rule loses its title bar, fills
  region 1, holds back updates through `LeaveFittedWindows`, and gets its title bar and place back
  from `RestoreBorderless`; saved rules keep both new actions; centered modes (3720 × 2160 for the
  middle of 1980 | 3720 | 1980 on 7680 × 2160; refused for a side or top region; accepted on a
  second display); fit failures without the generic advice.
- Sandboxed interface: the built `MainForm` hosted in a separate PowerShell process with scratch
  settings, a copy of the user's layout and an executable filter that matches nothing. App rules
  captured with the default option and with Centered fullscreen on region 2 (3,720 × 2,160, AMD
  steps). `WM_DISPLAYCHANGE` sent without changing the display: handling paused, Resume handling
  waited, handling resumed by itself within the next tick, and the layout stayed unchanged.
- `.\scripts\release.ps1` (no `-Publish`): the same 121 managed tests, native policy and IPC on
  x64 and x86, installer 20 of 20, setup 6 of 6.
- Live, the user's desktop, a portable copy run with the installed data folder: a game in its
  windowed fullscreen mode resized itself back to the monitor after the automatic fit (the expected
  0.5.12 error). Set to Windowed under a Borderless in region rule on region 2, it put its title
  bar and sizing border back after each removal and grew its window so its client area stayed in
  the region (window 1971, -38, 3,738 x 2,147 around a 3,720 x 2,100 client area; title bar above
  the display, not visible). The build before the client-area check reported an error after four
  tries although the picture filled the region; the rebuilt copy was not run against the game.
  Two other games filled their regions through the automatic fit, without a rule.
- Not covered: Centered fullscreen with a real driver setting; a real resolution change; style
  restoration on another process's window (only in-process).

## Automatic handling without the component (0.5.11-preview, October 11, 2026)

Changes on top of `046d4e8`; x64, same computer and display as below.

- **Automatic handling never starts a helper** (`FullscreenController.Tick`): it fits maximized
  and borderless fullscreen windows from outside (`FitFromOutside`). Force rules and the
  Ctrl+Alt+Shift shortcuts still load the component. The live hook fixtures
  (`AutomaticControllerIntegration`, `--auto-test-executable`) set `AutomaticExecutableFilter`,
  which keeps automatic loading for that one executable.
- **Borderless fullscreen** (`BorderlessFullscreen`): no `WS_CAPTION` and a visible frame within
  2 px of its monitor, or of its region once fitted. `WS_EX_TOPMOST`, `WS_EX_TRANSPARENT` and
  `WS_EX_NOACTIVATE` windows are skipped. The return covers the monitor's full bounds.
- **Updates**: `HasExpandedApp` counts only hooked apps; DisplayWeave forgets its outside fits
  (`LeaveFittedWindows`) before exiting for an update, and the next version adopts a maximized
  window already in its region.

Results:

- `.\scripts\build.ps1 -OutputDirectory <scratch>`, then `--self-test`: layout 20, rules 49,
  automatic 13, lifecycle 33 (115 passed, 0 failed). New cases: an update leaves fitted windows
  and the next version adopts them; automatic handling fits a hidden maximized window into a
  region and starts no helper; a hidden borderless window covering the primary display is
  fitted into region 1, and the same window with each overlay style stays where it is.
- `.\scripts\release.ps1` (no `-Publish`): the same 115 managed tests, native policy and IPC
  checks on x64 and x86, installer 20 of 20, setup 6 of 6. The running DisplayWeave was left
  untouched.
- Not covered: staying tracked across ticks and the return to the whole monitor need a window
  in another process, because the engine never inspects its own. There was no live run against
  a real game, a browser's fullscreen or a video player, and the live hook fixtures were not
  rerun (native code is unchanged).
- Release: committed as `deff6da` and pushed. CI run 38244634568 passed on `windows-2022` and
  `windows-2025` and attested the files; the maintainer signed and published the release. The
  published setup and ZIP signatures verify against the pinned key, their checksums match and
  name the files, and `gh attestation verify --source-ref refs/heads/main` passes for the setup.

## Security audit: signed releases, anonymous updates, diagnostic report, CI (0.5.10-preview, October 11, 2026)

Changes on top of `3d08f42`, released as 0.5.10-preview; x64, Windows 11 25H2 build
26200, 7680 × 2160 display at 125%.

- **Updater** (`Updater.cs`): no `gh`; lists releases from the public GitHub API and
  downloads the setup, its `.sha256` and `.sha256.sig` from the release download URL.
  `IsSignedRelease` needs an ECDSA P-256 signature from `ReleaseKeys` over the exact
  `.sha256` bytes, a line naming `DisplayWeave-Setup-<version>.exe`, and a matching hash.
  The maintainer created the release key with `sign-release.ps1 -NewKey` (CNG, not exportable),
  which pinned its public half in `Updater.cs` and `install.ps1`.
- **install.ps1**: the online route uses the same anonymous API and requires the signature on
  the ZIP's `.sha256` (`$releaseKeys`); `-PackagePath` installs are unchanged.
- **Release flow**: CI attests the ZIP and setup and makes a draft; `scripts/sign-release.ps1`
  (new) creates and pins keys, checks the attestation and checksums, signs and publishes;
  `release.ps1 -Publish` signs a local build.
- **Diagnostic report** (`DiagnosticReport.cs`, Status & help → Diagnostic report…), issue
  templates, `SECURITY.md`.
- **CI**: actions pinned to commit SHAs (checkout v7.0.1, upload-artifact v7.0.2,
  download-artifact v8.0.2, attest-build-provenance v4.2.2); `dependabot.yml`; build on
  `windows-2022` and `windows-2025`; installer tests also in Windows PowerShell 5.1; no
  cancelling on `main`.

Results:

- `.\scripts\build.ps1 -OutputDirectory <scratch>`, then `--self-test`: layout 20, rules 46,
  automatic 13, lifecycle 32 of 33 (111 passed). The one failure is the intended gate "A release
  key is pinned", because no key existed yet; it passes since the key was pinned. New passing cases: release-list parsing;
  signed setups (accepted with a pinned key; refused without keys, with another key, after
  the setup is replaced, after the checksum is rewritten to match, for an old version
  republished under a newer tag, and with a missing or unreadable signature); report
  scrubbing (profile path, user and computer names, SID, app paths and names, other paths
  with spaces); report shape (sections, scrubbed app row, every display listed).
- Scratch harness in Windows PowerShell 5.1 (ephemeral keys, nothing in the key store), 10 of
  10: `install.ps1`'s own `Assert-ReleaseSignature`, extracted by the parser, accepted a
  pinned signature and refused another key, a changed checksum, an empty key list and an
  unreadable signature; `sign-release.ps1 -PinKey` on a copy of the repository wrote two
  keys once into both files, kept CRLF and no BOM, refused a non-key, and the pinned
  `install.ps1` verified with its own list.
- Live against the public repository (scratch `TEMP` and install root): the updater, run from
  a `csc`-built host like `DisplayWeave.exe` (default protocols `Ssl3, Tls`), found
  `0.5.9-preview` from `0.5.6-preview` once TLS 1.2 was added (checked before signatures were
  required). `install.ps1` with and without `-Version` found `v0.5.9-preview` and refused it:
  "The release is missing DisplayWeave-0.5.9-preview.zip.sha256.sig"; nothing installed,
  no temporary files left. `sign-release.ps1 -Directory` without a key stopped with the
  `-NewKey` instruction.
- The report on this computer: Windows 11 25H2 build 26200, `\\.\DISPLAY1` 7680 × 2160,
  work area 7680 × 2100, 125%, its adapter and driver version, layout
  1980 × 2100, 3720 × 2100, 1980 × 2100. The dialog, rendered by a probe named
  `FullscreenProbe.exe` so the running app skipped it, opened with Copy focused and the
  text unselected.
- `tests/installer-tests.ps1` in Windows PowerShell 5.1: 20 passed. PowerShell 7 is not
  installed here.
- With the key pinned, `.\scripts
elease.ps1` (no publish) at 0.5.10-preview: managed and
  native checks passed, installer tests 20, setup tests 6. Local ZIP SHA-256 `2b96209f…`,
  setup `80aa0884…`; the published files are CI's rebuild, not these.
- CI run 38190736746 on `496d9ab`: both builds passed on `windows-2022` and `windows-2025`
  (managed 112 including the pinned-key check, native policy and IPC on x64 and x86, installer
  tests in PowerShell 7 and Windows PowerShell 5.1, setup tests). The release job attested the
  ZIP and setup and created draft `v0.5.10-preview`.
- `.\scripts\sign-release.ps1 -Version 0.5.10-preview` checked the attestations, signed both
  checksums and published the release. It is immutable, its tag points at `496d9ab`, and it has
  six assets including both `.sha256.sig` files. `gh attestation verify` on a fresh download:
  `build.yml@refs/heads/main`, commit `496d9ab`, GitHub-hosted runner.
- End to end against the published release (scratch `TEMP` and install root): the 0.5.10
  updater, from a `csc`-built host, went from `0.5.6-preview` to `0.5.10-preview` and
  accepted the signed setup; `install.ps1` without `-Version` verified the signature and
  installed 0.5.10 into a scratch root, with a receipt equal to GitHub's ZIP digest
  (`45864501…`).
- Not run: the full app with the new button (the installed DisplayWeave holds the
  single-instance lock) and the in-app update hand-off (idle wait, quiet setup, relaunch).
  Native code and the setup are unchanged and were not rebuilt.

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
- Not yet verified with any real game.

## App icon and repository cleanup (0.5.9-preview, October 11, 2026)

0.5.9 is the first published build of the unpublished local 0.5.8 work, plus a new app
icon (`src/DisplayWeave/app.ico`, 16 to 256 px) compiled into `DisplayWeave.exe` and the
setup; the main window and the tray use it. Cleanup: MinHook keeps only its sources,
headers and license (no upstream build, CMake or CI files); `tests/test-handoff.ps1`,
which expected the 0.4 `CAPS 3` capability and failed on every 0.5.x helper, was
removed; the unused `FullscreenController.AutomaticPendingCount` was removed.

- `scripts/release.ps1` passed: managed 108 (layout 20, rules 46, automatic 13,
  lifecycle 29), native policy and IPC on x64 and x86, installer 20, setup 6.
- The shell icon read from both `DisplayWeave.exe` and the setup is the new icon.
- Installed with the setup (`--quiet`, started through `Win32_Process.Create` so the
  relaunched app is not tied to the agent shell): it replaced the running 0.5.8, and
  0.5.9 runs from `versions\0.5.9-preview`.
- Not checked by eye: the title-bar and tray icon at 125% scaling.

## Shorter full-width flash for the outside fit (0.5.8-preview, October 11, 2026)

Reported: every maximize flashes. All of the user's open apps are Chromium-based or
folder windows, so they only get the outside fit: Windows maximizes the window to the
whole display and DisplayWeave fitted it on its next 250 ms timer tick. An app with a
working component is resized inside the app before Windows applies the maximize, so it
does not flash; using the outside fit for every app would add the flash to those.

Change: `MaximizeWatcher` (DisplayWeave.Core) listens to `EVENT_OBJECT_LOCATIONCHANGE`
with an out-of-context WinEvent hook (`WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS`;
nothing is loaded into other apps) and calls the UI's tick as soon as a top-level
window becomes maximized. Moves of an already maximized window, such as the fit itself,
do not trigger it. The 250 ms timer still runs; a re-entrant tick is skipped.

Checks, x64 Windows 11, 7680 x 2100 work area at 125%:
- Self-tests: managed 108 (rules 46; the new case checks a maximize is reported once per
  change and again after a restore, and not for the fit's own move).
- Timing harness, the user's DisplayWeave exited first so only the harness moved the
  window: one folder window on a unique scratch folder, restored in region 1 and
  maximized 5 times, its DWM frame polled every 1 ms (about 16 ms resolution). With the
  timer only it stayed full width for 63 to 171 ms (median 170; one trial missed both
  states). With the maximize event it stayed full width for one poll interval (16 ms)
  in all 5 trials. The slowest tick, window listing included, took 31 ms.
- `scripts/release.ps1` passed (managed 108, native policy 233, installer 20, setup 6);
  the unpublished local 0.5.8 folder was replaced and installed with the setup. With
  only the installed app moving the window, the same 5 maximizes stayed full width for
  16 ms each, and no helper was started.
- On screen, installed app: a one-pixel row from x 1990 to 7590 was read from the screen
  once per composed frame (`DwmFlush`) for 700 ms after each of 5 maximizes and compared
  in memory with the row before; nothing was saved. With Windows' animation on, the
  window never covered the row (at most 8% of it changed for a folder window and 4% for
  an owned Chromium-based browser window, the same as other apps redrawing). With the window's transitions
  disabled from DisplayWeave's process (`DWMWA_TRANSITIONS_FORCEDISABLED`, S_OK), the
  full-width window covered all of it for at least one frame in every trial. The
  animation hides the frame, so DisplayWeave leaves animations alone (user decision).

## Folder windows maximize into regions (0.5.8-preview, October 11, 2026)

Asked: the user's File Explorer windows should maximize into a region while the
desktop stays full width. They were excluded entirely as shell windows.

Changes:
- `TryInspect` accepts a `CabinetWClass` or `ExploreWClass` window even in the shell's
  process (`ShellProcessProtection.IsFolderWindow`). Every other shell window, class
  and executable is still refused.
- A folder window only ever gets the outside maximize fit (`OutsideOnlyReason`, shared
  with Chromium windows): an explicit Force is refused with `FolderMessage`, and the
  native helper still refuses these classes. Status & help lists it as Ready with that
  reason.

Checks, x64 Windows 11, 7680 x 2100 work area at 125%:
- Self-tests: managed 107 (layout 20, rules 45, automatic 13, lifecycle 29). The new
  rules case: explicit and saved-rule attaches of a folder window are refused without a
  helper or failure; the desktop, taskbar and `SHELLDLL_DefView` classes are not folder
  windows; a hidden, maximized folder-class window with a Force rule is fitted to region
  2 and listed as fitted from outside with the folder reason. The Explorer protection
  case now uses the desktop class.
- Live, engine hosted against one folder window opened on a unique scratch folder: its
  `explorer.exe` window was listed, restored inside region 2, maximized and fitted from
  `-9,-9,7689,2109` to frame `1980,0,5700,2100` in one tick, then restored to its
  original rectangle. Work area `0,0,7680,2100`, desktop `0,0,7680,2160` and taskbar
  `0,2100,7680,2160` were identical before, during and after. The test window was
  closed by its own handle, and the user's other folder windows stayed open.
- `scripts/release.ps1` passed (managed 107, native policy 233, installer 20, setup 6). The
  unpublished local 0.5.8 folder was replaced and installed with the setup. Under the
  running app, a folder window restored inside region 3 and then maximized landed on
  frame `5700,0,7680,2100` within 0.5 s, stayed there for 4.5 s, and restored to its
  original rectangle. No helper was started.

## Chromium windows are never hooked (0.5.8-preview, October 11, 2026)

Reported: the user's browser crashed repeatedly right after 0.5.6 was installed. Its
three Crashpad dumps (15:11:26, 15:11:40, 15:12:47) show the same deliberate Chromium
breakpoint (`0x80000003`) at the same offset in the browser engine DLL, on a thread handling window
messages, with DisplayWeave 0.5.6's `RegionHook.dll` in the window-procedure chain (and
a third-party display tool's DLL). This matches the 0.4.3 launcher crashes. 0.5.5 had
never really hooked the browser: its resident-hook refusal (1306) blocked it by
accident. 0.5.6's fix made automatic handling attach, and the browser began to crash. A
desktop app built on the same engine was attached too. The user's DisplayWeave was
closed gracefully to stop it.

Changes:
- 0.5.7 was a local-only build of this fix; 0.5.8 adds the Ready state below.
- `ChromiumWindowProtection`: a window whose class starts with `Chrome_WidgetWin_` never
  gets a helper. Automatic handling and Force rules use the outside maximize fit, its
  row explains why, and an explicit attach is refused with that message.
- The native preflight refuses the same class with error 50, before any mapping or
  load.
- After installing 0.5.7 the user reported that DisplayWeave detected no apps. Every open
  app was Chromium-based and not maximized, so nothing was listed until a maximize. Such a
  window is now listed each tick as **Ready**, with its region and the reason nothing is
  loaded (`HandledAppState.Ready`). The header counts these apps too ("N handled"); it
  used to count only hooked apps as "ready".
- The window title and tray tooltip show the version, compiled in from `version.txt` by
  `scripts/build.ps1`, so portable and development copies show it too.

Checks, x64 Windows 11, 7680 x 2100 work area at 125%:
- `scripts/release.ps1` at 0.5.7-preview: managed 104 (layout 20, rules 43, automatic 13,
  lifecycle 28); native policy 233 on x64 and x86 (ten Chromium class cases), IPC,
  installer 20, setup 6. The new rules case: explicit and saved-rule attaches of a
  Chromium-class window are refused with the explanation, start no helper and record no
  failure; near-miss classes stay eligible.
- Live, an owned window registered as `Chrome_WidgetWin_1`: the 0.5.7 helper exited with
  "Chromium windows are excluded ... (Windows error 50)". No `RegionHook.dll` was loaded
  and no `Local\DisplayWeave_<pid>` mapping was created.
- Installed with the setup (`--quiet --no-launch`) and started through the sign-in
  command. With 26 Chromium-family processes running, 0.5.7 started no helper; 0.5.6 had
  attached to the browser and a desktop app within seconds.
- 0.5.8-preview: `scripts/release.ps1` managed 105 (layout 20, rules 43, automatic 13,
  lifecycle 29; the new cases list a Chromium window with a rule as Ready in its region
  without a helper, and check the compiled-in version), native policy 233, installer 20,
  setup 6. Installed with the setup and started through the sign-in command.
- Live with 0.5.8 running: an owned `Chrome_WidgetWin_1` window maximized to exactly
  `0,0,1980,2100` (region 1), with no helper and no DLL, and restored to its original
  rectangle. The running window's title read "DisplayWeave 0.5.8-preview". Status & help
  listed the user's three open Chromium-based apps as Ready in regions 2, 3 and 1, and
  the header read "3 handled · 0 attaching".
- With that 0.5.8 installed, the user reported that no app stayed in its region when
  maximized: each filled the whole display. An owned Chromium-based browser (throwaway profile)
  showed why. The engine did fit the window, but Chromium reset every move of its
  maximized window in `WM_WINDOWPOSCHANGING`. The window stayed at `-9,-9,7689,2109`
  until the fit gave up after four tries ("keeps changing its maximized size").
- Fix, still 0.5.8-preview: the outside fit and its undo pass `SWP_NOSENDCHANGING`, so
  Windows applies the move without asking the window. A plain move stayed refused, and
  a move with that flag landed on frame `0,0,1980,2100` while the window stayed
  maximized. The window kept that frame through z-order, frame-change and foreground
  changes, and restored to its original rectangle. After a minimize and restore,
  Windows maximized it to the whole display again, as for a new maximize.
- New rules case: a hidden, maximized window that refuses moves in
  `WM_WINDOWPOSCHANGING` is fitted to its region. It failed before the fix.
  `scripts/release.ps1`: managed 106 (layout 20, rules 44, automatic 13, lifecycle 29),
  native policy 233, installer 20, setup 6.
- The local 0.5.8 folder was replaced (unpublished) and installed with the setup. Under
  the running app, the owned browser window maximized from region 1 to frame
  `0,0,1980,2100` within 0.5 s, with no helper started.
- Not run: maximizing the user's own Chromium-based apps.

## Presets and attaching without an app restart (October 11, 2026)

Reported: the user's browser (PID 3144, running since October 8) failed with "Restart the target
application; its resident DisplayWeave hook has no accessible control mapping (Windows
error 1306)". Read-only inspection: the browser held `RegionHook.dll` from an October 9
pre-rename build (`Local\RegionScreen_3144`, `RegionScreen.*.v2` window properties). Its
controller was long gone. The helper counted any module named `RegionHook.dll` as its own,
then found no `Local\DisplayWeave_3144`.

Changes:
- The helper reuses a resident worker only when `Local\DisplayWeave_<pid>` already exists
  (`CreateFileMapping` reports `ERROR_ALREADY_EXISTS`); the module-name scan is gone. Any
  other `RegionHook.dll` stays inactive beside a freshly loaded component, and the
  standard-loader fallback counts only the exact DLL path it loaded.
- A live resident worker takes over a window it never registered, such as a second
  window of the same process; it subclasses that window before acknowledging. Restart
  remains only for a worker whose hooks failed (`hookReady = -1`) or a damaged mapping.
- Presets come from the display's size, aspect ratio and Windows scaling
  (`DisplayMonitor.ScalePercent`, `RulerLayout.PresetPins`). Equal splits stay exact on
  whole logical pixels; other sizes round to 20 px that are also whole logical pixels.
  Wide center keeps a 16:9 center at full height with equal sides, or quarters when
  the sides would be under 1/8 of the width. The grid drops to the greatest common
  divisor of itself and the preset pins, so a reload never moves them. A first start
  applies this display's halves.

Checks, working tree on top of `0748abb`, x64 Windows 11, 7680 × 2100 work area at 125%:
- `scripts/release.ps1`: managed 103 (layout 20, rules 42, automatic 13, lifecycle 28);
  native policy 223 on x64 and x86 (the handoff table lost its multiple-hook and
  unregistered-window cases), IPC, installer 20, setup 6. The new layout case checks
  exact pins for nine display/scale/preset cases, including 1980 | 3720 | 1980, and
  that each layout reloads unchanged.
- Live, owned x64 WinForms probe (`FullscreenProbe.exe`, so the user's running
  DisplayWeave skipped it) with two windows, helpers run directly, Force mode:
  - The installed RegionScreen 0.5.4 helper attached and quit, leaving its hook resident.
    The installed DisplayWeave 0.5.5 helper then reproduced the reported 1306 error.
  - The new helper attached fresh beside it (`READY`), maximize landed exactly in
    `0,0,1920,1080`, a second controller was refused (183), and QUIT restored
    `48,40,450,279`.
  - The new helper then took the probe's second window through its own resident worker
    (`READY ... REUSED`), fitted `1920,0,4480,1440` exactly and restored `48,320,450,559`.
  - Same sequence with the installed 0.5.5 hook resident: the new helper reused it for
    both windows with the same exact fits and restores.
- Live, real `MainForm` hosted in-process with automatic regions off: scaling read 125%;
  the real **Use preset** button with Wide center listed 1980 × 2100, 3720 × 2100 and
  1980 × 2100 with a 20 px grid. Halves 3840 | 3840, columns 2560 × 3, quadrants
  3840 × 1050 (10 px grid).
- Harness note: a PowerShell console set to UTF-8 writes a byte-order mark before the
  first helper command, which the helpers report as `Unknown command`. The app writes
  without one; the scripts above set a BOM-less input encoding.
- Installed as 0.5.6-preview with `DisplayWeave-Setup-0.5.6-preview.exe --quiet` (release gate
  at 0.5.6: managed 103, native checks, installer 20, setup 6). Setup exit 0. The running
  0.5.5 left through `--exit-for-update` with no helper left. `current.json`, the uninstall
  entry and the sign-in entry show 0.5.6-preview, and settings were kept. A 0.5.6 helper
  attached to the browser that 0.5.5 had refused with 1306. The browser then held both
  the pre-rename hook and the 0.5.6 hook. After a graceful restart, the next helper reused
  the 0.5.6 hook already in the browser.
- Not run: x86 targets live, and `tests/test-handoff.ps1`, which still expects the 0.4 DXGI
  capability.

## Robustness and the handled-app list (October 11, 2026)

Changes:
- Status & help lists the apps the engine handles (`FullscreenController.HandledApps`):
  helpers, maximized fits, recorded failures, and listed windows whose saved rule names
  a missing region. Selecting a row shows the full reason. A fit after a component
  failure keeps that failure as its reason after the fit succeeds.
- A layout the engine cannot resolve sets `LayoutProblem`, shown as "Layout needs
  attention" with the display names Windows reports. Before, the engine skipped every
  window while the header said "Handling active".
- An exception while handling one app (its helper session, its window, or its
  maximized fit) leaves only that app alone. Its row shows the error, the stack goes
  to Status & help, and the other apps stay handled. Resume, Force or closing the app
  clears it. Only an exception outside any app's handling pauses everything. A window
  or button error is reported without pausing. One dialog per distinct error, so a
  repeating error cannot stack dialogs. A failed window construction shows a message
  and exits with code 2. A background-thread error shows the reason before the process
  ends.
- A first start no longer pauses itself. The default layout's split snaps to the
  100 px grid (3840 to 3800 on a 7680 px display), which was treated as repairing a
  saved layout: handling paused with "The previous layout was adjusted".
- An unreadable `rules.xml` is copied to `rules.xml.unreadable.bak` before the next
  save can replace it.

Checks:
- `scripts/release.ps1`: managed 102 (layout 19, rules 42, automatic 13, lifecycle
  28). New cases cover a failed attach (no helper), a rule naming a removed region, an
  ignored app, a missing layout display, and an app whose handling throws: the other
  app is still handled in the same tick, the failed app is skipped afterwards, nothing
  pauses, and Resume retries it. With the per-app catch removed, that case fails. The
  RegionService case covers the unreadable-rules backup. Native checks passed;
  installer tests 20, setup tests 6.
- While the user's RegionScreen 0.5.4 ran, its hook was loaded into each shown test
  process, and monitor APIs there reported `\\.\REGIONSCREEN1..3` instead of
  `\\.\DISPLAY1`. The new layout message reported exactly that. The user then quit
  RegionScreen for the live checks below.
- Live, 7680 × 2100 work area at 125% scaling, owned probe windows only, automatic
  regions off so no other window was handled:
  - Real `DisplayWeave.exe` with real helpers. A first start with no settings was not
    paused. A probe with a Force rule to region 1 listed as `Working · Region 1 ·
    3800 × 2100`. A probe whose rule named a removed region listed as `Error` with that
    reason, and it maximized to the whole work area. A DPI-aware probe maximized exactly
    into `0,0,3800,2100`. After exit both probes were back at their original
    rectangles, with no helper left.
  - Real `MainForm` hosted in-process, two DPI-aware probes with Force rules to regions
    1 and 2, both ready. A simulated bug in probe A's handling: not paused, B still
    `Working` and maximized exactly into `3800,0,7680,2100`. A listed as `Error` with
    the reason, and its maximize filled the whole work area (left alone).
- Found: a probe that is not DPI-aware maximized to `0,0,4750,2625` instead of
  `0,0,3800,2100`, the region scaled by 125%. The native hook reports physical region
  coordinates to a process whose coordinates Windows scales. Not fixed.
- Not run: a real game.

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
- Not yet verified with any real game.

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
This fixture does not start a game or establish why a game's DLL load failed.

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

No real launcher or game was started by
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

Read-only inspection confirmed that the user's game was elevated at high integrity
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
tool's DLL was also loaded in the probes. The user's game's startup and fullscreen behavior remain
unverified. Its missing control mapping after timeout motivated this investigation but does not
prove it uses the fixture's exact message filter.

## Manual release checks

Version 0.4.3 excludes a game launcher's executables before native
attachment, including attempts through manual and saved rules. Local validation
passed **82 managed checks**, **114 native policy cases per architecture**, and
the existing x64/x86 IPC checks. The launcher fixture harness passed **12 native
attachment rejection checks** across three exact image names, two architectures,
and automatic/manual helper modes. Hidden copies of our own D3D11 probe confirm
rejection leaves the target alive without a DisplayWeave DLL or control mapping.
The harness never starts or hooks the user's launcher:

```powershell
.\test-compatibility.ps1 -BuildDirectory .\release\build-0.4.3
```

Observed launcher crash reports contain a breakpoint with both DisplayWeave
and a third-party display tool's components loaded. A loaded DLL does not establish that its controller
was active or identify the cause of the crash. This release mitigates the
DisplayWeave attachment risk by leaving the launcher unmanaged; it does not
claim a fix in the launcher itself.

After updating, reopen any launcher that previously loaded an older hook before
assessing stability. Confirm the launcher remains usable while game windows can
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
  the graphics API and actual results before claiming support.
- Reopen the administrator instance from its tray icon, then exit it from the
  tray before using the ordinary updater. Verify that the normal shortcut can
  launch DisplayWeave without elevation after that exit.

Elevation alone does not establish fullscreen compatibility. Protected apps,
Direct3D 9, OpenGL, Vulkan, and individual commercial applications have not been
certified. Physical exclusive scanout within only
part of a monitor is not implemented. Versions through 0.4 translated DXGI requests
to a windowed swapchain; new 0.5 components do not change graphics-API state.
