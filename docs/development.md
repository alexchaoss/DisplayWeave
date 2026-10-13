# DisplayWeave development runbook

Start with [AGENTS.md](../AGENTS.md) and the [dated handoff](agent-handoff.md). This runbook separates compilation from actions that affect the desktop. Commands run from the repository root on Windows 10/11 x64 unless stated otherwise.

## Repository layout

| Folder | Contents |
| --- | --- |
| `src/DisplayWeave.Core` | Legacy region engine, `DisplayWeave.Core.dll`: layout, rules, window listing, controller and the `RegionService` facade. Compiled without any UI reference. |
| `src/DisplayWeave` | Legacy WinForms app over the engine (`version.txt`, `release.json`, `app.manifest`); managed checks under `Tests/`. |
| `native/legacy` | Legacy hook host, RegionHook DLL, fullscreen probe and protocols; loader fixtures under `tests/`. |
| `scripts` | `fetch-tools`, `build`, `build-native`, `package`, `build-setup`, `release`, `sign-release` (maintainer's computer, or CI with `-KeyVault`). |
| `installer` | `install.ps1`, shipped at the root of the package, and the setup program in `installer/setup`. |
| `tests` | Installer regression checks and all live fixtures (`test-*.ps1`). |
| `third_party`, `docs` | Vendored MinHook 1.3.4 (sources, headers and license only); documentation. |

Scripts locate the repository from their parent folder, so run them from anywhere. `dist`, `release`, `.tools` and `test-results` are ignored local outputs.

The virtual-display projection prototype (its app, `native/projection`, scripts, installer, fixtures and research docs) lives on the `prototype` branch since 2026-10-11.

## Source map

| Concern | Files and existing behavior |
| --- | --- |
| Legacy entry and lifecycle | `src/DisplayWeave/Program.cs`, `AppOptions.cs`, `ElevationRestart.cs`, `StartupRegistration.cs`, `Updater.cs`: single instance, settings location, explicit elevation, sign-in startup and release updates from the public GitHub releases. |
| Legacy interface | `MainForm` is one partial class per feature: `MainForm.cs` (startup, the adaptive timer, tray, notifications, exit), `.Interface.cs` (window shell and control helpers), `.Layout.cs` (with the display-change wait), `.Profiles.cs`, `.Rules.cs` with `.RuleOptions.cs`, `.Shortcuts.cs` (with `ShortcutDialog.cs`), `.Status.cs` and `.Updates.cs` (updates, the health mark for the launcher, going back a version). The timer ticks every 250 ms while the window is open, a helper works or a top-level window moved in the last 2 s (`MaximizeWatcher`'s second callback), otherwise every second. `RulerCanvas.cs` draws the ruler editor and `RulerCanvas.Input.cs` edits its pins. `DiagnosticReport.cs`: the Status & help report for GitHub issues; its `Scrub` removes paths, user/computer names, SIDs and (unless chosen) app names from all free text. Closing X hides to tray; Exit stops handling. Actions call `RegionService`; keep business logic out of the form. |
| Layout and rules | `src/DisplayWeave.Core`: `Layout.cs`, `RulerLayout.cs`, `ApplicationRules.cs`, `AutomaticRegionPolicy.cs`: non-overlapping grid, saved identities, rule precedence and automatic overlap selection. `ApplicationRule.StableClass` drops per-run parts of window classes; `StoreAppIdentity.cs` reads a Store app's AppUserModelID. `Shortcuts.cs` holds the user's shortcuts, `AppRegionMemory.cs` the region each app was last fitted into. Other settings files go through `XmlFile.cs` (no DTDs, atomic replace). `ActivityLog.cs` writes `logs\activity.log` once a host opens it. |
| Window/controller ownership | `WindowManager.cs` lists eligible windows; `DisplayMonitor.cs` reads monitors; `DisplayMonitor.TryCenteredMode` and `OffersMode` back the Centered fullscreen guide on App rules; `RegionService.cs` loads/saves settings and exposes the app's actions to any host; `RegionService.Profiles.cs` switches layout profiles (`data\profiles\profile-<name>.xml`) and follows a renamed display by `DisplayMonitor.DeviceId`, else the only display of the layout's size. `FullscreenController` is the UI-thread coordinator, one partial class per concern: `FullscreenController.cs` (state, `Tick` and the window decisions, failures, pause/resume), `.OutsideFit.cs` (maximized and borderless fullscreen windows, and `ShowBorderless` for Borderless in region rules), `.Sessions.cs` (the legacy native component, which from 0.5.11 loads only for Force rules; automatic handling still loads it when `AutomaticExecutableFilter` scopes a live fixture), `.Memory.cs` (remembered regions and the `BorderlessJournal.cs` file that lets a later start take over windows left without their title bar) and `.Windows.cs` (window identity and state, Win32 declarations). The engine never inspects its own process, so self-tests can drive a fit on a test window but not the steps that re-inspect it (adoption, return to the monitor, the one-click rule offer). Its parts with no controller state are separate classes: `HelperSession.cs` (one helper process), `HelperProtocol.cs` (helper command-line geometry and readiness line), `FailureMessages.cs` (failure classification and wording), `FullscreenFailureHistory.cs`, `RegionGeometry.cs` (resolving and validating regions in pixels), `ProcessProtection.cs` (shell, launcher and Chromium exclusions), `ApplicationIdentity.cs` and `HandledApp.cs`. Do not pull the controller into the non-injecting prototype (`prototype` branch). |
| Legacy native code | `native/legacy/HookHost.cpp`, `RegionHook.cpp`, `RegionProtocol.h`, `RegionLayoutProtocol.h`, `VirtualMonitorModel.h`: delivery, resident-hook reconnection and USER32 monitor/window compatibility. `ShellPolicy.h` and `CompatibilityPolicy.h` contain exclusions. |
| Managed checks | `src/DisplayWeave/Tests/LayoutTests.cs`, `RuleTests.cs`, `AutomaticRegionTests.cs`, `LifecycleTests.cs`; `AutomaticControllerIntegration.cs` has separate live fixture integration. `RuleTests.Run` calls one method per group, each in its own `RuleTests.*.cs` file. `scripts/build.ps1` compiles every `.cs` file in `src/DisplayWeave` and its `Tests` folder, so new files need no list. Keep source files under 500 lines. |
| Packaging and CI | Legacy: `scripts/package.ps1`, `installer/install.ps1` (install and `-Uninstall`), `tests/installer-tests.ps1`; setup: `installer/setup/Setup.cs`, `scripts/build-setup.ps1`, `tests/setup-tests.ps1`; full flow: `scripts/release.ps1`. CI: `.github/workflows/build.yml`; its actions are pinned to commit SHAs (version in a trailing comment), and `.github/dependabot.yml` proposes grouped updates weekly after a 7-day cooldown. Issue templates and `SECURITY.md` are under `.github`. |

## Build and checks without desktop interaction

The C# compiler comes with Windows .NET Framework at `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`; a modern .NET SDK is not required. `fetch-tools.ps1` downloads Zig 0.13.0 and checks it against the SHA-256 pinned in the script.

Inspect the working tree first and preserve local work.

```powershell
# Once per new clone, when .tools does not already contain the compiler:
.\scripts\fetch-tools.ps1

# Native components first: build.ps1 embeds their SHA-256 (helpers.sha256) for administrator mode.
.\scripts\build-native.ps1 -Architecture all
.\scripts\build.ps1
```

Rebuild the app after rebuilding native files, or administrator mode refuses the changed helpers. `build.ps1 -NativeDirectory` reads them from another folder, as `release.ps1` does.

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

## Tests that affect the desktop

They are not required for documentation changes. Keep the user informed when a requested test will take focus or attach to windows.

| Script | Effects |
| --- | --- |
| `test-monitor.ps1`, `test-loader-failure.ps1`, `test-compatibility.ps1`, `test-native.ps1` | Interactive/native-delivery fixtures. They attach only to the test processes they start; inspect their parameters and isolation guards first. |

## Packaging and repository updates

```powershell
# Requires the legacy managed and both native builds above:
.\scripts\package.ps1
.\tests\installer-tests.ps1 -OutputDirectory .\test-results\installer-check
```

`version.txt` and `release.json` must agree. Packaging uses an allowlist and generates ZIP/checksum output under `release`; from 1.0.4 the ZIP entries are stored, not deflated, so the setup does not carry a compressed blob; Installer regression checks use temporary fixtures without GitHub calls or application launches; the only registry write is a temporary HKCU RunOnce value in the in-use uninstall case, restored afterwards. Publishing a release is distinct from pushing source; do not imply one accomplished the other.

`install.ps1` installs a GitHub release downloaded anonymously over HTTPS, accepted only with a release-key signature on its `.sha256` (`$releaseKeys`, the same list as `Updater.ReleaseKeys`), or a local ZIP/checksum without that check. Default legacy root is `%LOCALAPPDATA%\DisplayWeave`, with `versions`, `data` and `current.json`. `-Startup Preserve -NoLaunch` preserves startup and does not launch the app. Keep old version folders while applications may retain their DLLs. See [README install guidance](../README.md#install-or-update) for migration and elevated-instance behavior.

### Setup and release flow

`installer/setup/Setup.cs` builds `DisplayWeave-Setup-<version>.exe` (C# 5, `csc`, no extra tools). It embeds the portable ZIP, its `.sha256` and `installer/install.ps1`; all file work stays in that script, including `-Uninstall [-RemoveSettings]`. The setup only adds a window, the HKCU `Uninstall\DisplayWeave` entry and the `Uninstall DisplayWeave.exe` copy in the install root. It runs `install.ps1` with `-ExecutionPolicy RemoteSigned` (from 1.0.3; `Bypass` before), as do the Start menu shortcut, the startup entry and `StartupRegistration`; the extracted script has no download mark, so it counts as local. The launcher is `installer/Launch-DisplayWeave.ps1`, shipped in the package and copied to the install root (earlier versions generated it from text in `install.ps1`, so the 1.0.3 `install.ps1` cannot install an older package). The uninstaller copy cannot delete itself: from 1.0.3 it copies itself to `%TEMP%\DisplayWeaveUninstall-<guid>` and starts that copy with `--remove-uninstaller`, which deletes it once it exits and the root if empty; the copy stays in `%TEMP%` (before 1.0.3 a `cmd ping … & del` command did this). Non-default roots (`--install-root`, used by tests) are never registered. Its manifest is `asInvoker`, which also stops Windows' installer detection from prompting for elevation. Only CI's release build code-signs it (below); local builds are unsigned.

**Releases are signed with a release key.** Installed copies and `install.ps1` accept a release only when its `.sha256` file carries an ECDSA P-256 signature (`<file>.sha256.sig`, base64) from a key pinned in `Updater.ReleaseKeys` and `install.ps1`'s `$releaseKeys`. Two keys are pinned from 1.0.1, neither exportable: the maintainer's Windows key store (CNG key `DisplayWeave release signing`), and Azure Key Vault, which CI uses after the maintainer approves the release build (user decision, 2026-10-13: whoever controls the GitHub account and that approval can ship an update). The signed line names the file, and so its version, so an old signed setup cannot be republished under a newer tag. The managed self-test fails while no key is pinned, so neither CI nor `release.ps1` can produce a release that installed copies could never verify.

1. Once per computer: `.\scripts\sign-release.ps1 -NewKey` creates the key and pins its public half in both files; commit them. A backup key made the same way on another computer is added with `-PinKey <base64>`. From 1.0.1 a second key is pinned: `release-signing` in the Azure Key Vault `displayweave-release` (EC P-256, sign and verify only, not exportable, 90-day soft delete), whose public half is the ECCPUBLICBLOB of its JWK (`0x45 0x43 0x53 0x31 0x20 0 0 0`, then x and y). Pinned keys reach installed copies only through a release signed with a key they already trust, so pin a backup before the only key could be lost.
2. Bump `src/DisplayWeave/version.txt` and `release.json`, then merge to `main`, and approve the windows-2022 build's `release` environment in GitHub Actions. After its tests, that build signs the two `.sha256` files with the Key Vault key (`sign-release.ps1 -Directory release -KeyVault displayweave-release/release-signing`, through a second OIDC sign-in; it checks that each `.sha256` names and matches its file, that the key is pinned in both files, and each signature against that key). The `release` job runs after both Windows builds pass: if `v<version>` has no release or draft yet, it attests the windows-2022 ZIP and setup (build provenance, Sigstore) and creates a **draft** prerelease with them and their `.sha256` files. The `publish` job then uploads the `.sig` files and publishes it, as the latest full release when the version has no suffix.
3. Without CI, or with the local key: `.\scripts\sign-release.ps1 -Version <version>` downloads the draft, checks with `gh attestation verify` that both files were built by this repository's workflow on `main`, checks that each `.sha256` names its file and matches it, signs both, uploads the `.sig` files and publishes the release. The release that first pins a new key must be signed with a key the installed copies already trust (1.0.1 was signed with the local key).

**Code signing (Authenticode)** is separate from the release key and only gives Windows a publisher name for SmartScreen. When the workflow's `version` job finds that a push to `main` makes a new version, the windows-2022 build runs in the GitHub `release` environment (deployment branch `main` only) and signs through Azure Artifact Signing (account `displayweave`, certificate profile `displayweave-public`, East US). It signs the native helpers before `build.ps1` hashes them into the app, then `DisplayWeave.exe` and `DisplayWeave.Core.dll`, then (from 1.0.3) `install.ps1` and `Launch-DisplayWeave.ps1` before packaging, then the setup, whose `.sha256` it writes again before checking that every binary has a valid signature. It signs in with OIDC: the environment secrets `AZURE_CLIENT_ID` and `AZURE_TENANT_ID` name an app registration whose federated credential trusts only `repo:alexchaoss@45114914/DisplayWeave@1413866795:environment:release` (GitHub's subject carries the owner and repository IDs; the plain `repo:alexchaoss/DisplayWeave:...` form fails with AADSTS700213), with the Artifact Signing Certificate Profile Signer role on that profile and Key Vault Crypto User on the release key only. Other builds, PRs and `release.ps1` stay unsigned.

```powershell
# Everything except publishing: build, managed/native checks, package, installer and setup tests.
.\scripts\release.ps1
# Manual fallback when CI cannot build: after committing and pushing the version bump, the same,
# then it signs locally and creates the GitHub prerelease v<version>. Do not also let CI make a draft of it.
.\scripts\release.ps1 -Publish -NotesFile .\path\to\notes.md
```

CI runs the build job on `windows-2022` and `windows-2025` and the installer tests in both PowerShell 7 and Windows PowerShell 5.1, which users and the setup use. The build uses .NET Framework `csc` and Zig's own headers, so no Windows SDK version is involved.

**Updates:** `src/DisplayWeave/Updater.cs` runs only in an installed copy (`<root>\versions\<version>\` next to `<root>\current.json`). Right after start and then every hour (from 1.0.2; before, a minute after start and every six hours), it reads the public releases anonymously over HTTPS (no account, token or `gh`; versions 0.5.5 to 0.5.9 used the user's signed-in `gh`):
- `GET https://api.github.com/repos/alexchaoss/DisplayWeave/releases?per_page=50` lists the tags; drafts are skipped. `prototype-v*` tags are skipped and the highest `v<version>` wins. Anonymous API calls are limited to 60 an hour per IP address; one check uses one, so the hourly check and five-minute retries stay well under it.
- `DisplayWeave-Setup-<version>.exe`, its `.sha256` and `.sha256.sig` are downloaded from `https://github.com/alexchaoss/DisplayWeave/releases/download/v<version>/` into `%TEMP%\DisplayWeaveUpdate`. `Updater.IsSignedRelease` requires a pinned key's signature over the exact `.sha256` bytes, a line naming `DisplayWeave-Setup-<version>.exe`, and a matching setup hash.
- The app sets TLS 1.2 itself: built by `csc` without a target-framework attribute, .NET Framework otherwise offers only SSL 3 and TLS 1.0, which GitHub refuses.
- `MainForm` then starts `--quiet --install-root <root> --log <setup>.log`, but only while its window is hidden and `FullscreenController.HasExpandedApp` is false. That property is true while a hooked app (a helper session) is maximized or fullscreen in its region, or hasn't yet reported its state. Windows fitted from outside do not count (0.5.11; 0.5.5 to 0.5.10 waited for them too, so with any maximized window their updates waited for the tray item). On the `--exit-for-update` message the app calls `LeaveFittedWindows` instead of returning those windows to their monitor; the new version adopts any maximized window already in its region (`FitMaximized`), so a later ordinary exit still returns it. The setup closes the app through that path and relaunches the new version.
- `InstallUpdate` holds the setup open and runs `IsSignedRelease` again right before starting it.
- The launcher (`$launcherSource` in `install.ps1`) counts starts in `health\<version>.starts` while `current.json` names a `previousVersion`; the app writes `health\<version>.ok` after a minute or on a clean exit. On the third start without it, the launcher points `current.json` back at the previous version and writes `<version>.failed`; `Updater.Newest` skips failed versions. The tray's **Go back to DisplayWeave <version>** (`Updater.GoBack`) does the same and restarts with `--wait-for-exit`. A reinstall of a version clears its `.starts` and `.ok`; uninstall removes `health`. 0.5.12 and older updaters do not skip failed versions.
- A release without the setup, its checksum or its signature makes the check fail (HTTP 404 or "not signed"), and it retries five minutes later (from 1.0.2; six hours before). The setup itself is downloaded only after its `.sha256` and `.sig`. Releases up to 0.5.9 are unsigned; they are older than any copy that checks signatures, so none is offered.

`release.ps1` builds native files into `release\build-<version>` instead of `dist`, because running apps can keep a `dist` DLL loaded (a browser did on the development computer). `tests/setup-tests.ps1` runs the real setup quietly against a temporary root and checks that Settings > Apps, startup and the Start menu stay unchanged. A version folder is immutable once installed, so any package change needs a new version. ZIPs are not byte-reproducible, so a version installed from a local build cannot later be installed from its published rebuild on the same computer; bump the version before publishing in that case. Do not time a setup that relaunches DisplayWeave with Windows PowerShell's `Start-Process -Wait`: it also waits for the relaunched app.

For prereleases, use `gh release list`; an unqualified `gh release view` can report no release when only prereleases exist. Never print authentication tokens or include personal layout/rule files or raw local reports in a commit. Stage explicit intended paths when unfinished source is present. After a docs-only commit, verify the remaining source diff is unchanged, then push and verify the remote commit.

When handing off again, update the tested commit, implemented/proposed distinction, remaining changes, installation reality and test limitations. This lets the next agent continue from evidence without repeating risky desktop experiments.
