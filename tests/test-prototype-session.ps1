#requires -Version 5.1
# Live end-to-end check of one prototype session against an OWNED windowed
# ProjectionProbe standing in for the user's app. Creates a temporary display
# (Windows asks for administrator approval), moves only the owned probe, and
# verifies release, restoration and display removal. With -Interactive it
# sends one tagged synthetic left click to the owned presenter's projected
# image to enter; that is not hardware-input evidence.
[CmdletBinding()]
param(
    [string]$BuildDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\prototype'),
    [string]$ProbeDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist\projection-x64'),
    [string]$DriverPackageDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '.tools\research\vdd-signed-24.12.24\package'),
    [ValidateSet(30, 60, 90)][int]$Seconds = 30,
    [switch]$Interactive,
    # Writes stop.request this many seconds after the session is running (0 = run to the deadline).
    [ValidateRange(0, 80)][int]$StopAfterSeconds = 0,
    [int[]]$Region = @(3000, 300, 1280, 720)
)
$ErrorActionPreference = 'Stop'
$BuildDirectory = (Resolve-Path -LiteralPath $BuildDirectory).Path
$exe = Join-Path $BuildDirectory 'DisplayWeavePrototype.exe'
$probeExe = Join-Path $ProbeDirectory 'ProjectionProbe.exe'
foreach ($file in @($exe, $probeExe)) { if (-not (Test-Path -LiteralPath $file)) { throw "Missing $file" } }
if (Get-Process -Name DisplayWeave -ErrorAction SilentlyContinue) { throw 'Exit the legacy DisplayWeave app first.' }
$driver = Join-Path $BuildDirectory 'driver'
if (-not (Test-Path -LiteralPath (Join-Path $driver 'MttVDD.inf'))) {
    New-Item -ItemType Directory -Path $driver -Force | Out-Null
    foreach ($name in @('MttVDD.inf', 'MttVDD.dll', 'mttvdd.cat')) { Copy-Item -LiteralPath (Join-Path $DriverPackageDirectory $name) -Destination $driver }
}
if (-not ('PrototypeSessionTest' -as [type])) {
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class PrototypeSessionTest {
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll")] static extern uint SendInput(uint count, Input[] inputs, int size);
    [StructLayout(LayoutKind.Sequential)] public struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] struct MouseInput { public int dx, dy; public uint data, flags, time; public UIntPtr extra; }
    // x64 INPUT is 40 bytes: type, alignment padding, then the 32-byte MOUSEINPUT.
    [StructLayout(LayoutKind.Sequential)] struct Input { public uint type; public MouseInput mouse; }
    static void Aware() { SetThreadDpiAwarenessContext(new IntPtr(-4)); }
    public static int[] WindowRect(long hwnd) { Aware(); Rect r; if (!GetWindowRect(new IntPtr(hwnd), out r)) return null; return new[] { r.Left, r.Top, r.Right, r.Bottom }; }
    public static int[] Cursor() { Aware(); Point p; GetCursorPos(out p); return new[] { p.X, p.Y }; }
    // Owned-fixture entry gesture: one cursor placement inside the owned
    // presenter image, then a tagged left click. Never used by the product.
    public static void ClickAt(int x, int y) {
        Aware(); if (!SetCursorPos(x, y)) throw new Exception("SetCursorPos failed.");
        var inputs = new Input[2];
        inputs[0].type = 0; inputs[0].mouse.flags = 0x0002; inputs[0].mouse.extra = new UIntPtr(0x52535054);
        inputs[1] = inputs[0]; inputs[1].mouse.flags = 0x0004;
        if (SendInput(2, inputs, Marshal.SizeOf(typeof(Input))) != 2) throw new Exception("Tagged click was not accepted.");
    }
    public static void Restore(int x, int y) { Aware(); SetCursorPos(x, y); }
}
'@
}
$folder = Join-Path (Split-Path -Parent $PSScriptRoot) ('test-results\prototype-session-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $folder | Out-Null
$outcome = [ordered]@{ success = $false; failure = $null; interactive = [bool]$Interactive; seconds = $Seconds; stopAfterSeconds = $StopAfterSeconds
    session = $null; finalState = $null; cleanupComplete = $false; windowRestored = $false; probeAlive = $false; rectBefore = $null; rectAfter = $null
    syntheticEntryClick = $false; inputEntered = $false; presenterClaimedRelease = $false; presenterReleaseAcknowledged = $false
    hardwareInputVerified = $false; problems = @() }
$probe = $null; $worker = $null; $cursorBefore = [PrototypeSessionTest]::Cursor()
try {
    $probeReport = Join-Path $folder 'probe.json'
    $probe = Start-Process -FilePath $probeExe -ArgumentList @('--rect', '200,200,960,540', '--seconds', '300', '--report', ('"{0}"' -f $probeReport)) -PassThru
    $until = (Get-Date).AddSeconds(15)
    while (-not (Test-Path -LiteralPath ($probeReport + '.ready.json'))) { if ($probe.HasExited -or (Get-Date) -gt $until) { throw 'Owned probe did not start.' }; Start-Sleep -Milliseconds 200 }
    $ready = Get-Content -LiteralPath ($probeReport + '.ready.json') -Raw | ConvertFrom-Json
    $hwnd = [int64]$ready.hwndValue
    $outcome.rectBefore = [PrototypeSessionTest]::WindowRect($hwnd)
    $id = [Guid]::NewGuid().ToString('N')
    $session = Join-Path $env:LOCALAPPDATA "DisplayWeavePrototype\sessions\$id"
    New-Item -ItemType Directory -Path $session | Out-Null
    $outcome.session = $session
    $request = [ordered]@{ SchemaVersion = 1; SessionId = $id; Mode = $(if ($Interactive) { 'interactive' } else { 'visual' }); DurationSeconds = $Seconds; Borderless = $true
        SourceHwnd = [string]$hwnd; SourcePid = $probe.Id; SourceStartedUtcFileTime = [string]$probe.StartTime.ToUniversalTime().ToFileTimeUtc()
        SourceExecutable = (Get-Process -Id $probe.Id).Path; ReturnHwnd = $null; Region = $Region }
    $request | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $session 'request.json') -Encoding UTF8
    $worker = Start-Process -FilePath $exe -ArgumentList @('--worker', ('"{0}"' -f $session)) -PassThru
    $state = $null; $runningSince = $null; $clicked = $false; $stopped = $false
    $deadline = (Get-Date).AddSeconds(420)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 250
        $statusPath = Join-Path $session 'status.json'
        if (-not (Test-Path -LiteralPath $statusPath)) { if ($worker.HasExited) { throw "Worker exited ($($worker.ExitCode)) without status." }; continue }
        try { $status = Get-Content -LiteralPath $statusPath -Raw | ConvertFrom-Json } catch { continue }
        if ($status.state -ne $state) { $state = $status.state; Write-Output ("{0:HH:mm:ss} {1}: {2}" -f (Get-Date), $state, $status.message) }
        if ($status.terminal) { break }
        if ($state -eq 'running' -and -not $runningSince) { $runningSince = Get-Date }
        if ($Interactive -and $runningSince -and -not $clicked -and (Get-Date) -gt $runningSince.AddSeconds(3)) {
            # Region is the destination; the 1280x720 source fits exactly when the region is 16:9.
            [PrototypeSessionTest]::ClickAt($Region[0] + [int]($Region[2] / 2), $Region[1] + [int]($Region[3] / 2))
            $clicked = $true; $outcome.syntheticEntryClick = $true
        }
        if ($StopAfterSeconds -and $runningSince -and -not $stopped -and (Get-Date) -gt $runningSince.AddSeconds($StopAfterSeconds)) {
            Set-Content -LiteralPath (Join-Path $session 'stop.request') -Value (Get-Date -Format o); $stopped = $true
        }
    }
    if (-not $status.terminal) { throw 'Session did not reach a terminal state in time.' }
    $outcome.finalState = $status.state; $outcome.cleanupComplete = [bool]$status.cleanupComplete; $outcome.windowRestored = [bool]$status.windowRestored
    $outcome.problems = @($status.problems)
    $presenterPath = Join-Path $session 'presenter.json'
    if (Test-Path -LiteralPath $presenterPath) {
        $presenter = Get-Content -LiteralPath $presenterPath -Raw | ConvertFrom-Json
        $outcome.inputEntered = [bool]$presenter.input.entered
        $outcome.presenterClaimedRelease = [bool]$presenter.input.leaseReleaseStarted
        $outcome.presenterReleaseAcknowledged = [bool]$presenter.input.leaseReleaseAcknowledged
    }
    $outcome.probeAlive = -not $probe.HasExited
    $outcome.rectAfter = [PrototypeSessionTest]::WindowRect($hwnd)
    $rectSame = $outcome.rectBefore -and $outcome.rectAfter -and (($outcome.rectBefore -join ',') -eq ($outcome.rectAfter -join ','))
    $outcome.success = $status.state -eq 'completed' -and $outcome.cleanupComplete -and $outcome.windowRestored -and $outcome.probeAlive -and $rectSame -and
        (-not $Interactive -or ($outcome.inputEntered -and $outcome.presenterClaimedRelease -and $outcome.presenterReleaseAcknowledged))
    if (-not $outcome.success -and -not $outcome.failure) { $outcome.failure = 'Acceptance checks failed; see result.json and the session folder.' }
} catch { $outcome.failure = $_.Exception.Message }
finally {
    # On a harness failure, let the session release and restore before the probe closes.
    if ($worker -and -not $worker.HasExited -and $outcome.session) {
        Set-Content -LiteralPath (Join-Path $outcome.session 'stop.request') -Value (Get-Date -Format o)
        $worker.WaitForExit(300000) | Out-Null
    }
    if ($probe -and -not $probe.HasExited) { $probe.CloseMainWindow() | Out-Null; if (-not $probe.WaitForExit(3000)) { $probe.Kill() } }
    if ($Interactive) { [PrototypeSessionTest]::Restore($cursorBefore[0], $cursorBefore[1]) }
    $outcome | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $folder 'result.json')
}
$outcome | ConvertTo-Json -Depth 5
if (-not $outcome.success) { throw "Prototype session test failed: $($outcome.failure)" }
Write-Output "PASS prototype session. Reports: $folder and $($outcome.session)"
