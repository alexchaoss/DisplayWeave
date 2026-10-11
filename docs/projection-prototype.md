# Projection prototype

Status: experimental tools, updated October 10, 2026. These tools test the proposed [virtual display architecture](virtual-display-architecture.md) independently of the installed DisplayWeave 0.5.1 application. They are packaged for users as the separate **DisplayWeave Prototype 0.1.0-preview** app ([user guide](prototype-guide.md)); they do not add a projection backend to the legacy UI, ship a DisplayWeave display driver, or implement a production display broker. See the [agent handoff](agent-handoff.md) for the current state.

## Own-app prototype sessions

`DisplayWeavePrototype.exe` runs one bounded session per Start: an elevated `test-virtual-display.ps1 -SelectedAppSession -ReportDirectory <session>\display` lease, a single move of the selected window onto the virtual output (optionally without caption/sizing frame), whole-output projection into the chosen region, optional click-to-enter modal input, then BEGIN/ACK release, window restoration, COMPLETE and display removal. Everything for a session is under `%LOCALAPPDATA%\DisplayWeavePrototype\sessions\<id>`.

`tests/test-prototype-session.ps1` drives the worker against an owned windowed probe. On October 10 the view-only run (`prototype-session-20261010-135322-779`, 2339 frames) and the interactive run with Stop after 10 s (`prototype-session-20261010-135722-861`, 815 frames, presenter-claimed BEGIN acknowledged, focus/clip/cursor restored) both passed with exact window restoration and full display cleanup. The probe was not DXGI-exclusive and entry used one tagged synthetic click, so neither run is evidence of exclusive-mode release transitions or hardware input. No real game has been run through the prototype.

The x86 automated projection sequence has passed with an owned app reporting exclusive fullscreen on a temporary virtual output, verified colors, tagged synthetic keyboard/relative-mouse input, registered-shortcut release, restored focus/cursor/clip, and the original physical topology after display removal. An x64 watchdog-stall sequence also passed guardian recovery and complete display cleanup. Automatic focus return was denied in a separate deadline-release test and is not guaranteed. Physical hardware input, live separate-pointer delivery, process-crash recovery and game compatibility remain unverified. The installed legacy app was stopped for the recorded tests; this backend is not integrated into its UI.

A further 30-second passive demo on October 10 passed with 2522 presents and 30 owned-source exclusive-state samples. Colors, source restoration, controlled release, native lease exit 0, device/configuration removal and final physical topology all passed. Local reports are `test-results/virtual-projection-20261010-082440-062` and `test-results/virtual-display-20261010-082413-101`. The existing driver package was preserved. This demo exercised no input session; it adds no evidence for user-app or hardware-input compatibility.

## Implemented components

| Component | Current scope |
| --- | --- |
| [RegionPresenter](../native/projection/RegionPresenter.cpp) | D3D11 Desktop Duplication capture, GPU crop or whole-output presentation, aspect-fit scaling, GPU cursor composition, bounded frame handling, and JSON measurements. Its nonactivating presentation window does not inject code into the source application. |
| [ProjectionInput](../native/projection/ProjectionInput.h) | Explicit, bounded modal interaction with a verified source HWND/PID/client rectangle. Gives the source keyboard focus and places the hardware pointer on its output, with Ctrl+Alt+F10 release and a separate cleanup guardian. No injected input, application hooks, or continuous pointer warping. |
| [ProjectionProbe](../native/projection/ProjectionProbe.cpp) | An owned D3D11 application with color patches and animation. Records its rendering, focus, input and fullscreen state. Virtual-output tests sample its reported exclusive state; physical crop tests do not exercise exclusive fullscreen. |
| [RegionDisplayLease](../native/projection/VirtualDisplayLease.cpp) | Read-only physical display preflight, plus a bounded, temporary software-device fixture for an explicitly supplied upstream VDD package. Correlates the new display with the exact created PnP instance and checks physical display invariants. |
| [ProjectionPlan](../native/projection/ProjectionPlan.h) | Pure layout and apply-revision validation: stable IDs, integer physical pixels, non-overlapping half-open rectangles, negative coordinates, overflow checks, capability bounds, and preservation of physical display state. |
| [ProjectionProtocol](../native/projection/ProjectionProtocol.h) | A proposed fixed-width version 1 monitor-request format with bounded records and validation. No WDF request handler, service, authenticated IPC or driver implementation exists behind it. |

The model keeps a virtual source output and its physical destination rectangle distinct. It accepts up to nine regions, defaults to a complete partition, and checks an expected applied revision before preparing a change. Default limits are 16,384 pixels per dimension and 64 MiPixels both per output and in aggregate. Version 1 source modes are SDR with rational refresh rates. These are validation limits, not a claim that a driver or GPU can supply every accepted mode. Commit validation checks revisions and physical invariants; it does not establish capture or input readiness.

## Build and read-only checks

Run from the repository root. The build uses the portable compiler provisioned by `fetch-tools.ps1` and writes separate prototype binaries:

```powershell
.\scripts\build-projection.ps1 -Architecture all -OutputDirectory .\release\projection-prototype -RunSelfTests
```

Both `projection-x64` and `projection-x86` contain `RegionPresenter.exe`, `ProjectionProbe.exe`, `RegionDisplayLease.exe`, `ProjectionPlanTests.exe`, and `ProjectionInputTests.exe`. The default output directory, when omitted, is `dist`. This build does not update the installed application.

The current checks pass for each architecture: **118 presenter checks, 219 lease checks, 101 layout/protocol checks, and 57 input policy checks** (the additions cover the paired lease release gate, click-to-enter options, the single BEGIN claim and event-name validation). Presenter checks include bounded cursor decoding, monochrome AND/XOR, masked-color XOR/replacement, color alpha, coordinate overflow, CLI validation, and compilation of the production shaders. Input checks exercise coordinate mapping, conditional restoration, competing cleanup claims and focus acknowledgment. Lease checks include the bounded release state machine, permitted refresh transition and exact resolved physical topology after removal. These checks create no capture session, window, display device or input session; shader compilation runs on the CPU. They are not a substitute for live rendering or input tests.

A separate `RegionPresenter.exe --self-test-cursor-gpu` diagnostic renders tiny owned textures through the exact production shader, then checks color alpha, monochrome AND/XOR, masked color, clipping and hidden-pointer cases by bounded GPU readback. It uses a hardware D3D11 device but creates no window, desktop capture, input session or display. It is separate from `--self-test` and is not invoked by `-RunSelfTests`; a pass tests shader output, not DDA pointer delivery or hardware input.

The recorded offscreen run `test-results/cursor-gpu-20261009-224911-959` passed on x64 and x86. Each used the AMD Radeon RX 7900 XT, completed seven cases and seven readbacks, checked all 224 pixels with no failures, and took 203 ms. The cases cover color alpha, all monochrome mask outcomes, masked-color replacement/XOR, left and bottom-right clipping, hidden pointers, and an offscreen pointer. This validates production-shader output on owned textures; it does not validate live DDA cursor metadata, input delivery, or recovery.

These commands inspect display metadata without creating displays or capturing application windows:

```powershell
.\release\projection-prototype\projection-x64\RegionPresenter.exe --list-outputs
.\release\projection-prototype\projection-x64\RegionDisplayLease.exe --preflight
.\release\projection-prototype\projection-x64\RegionDisplayLease.exe --preflight-refresh-aware
```

Preflight records physical source and target modes, refresh rates, monitor bounds, primary status, work area, and advanced-color state. It fails closed when required state cannot be observed. The live preflight on the test host passed: 7680 × 2160 at 120 Hz, primary, work area 7680 × 2100, and active 10-bit HDR. The local record is `test-results/virtual-display-20261009-220721-678/physical-before.json`. Test reports are local, ignored artifacts and are not included in the repository.

## Physical-display capture test

Exit DisplayWeave from its tray menu before running the isolated fixture; closing its main window with X only hides it. The fixture opens its own source and presenter windows at separate positions on the primary display:

```powershell
.\tests\test-projection.ps1 -Architecture all -BuildDirectory .\release\projection-prototype -Seconds 6
```

`-Seconds` accepts 3–30. The script verifies the owned source process and window, checks that RegionHook is absent, validates captured color patches, closes only its own processes, and compares display inventories before and after. It does not add a virtual display, activate exclusive fullscreen, or forward input. Desktop Duplication acquires an output texture; the presenter then extracts the fixture rectangle. Reports contain metadata, counters and sampled fixture colors, not saved desktop screenshots.

The six-second local run `test-results/projection-20261009-220457-355`, recorded before cursor composition and modal input were added, produced:

| Measurement | x64 | x86 |
| --- | ---: | ---: |
| Captured / released frames | 710 / 710 | 707 / 707 |
| Presented frames | 707 | 701 |
| Busy presents dropped | 3 | 6 |
| Failed presents | 0 | 0 |
| Capture / presentation errors | 0 / 0 | 0 / 0 |
| Recovery attempts | 0 | 0 |
| Desktop update to present call, p50 | 0.165 ms | 0.199 ms |
| Desktop update to present call, p95 | 0.319 ms | 0.386 ms |
| Desktop update to present call, p99 | 0.421 ms | 0.519 ms |

Sources: `x64-presenter.json` and `x86-presenter.json` in that local run folder. Both runs used the AMD Radeon RX 7900 XT and the 7680 × 2160, 120 Hz physical output. The source client crop was `(89, 138, 622, 313)` and the destination was `(900, 100, 640, 360)`.

A passive regression with the new presenter, `test-results/projection-20261009-224404-261`, also passed on both architectures for six seconds: x64 captured/released 708 frames and presented 705 (3 busy); x86 captured/released 693 and presented 686 (7 busy). Both had zero failed presents, capture/presentation errors or recoveries, and exact matches for all eight fixture patches. It used the same 120 Hz physical display and 240-nit SDR-white conversion. Input was not requested and DDA reported no visible separate pointer, so that run does not exercise cursor composition or establish input/fullscreen behavior.

The physical source was HDR, so the explicitly enabled fixture conversion used FP16 scRGB capture (`R16G16B16A16_FLOAT`). Its observed SDR white-level value was 3000, equivalent to 240 nits; the shader normalized by 1000/3000, clipped to SDR, and encoded sRGB. All four RGB patches and four gray patches matched exactly in one verification readback per run. This verifies that SDR fixture path on this host; it does **not** preserve HDR or validate general HDR tone mapping.

The timing above ends at the present call and excludes input processing and physical scanout. It is not input-to-photon latency. Presented desktop frames count successful present submissions and can repeat unchanged source content; they are not counts of unique application frames or measured scanout. The owned source submitted 402 frames in each run with no failed presents. RegionHook was absent, but a third-party display tool's resident modules were detected in both source processes, so these results are not evidence from an environment free of third-party hooks. Source records are `x64-source.json` and `x86-source.json` in the same run folder.

## Temporary virtual-display fixture

**Native lifecycle, x86 shortcut release and x64 watchdog-stall sequence passed.** Earlier run `test-results/virtual-display-20261009-224224-747` verified the temporary device's ownership but could not restore the baseline physical path refresh during startup. Its one-time `SetDisplayConfig` validation returned Windows error 31. No apply call followed (`refreshRestore.applyCalled:false`); the report's default `applyError` is not the result of an attempted apply. No source application, presenter, or input session was started in that run. Later candidate normalization passed validation and apply, reached lease readiness, and enabled the virtual-output rendering tests below.

That run removed the exact owned device and generated configuration, preserved the pre-existing pinned `oem106.inf` package, and observed the original physical state after removal. `result.json` reports no cleanup errors. An earlier cleanup script had selected the wrong journal and left a non-present fixture instance registered; that exact instance was removed explicitly, and the script was corrected before these recorded automatic cleanup passes.

The first detailed preservation failure, `test-results/virtual-display-20261009-221416-213`, recorded these differences immediately after device arrival:

| Existing physical output | Baseline | Observed during arrival |
| --- | --- | --- |
| Desktop size and position | 7680 × 2160 at (0, 0) | Same |
| Reported target/path refresh | 12000/100 (120 Hz) | 240/1 (240 Hz) |
| Target pixel clock and total dimensions | 4,233,600,000; 7840 × 2250 | Same |
| Work-area bottom | 2100 | 2160 |
| Primary and 10-bit HDR state | Enabled | Same |

These values came from `QueryDisplayConfig(QDC_ONLY_ACTIVE_PATHS)`. They do not establish whether the scanout rate changed. A subsequent read-only baseline check with virtual-mode/refresh awareness (`0x52`) reported a 240 Hz target signal and 120 Hz path refresh simultaneously; path flags were `9`, with `BOOST_REFRESH_RATE` unset. The legacy view (`0x02`) reported 120 Hz in both fields. Both views retained the original taskbar area and HDR settings. This difference is not proof of DRR or of a physical scanout change. The added diagnostic correctly decodes packed mode indexes and records both the low 16-bit video standard and its separate divider. [Query flags](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-querydisplayconfig), [refresh and path flags](https://learn.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_path_info)

The observed work-area change was independently sufficient to reject that earlier run. The helper closed the device without opening a source application or presenter. Later startup handling allows up to 10 seconds for shell/display state to settle, requires 750 ms of continuous matching observations, and resets that interval after an observation gap over 500 ms. It records startup mismatches rather than claiming the physical state was unchanged throughout arrival. Baseline, active and removal checks use one consistent refresh-aware query mode, with a pre-creation legacy fallback only when the newer query is unsupported.

A narrowly checked, one-time startup transaction can request the original physical path refresh when all other physical fields already match. It preserves physical target signal data, validates the supplied configuration before applying it, and still requires the full original physical state afterward. If the first representation fails validation, a separately checked normalized candidate must itself validate before any apply. The later run `test-results/virtual-display-20261009-225457-356` selected that candidate and recorded validation/apply success. The active physical-state guard remains strict until an explicit release is acknowledged. The helper also observed a third-party display tool's DLL in itself and no RegionHook, so these observations include that resident component.

At initial arrival the virtual output had an 800 × 600 desktop source and a 1280 × 720 target signal, despite the XML requesting 1280 × 720. The owned fullscreen source subsequently established a 1280 × 720 capture surface. Configuration text therefore cannot substitute for validating the applied mode. The driver also advertises an HDMI connector type; ownership must come from exact PnP ancestry, not a connector label. The fixture is not accepted as a production provider.

[test-virtual-display.ps1](../tests/test-virtual-display.ps1) requires an elevated PowerShell session and an explicitly supplied, extracted upstream package. It pins release **25.5.2**, asset **Signed-Driver-v24.12.24-x64.zip**, checks the exact SHA-256 hashes of `MttVDD.inf`, `MttVDD.dll` and `mttvdd.cat`, and requires valid expected-publisher signatures for the DLL and catalog. It neither downloads a package nor changes Windows signature enforcement.

```powershell
.\tests\test-virtual-display.ps1 `
  -DriverPackageDirectory 'C:\path\to\extracted\pinned-driver' `
  -BuildDirectory .\release\projection-prototype `
  -Seconds 45
```

This fixture makes temporary system changes: it stages the pinned driver package if absent, creates `C:\VirtualDisplayDriver\vdd_settings.xml` for one 1280 × 720, 60 Hz output, and creates a handle-bound software device. It refuses an existing VDD device, configuration directory or vendor registry configuration. It also refuses a previously staged package unless `-UseExistingPinnedPackage` is explicitly supplied; that option verifies all three driver-store files against the same hashes and preserves the existing package. It does not reuse an existing display device.

`-Seconds` accepts 15–120. One native deadline bounds device creation and the live lease to at most 120 seconds; shutdown verification and package cleanup can take additional time. This is a device lifecycle test, not a virtual-output capture or game test. The upstream driver is a fixture dependency, not a DisplayWeave driver implementation or bundled installer.

While the device is present, the helper checks the original physical outputs' source and target modes, refresh rates, positions, primary status, monitor/work-area bounds, and advanced-color state. It accepts a new output only after correlating its adapter with the exact callback-created PnP instance. Missing ownership or invariant observations fail the fixture.

Cleanup closes the lease, removes only the exact owned PnP instance, and removes only a package newly staged by this run. If explicitly reusing a pinned package, it verifies that package remains unchanged. It removes the generated configuration only if its content still matches, and preserves unexpected files for inspection. Finally it compares physical display state with the preflight snapshot. Device unplugging and driver-package removal are distinct operations; closing a software-device handle alone does not uninstall a package. [Microsoft software-device lifetime documentation](https://learn.microsoft.com/en-us/windows/win32/api/swdevice/nf-swdevice-swdeviceclose)

Each run writes journals, command logs and `result.json` under `test-results/virtual-display-*`. A preservation failure records its exact field and before/observed values, plus a `lease.json.physical-change.json` snapshot journal before closing the device. Success requires device removal, owned-package removal or verified preservation of the reused package, configuration cleanup, the original physical state after cleanup, and no cleanup errors. A reported cleanup failure requires investigation; this fixture does not claim crash-proof installation or recovery.

An explicit two-phase release coordinates input cleanup and source fullscreen exit. The ready journal supplies private BEGIN/COMPLETE event names restricted to the current user and SYSTEM. The harness signals BEGIN and waits for a matching `lease.json.release.json` acknowledgment before triggering release. This opens a non-renewable window of at most five seconds. Within it, only a physical path-refresh change matching the unchanged physical target signal is permitted; other physical invariants and the owned virtual output's identity remain checked. The virtual source mode can change as its application leaves fullscreen. COMPLETE is accepted only after BEGIN, and the harness sends it only after presenter input cleanup and source exit.

The helper then closes its owned display and requires the exact original physical topology within its removal check. Final reports separate `physicalPreservedSinceReady` from `physicalRestoredAfterRelease`, `releaseCompleted`, and `originalTopologyObservedAfterClose`: a permitted release transition makes the first false and can still finish with verified restoration. This is a bounded transition policy with a final-state requirement, not a claim that the physical state stays untouched throughout startup and release.

## Virtual-output projection test

[test-virtual-projection.ps1](../tests/test-virtual-projection.ps1) has passed its x86 automated exclusive-projection/synthetic-input/shortcut-release sequence and an x64 guardian-stall sequence. It depends on a live, verified temporary lease; it does not create or prolong that lease. Each projection run consumes one lease through the coordinated release. Run the lifecycle fixture in one elevated PowerShell session with enough time remaining, then use its absolute `lease.json` report path in a second session after `lease.json.ready.json` exists:

```powershell
.\tests\test-virtual-projection.ps1 `
  -LeaseReport 'C:\absolute\repository\test-results\virtual-display-RUN\lease.json' `
  -BuildDirectory .\release\projection-prototype `
  -Architecture x64 `
  -Seconds 6
```

The script accepts `x64` or `x86` and 3–30 seconds. It checks the lease process identity, executable path, journal identity, remaining lifetime, display ownership and physical-state guard. It then starts only the owned probe, requests exclusive fullscreen on the leased output, presents its verified color patches at `(900, 100, 1280, 720)` on the physical primary display, and asks the probe to return to windowed mode before closing. The destination must fit that display. It records the application's DXGI fullscreen report rather than assuming a successful API call proves exclusive physical scanout.

Success requires verified fixture colors, sampled source fullscreen state, successful source restoration, acknowledged BEGIN/COMPLETE release, lease exit, removal of the owned virtual output, and matching remaining physical inventory/topology. Mode-array indexes are not stable identities; resolved physical fields are compared. The script closes only its own probe and presenter and signals the lease's private release protocol. The original lifecycle fixture remains responsible for PnP instance, package and configuration cleanup. Its default mode is passive and launches no games.

The passive x64 run `test-results/virtual-projection-20261009-225525-609` captured, released and presented 468 frames in six seconds with zero errors or recoveries and exact matches for all eight color/gray patches. Five source snapshots reported exclusive fullscreen on the owned virtual output. The SDR BGRA capture surface was 1280 × 720 at 60 Hz; presentation occupied `(900, 100, 1280, 720)` on the physical display. Overall acceptance failed when source fullscreen exit changed the physical path refresh from 120 to 240 Hz and the virtual desktop mode returned to 800 × 600; the old strict guard ended the lease. Its paired lifecycle fixture nevertheless removed the owned device/configuration, preserved the existing package and restored the physical baseline. This observation motivated the explicit release protocol above, rather than silently relaxing active-session checks.

The first interactive run, `test-results/virtual-projection-20261009-230540-413`, published verified active readiness and recorded four exclusive-state samples. The source received a tagged key-down/key-up pair and a tagged relative mouse event with delta `(4, 2)`; raw counters recorded one mouse and two keyboard packets. The presenter verified eight exact patches and submitted 304 frames with no capture/presentation errors. The overall run failed when the harness could not signal BEGIN because of a PowerShell/.NET event-overload mismatch. Cleanup restored cursor and clip but reported unsuccessful focus restoration. The harness now uses the explicit Windows event API, and focus return observes the outcome of a single request for up to 250 ms. The source contained a third-party display tool's resident component and no RegionHook; these are not measurements from an environment free of third-party hooks.

The subsequent run `test-results/virtual-projection-20261009-231155-841` acknowledged BEGIN successfully, captured/released 463 frames, presented 462 (one busy), verified all eight patches, and recorded six exclusive-state samples. Windows returned false from the automatic focus-return request; the report correctly identifies rejection rather than inferring it from an immediate foreground observation. Cursor and clip restored, but focus did not, so COMPLETE was withheld and overall acceptance failed. The release window expired; the native lease observed the exact original topology after closing, and the paired elevated lifecycle fixture removed its device/configuration and verified physical restoration.

Registered-shortcut x64 run `test-results/virtual-projection-20261009-231619-853` verified tagged keyboard/relative-mouse input and four exclusive-state samples. It captured/released 318 frames, presented 317 (one busy), matched all eight patches and reported no rendering errors or recoveries. The presenter received Ctrl+Alt+F10 through its registered hotkey, reported `release_shortcut`, and restored focus, cursor and clip without incomplete cleanup. The paired native/elevated lifecycle run `test-results/virtual-display-20261009-231543-315` passed: COMPLETE accepted, original physical topology restored, owned device/configuration removed and existing package preserved. The projection harness nevertheless marked its run failed at the external lease exit-status check. These component passes do not overwrite that recorded harness failure.

After correcting the exit-status observation, the x86 sequence `test-results/virtual-projection-20261009-231943-361` passed the full projection harness. It captured/released/presented 316 frames, recorded four exclusive-state samples, matched all eight patches, and had zero rendering errors, busy presents or recoveries. Tagged keyboard/mouse input and the registered shortcut were verified; focus, clip and cursor restored with no incomplete cleanup. BEGIN/COMPLETE were acknowledged, the native lease exited with observed code zero, the owned virtual output disappeared, and remaining physical inventory/topology matched. The run recorded `hardwareInputVerified:false`, `physicalReleaseShortcutVerified:false`, and no visible separate pointer. Its paired lifecycle report, `test-results/virtual-display-20261009-231910-148`, also passed device/configuration cleanup and preserved the existing pinned package.

The x64 guardian run `test-results/virtual-projection-20261009-232049-844` passed the complete watchdog-stall sequence. After verified readiness it paused presenter heartbeats for 3516 ms (3500 ms requested); the guardian's focus request was accepted, and focus, clip and cursor restored with `guardianRecovered:true` and no incomplete cleanup. Two source snapshots reported exclusive fullscreen during interaction. In the 3.75-second run, the presenter captured/released nine frames, presented eight (one busy), matched all eight patches and reported no rendering errors or recoveries; the low frame count reflects the deliberate stall. COMPLETE and native exit zero were observed, the virtual output was removed, and physical inventory/topology matched. Paired lifecycle `test-results/virtual-display-20261009-232022-290` passed exact device/configuration cleanup, package preservation and physical restoration with no cleanup errors. No synthetic keyboard/mouse exercise preceded this stall. This proves that one watchdog-stall scenario, not process-crash recovery or focus return after arbitrary input histories.

Add `-Interactive` to exercise the explicit modal input session against the owned probe. The harness waits for `<presenter-report>.active.ready.json`, verifies the source identity, focus, clip and guardian, and checks restoration on release. `-ExerciseSyntheticInput` additionally sends narrowly scoped, tagged test events only after that readiness check; it requires `-Interactive`. Those events and the single entry pointer warp are not evidence of physical-device delivery, even when the source's raw-input counters advance. Hardware validation needs a post-entry input baseline and actual user mouse/keyboard activity. The harness reports `hardwareInputVerified:false` for synthetic exercises.

`-ExerciseReleaseShortcut` requires `-Interactive` and can accompany `-ExerciseSyntheticInput`. After BEGIN acknowledgment it sends a tagged Ctrl+Alt+F10 sequence, guarded by both owned processes' identities and the source's foreground/clip state. The presenter must receive its registered Windows hotkey and report `release_shortcut`, accepted/restored focus and restored cursor/clip. The harness does not post a fake hotkey message. This remains synthetic shortcut delivery, not a physical-key test.

Optional `-ExerciseGuardianRecovery` requires `-Interactive`, at least six seconds, and neither `-ExerciseSyntheticInput` nor `-ExerciseReleaseShortcut`. It asks the owned presenter to stall its heartbeat for 3500 ms after active readiness. The harness acknowledges BEGIN before the watchdog deadline, then requires actual guardian recovery and restored focus, clip and cursor with no incomplete cleanup before COMPLETE. The recorded x64 scenario above passed; crash recovery remains a separate test.

## Cursor and modal input controls

The presenter composes separate DDA pointer metadata in its GPU pixel shader. It supports color alpha, monochrome AND/XOR masks, and masked-color replacement/XOR, with bounded shape decoding and uploads only when the shape changes. Pointer position is the shape's output-relative top-left; the reported hotspot is informational and is not subtracted again. Pointer rendering is clipped to the source crop. No desktop CPU readback is needed for cursor composition. A non-visible separate pointer can mean that the application hides it or that it is already present in the captured image. [Microsoft pointer metadata](https://learn.microsoft.com/en-us/windows/win32/direct3ddxgi/desktop-dup-api), [pointer formats](https://learn.microsoft.com/en-us/windows/win32/api/dxgi1_2/ne-dxgi1_2-dxgi_outdupl_pointer_shape_type)

For direct presenter use, the capture CLI remains:

```text
RegionPresenter.exe --source-device \\.\DISPLAYn --destination x,y,width,height --seconds 1..600 --report absolute-path
```

`--fixture-source-rect x,y,width,height` selects an explicitly owned fixture crop. `--verify-fixture-colors` enables one verification readback. `--allow-sdr-conversion` opts that fixture into the diagnostic HDR-to-SDR conversion; both require the fixture crop. Source pixels and the destination must not overlap.

Interactive mode additionally requires both `--interactive-source-hwnd <hex-or-decimal>` and `--interactive-source-pid <decimal>`. The source client must exactly equal the captured rectangle, and source and projected image must occupy different outputs. The session starts only after a valid projected frame and cursor path, plus completion of fixture color verification when requested. By default, the pointer must be inside the projected image when entry occurs. `--interactive-entry-center` permits an explicit fixture to enter at source center when the pointer is elsewhere; it requires the fixture crop and source identity. Optional `--interactive-return-hwnd <hex-or-decimal>` identifies a verified window to receive focus on normal release.

The diagnostic `--fixture-input-stall-ms 2800..3500` additionally requires a fixture crop, explicit interactive source, and a duration of at least six seconds. It stalls once after publishing readiness, then reports whether the stall completed and whether the guardian actually recovered. It is solely an owned-fixture recovery test.

The presenter stays nonactivating. The input component asks Windows once to focus the source, moves the real cursor once into its client, and confines it there for the bounded session. Subsequent hardware events go directly to that focused application, including its own raw-input path; the presenter does not use `SendInput`, register raw input, install input hooks, or continuously remap movement. This is a modal interaction model, not simultaneous free interaction with other desktop regions.

**Ctrl+Alt+F10 releases interaction.** Escape remains an ordinary key for the source application. Closing the presenter, reaching the deadline, losing source focus or identity, changing source geometry or display topology, or losing capture also ends the session. Cleanup restores only the cursor/clip state it still owns and avoids overriding a new application clip or stealing focus after a focus-loss cancellation. A separate guardian watches the exact presenter process and heartbeat, and conditionally restores owned state after a crash or stall. Its private, nonce-named mapping is restricted to the current user and SYSTEM. The guardian is armed before entry mutations; competing or late cleanup is coordinated. Restoration is conditional and best effort: if a live presenter hangs inside cleanup, the guardian avoids racing another global-cursor restoration and cannot promise an absolute recovery deadline. A failed or still-pending restoration is reported, not treated as success. Live crash recovery and application-specific clipping behavior still require validation.

The active journal records `pid`, `hwndValue`, `sourcePid`, `sourceHwndValue`, `source`, `destination`, `projectedImage`, `inputActive`, `focused`, `clip`, `guardianReady`, and the release shortcut. The final report separates pointer visibility/shape/draw counters from the `input` object, including entry, guardian and restoration results. Focus diagnostics distinguish a rejected Windows request, an accepted request whose activation was not observed within 250 ms, and successful return; an unrelated foreground change cancels observation. Guardian focus results are reported separately. The failed deadline-release run demonstrates that Windows can deny automatic focus return even though the shortcut and isolated stall scenarios passed. `input.mode` is `modalHardwareDirect`; `inputForwarding` and `injection` remain false. Source fullscreen state and raw-input observations come from the owned probe's reports; the presenter does not independently certify either.

## Remaining acceptance gates

Before this can become an application backend, recorded tests must establish:

- Repeatable temporary display lifecycle, exclusive projection and coordinated release across both architectures and longer sessions, beyond the recorded x86 shortcut and x64 watchdog-stall passes.
- Physical hardware keyboard and raw/relative mouse input, cursor clipping and focus recovery that remain usable when the application renders on a different output; synthetic reception alone is insufficient.
- Live DDA delivery and composition of separate pointer shapes, plus crash recovery and input restoration under additional focus/input histories.
- Multi-output layouts, resizing, hotplug, sleep, device loss, process failure and recovery without stale outputs or a changed physical desktop.
- Appropriate capture/presentation latency and resource use on virtual outputs; HDR and rotated outputs need separate support and validation.

Cursor shader output, the owned x86 exclusive-projection/synthetic-input/release sequence and the x64 guardian-stall sequence have measured passes. Production lifecycle and hardware-input acceptance remain open. The presenter does not synthesize or forward input. Its ordinary path supports SDR capture; the HDR conversion above is explicitly limited to the owned fixture. No Diablo IV or other game compatibility, anti-cheat compatibility, terms-of-service approval, universal exclusive-fullscreen support, or end-to-end gaming latency has been established by these tests.
