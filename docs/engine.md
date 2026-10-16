# DisplayWeave engine notes

Developer-level detail moved out of the README. For the user guide see [README.md](../README.md).

## Launcher exclusion

Version 0.4.3 excludes one game launcher from region hooks because crashes have been
reported while it is hooked; `LauncherProcessProtection` in
`src/DisplayWeave.Core/ProcessProtection.cs` lists its executable names. The
exclusion applies to automatic handling, manual Force rules, saved rules, and direct
native-helper requests. It matches complete executable names, not folders or parent
processes, so games started by the launcher remain eligible for region handling.

After updating, exit and reopen the launcher if it was already running with an
older DisplayWeave component. Pausing DisplayWeave does not unload that resident
component. The launcher runs at its normal size; region rules still apply to
eligible games.

## Apps running as administrator

DisplayWeave normally runs with ordinary user permissions, including when it
starts with Windows. Version 0.4.2 identifies a confirmed mismatch when an app
runs as administrator and DisplayWeave does not. A generic Windows access-denied
error (error 5) can have other causes; it does not by itself prove that elevation
is needed.

To handle an app already running as administrator, open **Status & help** and
choose **Restart as administrator**. DisplayWeave saves the layout and uses the
normal Windows UAC prompt. Approving restarts it with administrator access for
this session, preserving the settings directory and saved app rules. Cancelling
leaves the current DisplayWeave instance running. The restart does not change
your startup preference, add a service, bypass Windows protections, or make
administrator access mandatory for ordinary use.

For ordinary apps managed by an elevated controller, newly created control
objects retain the creator's default access list and use a medium integrity
label so the app can acknowledge commands. Elevated targets retain their default
security. Existing objects are never relabeled.

With administrator access, DisplayWeave starts its native helpers from a copy in
`%ProgramFiles%\DisplayWeave`, which only administrators can change, and removes
the copy when it exits. From 0.6.0 it copies only the helper files its build lists and checks each
copy against the SHA-256 recorded when it was built; a changed helper stops the
administrator start with a message. A copy still loaded by a running app is removed at the next
administrator start. Administrator mode never checks for or installs updates. The app itself still starts from
the install folder your account can change; [SECURITY.md](.github/SECURITY.md) lists what that allows.

While running with administrator access, reopen settings from DisplayWeave's
tray icon. Use **Exit DisplayWeave** from the tray before running the ordinary
updater. Windows can block messages from a normal launcher or updater to an
administrator instance. After exiting it, an ordinary launch runs normally again.

Matching permissions does not establish compatibility with a game's graphics API
or protections.

## Fullscreen engine

Version 0.5 uses a USER32 monitor/window engine. From 0.5.11 it is loaded only into
apps with a Force rule. Such an app sees the selected display as the configured virtual regions. Monitor enumeration,
monitor lookup, monitor/work-area information and primary screen metrics agree
with that layout. Other physical displays stay available. Window positioning,
placement and maximize messages constrain the selected window to its region.
Normal windows remain movable in automatic mode.

The component intercepts `EnumDisplayMonitors`, `MonitorFromPoint`,
`MonitorFromRect`, `MonitorFromWindow`, `GetMonitorInfoA/W`, `GetSystemMetrics`,
`SetWindowPos`, `MoveWindow`, and `SetWindowPlacement`. It subclasses only the selected window
through the documented window procedure API. Layout changes are delivered through
an optional shared-memory sidecar without breaking compatible older controllers.

Each managed process gets an architecture-matched helper. Startup first uses
thread-specific Windows message hooks. If no startup callback arrives, the helper
uses ordinary Windows process permissions to load DisplayWeave's DLL and call its
exported initializer. This fallback does not depend on the application's message
filters. Loading and initialization share a bounded deadline, and diagnostics
identify the failed stage. No global hook, display driver, administrative service,
or security-policy change is installed.

Version 0.5.1 corrects loader-failure reporting. When the load thread ends but
DisplayWeave's DLL is absent, earlier 0.5.0 helpers supplied error 1114 themselves;
that was not the target thread's `GetLastError`. The new diagnostic reports the
observed thread result, process state, and module verification separately. A raw
thread result is not interpreted as a Windows loader error or a full module handle.
The original loader error remains unknown when it was not captured in that thread.

The new engine does not intercept DirectX or alter swapchain fullscreen state.
It targets applications that size their fullscreen or maximized windows using
Windows monitor/window APIs. Genuine hardware-exclusive scanout is not divided
into regions, and applications that bypass these APIs may remain incompatible.

The native component stays loaded until the target application exits. Ignore,
pause, exiting DisplayWeave, and controller heartbeat expiry make its hooks pass
through and request restoration of the window. Restart the target application
to unload the component completely. Depending on an application's own fullscreen
state, its fullscreen control may be needed after release. No unrelated application
is forcibly closed during an update.

Version 0.4.1 reconnects to an existing compatible component after its previous
controller exits. It checks the target window, protocol, and exclusive controller
ownership, then waits for fresh acknowledgements before resuming handling. A
different installation folder alone no longer requires restarting an app.

A reconnected component keeps the engine it was loaded with until the target app
restarts; a new installation does not replace code inside a running app. The
component also takes over any other window of the same app, such as a second
browser window. From 1.1.3 this works for browser windows too: earlier versions
refused to reconnect to them (Windows error 1400), so a browser with a Force rule
had to be reopened after every update or restart of DisplayWeave.

A component from an earlier build under another name or native protocol stays
loaded but inactive. DisplayWeave loads its current component beside it, so the app
does not need a restart. A restart is needed only when a loaded DisplayWeave
component could not install its hooks or its control data is damaged; only these
cases show a restart instruction. Close and reopen the target app, then choose
**Save layout** or **Apply saved rules** to retry. Closing
a window may leave some apps running in their tray. App-specific failures remain
visible in the footer and **Status & help** even when another app reconnects.

To update a portable copy, use **Exit DisplayWeave**, extract the new release to
a separate folder, and copy `layout.xml` and `rules.xml` there. The installer is
recommended for subsequent updates because it keeps settings separate from
version files. Release archives contain no personal settings.

## Scope

- Consistent virtual-monitor geometry for 32-bit and 64-bit Win32 applications.
- Automatic maximize and borderless fullscreen handling from outside the app, with
  Force rules that load the component for apps that need it.
- A Direct3D 11 windowed rendering fixture exercises monitor APIs, layout changes,
  restoration, loading, and continued rendering; see [docs/testing.md](docs/testing.md) for actual results.
- No graphics-API exclusive-fullscreen interception in the new engine. Commercial
  game compatibility remains unverified.
- A confirmed elevation mismatch can be addressed with the explicit administrator
  restart above. Protected applications may still reject the hook; no protection
  bypass is attempted.
- Apps that are not DPI-aware are confined at the wrong size when Windows display
  scaling is above 100%. DPI-aware apps, which include most games, fit their region
  exactly.
- One selected window per process, one editable monitor layout, up to nine custom
  regions. Different independent processes can occupy different regions.

This is a testable first version, not a claim of compatibility with every app.

## Build and test

These commands apply to the source project, not the portable ZIP.

```powershell
.\scripts\fetch-tools.ps1
.\scripts\build-native.ps1 -Architecture all
.\scripts\build.ps1
.\dist\DisplayWeave.exe --self-test managed-test-results.txt
.\dist\native-v2-x64\RegionHookHost.exe --self-test-policy
.\dist\native-v2-x86\RegionHookHost.exe --self-test-policy
.\scripts\package.ps1
.\tests\test-monitor.ps1 -Architecture all
.\tests\test-monitor.ps1 -Architecture all -BootstrapLoader -FilteredMessages
.\tests\test-monitor.ps1 -Architecture all -HeartbeatExpiry
```

`fetch-tools.ps1` downloads Zig 0.13.0 and checks it against a SHA-256 pinned in the
script. MinHook v1.3.4 is vendored as sources and license only. The toolchain stays
inside the workspace.
The C# build uses Windows' .NET Framework compiler. Build the native files first:
`build.ps1` records their SHA-256 in the app for administrator mode. Native integration tests open
temporary DirectX test windows and attach only to those test processes. The monitor fixture keeps its Direct3D swapchain windowed throughout. Legacy DXGI
fixtures remain available for testing older resident components only.

The app is in `src/DisplayWeave`, its engine in `src/DisplayWeave.Core`, its native
code in `native/legacy`; build scripts are in `scripts/`, installers in `installer/` and fixtures in `tests/` (see the
[runbook](docs/development.md#repository-layout)). MinHook's license
is preserved in `third_party/minhook/LICENSE.txt` and copied into the distribution.
GitHub Actions is configured to build both architectures, run noninteractive checks,
and upload the portable packages and test reports. `src/DisplayWeave/version.txt` and
`release.json` define the package version.

## Use the engine without the UI

The region engine is a separate library, `DisplayWeave.Core.dll`, built from
`src/DisplayWeave.Core`. It is compiled without any UI reference, so UI code cannot
creep into it. `RegionService` loads the layout and app rules that the app saves,
runs the engine, and offers the app's actions as methods that return a message for
the user:
- `Assign` a window to a region, as Force fullscreen or Maximize in region
- `Ignore` an app, `Release` a window, `RemoveRule`
- `Pause`, `ApplySavedRules`, `ApplyLayout`

- `SwitchProfile`, `CreateProfile`, `DeleteProfile` and `Profiles` for layout profiles,
  and `RebindMonitor` after Windows renames a display

`Layout`, `Rules`, `Shortcuts` and `Engine` expose the full state, and save failures surface as
`IOException`. Call `ActivityLog.Open(data)` to keep the same activity log as the app. `Engine.HandledApps()` lists each handled app with its state, region
and error; `Engine.LayoutProblem` says why the layout cannot be used. A minimal host:

```csharp
// csc /r:DisplayWeave.Core.dll /r:System.Drawing.dll Headless.cs
using System;
using System.Threading;
using DisplayWeave;

static class Headless
{
    static void Main()
    {
        WindowManager.EnableDpiAwareness(); // the engine works in physical pixels
        string data = Environment.ExpandEnvironmentVariables(@"%LOCALAPPDATA%\DisplayWeave\data");
        bool stop = false;
        // Ctrl+C ends the loop, so disposing the service returns windows to normal.
        Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; stop = true; };
        using (RegionService service = new RegionService(data))
            while (!stop) { service.Tick(); Thread.Sleep(250); } // always from the same thread
    }
}
```

Put the host next to `DisplayWeave.Core.dll` and the `native-v2-x64` and
`native-v2-x86` folders; the engine starts its native helpers from there. A host that
runs as administrator should copy those folders somewhere only administrators can
change and set `FullscreenController.HelperDirectory`, as the app does. Exit the
DisplayWeave app first, so two controllers never handle the same windows.
`DisplayMonitor` lists monitors by the device names that layouts and rules use.
.NET also has a `System.ApplicationIdentity`, so a host that imports both namespaces and
names the type adds `using ApplicationIdentity = DisplayWeave.ApplicationIdentity;`.
