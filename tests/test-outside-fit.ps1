#requires -Version 5.1
# Live: every app rule, and automatic handling, against a stand-in app window (tests\fixtures\TargetWindow.cs, built
# here into test-results), each on a fresh stand-in and data folder. The engine sees only that window; the stand-in
# covers region 1 of the layout's display or its whole monitor for a few seconds at a time and takes focus when it
# maximizes. Borderless in region is also checked while the window is not responding and after a killed controller.
# Force fullscreen loads the native component into the stand-in only; it needs the native helpers (-NativeDirectory).
[CmdletBinding()]
param([string]$BuildDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [string]$NativeDirectory = $BuildDirectory,
    # Internal: the controller run in a second process, which the test kills while the window is borderless.
    [string]$ControllerData, [long]$TargetHandle)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -ne 'Desktop') { throw 'Run this in Windows PowerShell 5.1: the engine is a .NET Framework library.' }

if (-not ('OutsideFitFixture' -as [type])) {
Add-Type -ReferencedAssemblies System.Drawing @'
using System;
using System.Drawing;
using System.Runtime.InteropServices;
public static class OutsideFitFixture {
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect rect, int size);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr window);
    [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr window);
    [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
    public static Rectangle Bounds(IntPtr window) {
        Rect r; return GetWindowRect(window, out r) ? Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom) : Rectangle.Empty;
    }
    // What is drawn, without the invisible resize border: a maximized window's frame is compared with this.
    public static Rectangle Frame(IntPtr window) {
        Rect r; if (DwmGetWindowAttribute(window, 9, out r, 16) != 0) GetWindowRect(window, out r); // DWMWA_EXTENDED_FRAME_BOUNDS
        return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
    }
    // WS_CAPTION: the title bar Borderless in region removes.
    public static bool HasCaption(IntPtr window) { return (GetWindowLongPtr(window, -16).ToInt64() & 0x00C00000L) == 0x00C00000L; }
    // Per-monitor, as DisplayWeave itself, so this thread and the engine measure the same pixels.
    public static void PerMonitorDpi() { SetThreadDpiAwarenessContext(new IntPtr(-4)); }
}
'@
}

function Import-Engine([string]$folder) {
    Add-Type -AssemblyName System.Drawing
    Add-Type -Path (Join-Path $folder 'DisplayWeave.Core.dll')
    [OutsideFitFixture]::PerMonitorDpi()
}

function New-Service([string]$data, [switch]$Automatic) {
    $service = [DisplayWeave.RegionService]::new($data, $null)
    $service.Layout.AutomaticRegions = [bool]$Automatic
    $service
}

function Get-Monitor($service) {
    $monitor = [DisplayWeave.DisplayMonitor]::Find($service.Layout.MonitorDevice)
    if (!$monitor) { throw "The layout's display $($service.Layout.MonitorDevice) was not found." }
    $monitor
}

function Get-Area($service) {
    $monitor = Get-Monitor $service
    if ($service.Layout.UseWorkArea) { $monitor.WorkingArea } else { $monitor.Bounds }
}

function Get-Region($service) { $service.Layout.Zones[0].ToPixels((Get-Area $service)) }

function Get-Identity([IntPtr]$window) {
    $identity = $null; $problem = $null
    if (![DisplayWeave.FullscreenController]::TryInspect($window, [ref]$identity, [ref]$problem)) { throw "The stand-in window cannot be inspected: $problem" }
    $identity
}

# A rule of the given action for the stand-in app, pinned to region 1.
function New-Rule($service, [IntPtr]$window, [string]$Action) {
    $identity = Get-Identity $window
    $rule = New-Object DisplayWeave.ApplicationRule
    $rule.ExecutablePath = $identity.ExecutablePath; $rule.WindowClass = $identity.WindowClass; $rule.DisplayName = 'Test target'
    $rule.Action = [DisplayWeave.ApplicationAction]$Action
    $rule.MonitorDevice = $service.Layout.MonitorDevice; $rule.ZoneId = $service.Layout.Zones[0].Id
    $rule
}

# One tick over the stand-in window only: with a rule of the given action for region 1, the service's own saved rules
# (-Saved), or no rule.
function Invoke-Tick($service, [IntPtr]$window, [string]$Action, [switch]$Saved) {
    $identity = Get-Identity $window
    $rules = New-Object DisplayWeave.ApplicationRules
    if ($Saved) { $rules = $service.Rules }
    elseif ($Action) { $rules.Put((New-Rule $service $window $Action)) }
    $candidate = New-Object DisplayWeave.WindowCandidate
    $candidate.Handle = $window; $candidate.Title = 'Test target'; $candidate.Identity = $identity
    $service.Engine.Tick([DisplayWeave.WindowCandidate[]]@($candidate), $rules, $service.Layout)
}

function Wait-Until([scriptblock]$condition, [scriptblock]$failure, [int]$milliseconds = 5000) {
    $deadline = [DateTime]::UtcNow.AddMilliseconds($milliseconds)
    while (!(& $condition)) {
        if ([DateTime]::UtcNow -gt $deadline) { throw (& $failure) }
        Start-Sleep -Milliseconds 50
    }
}

function Start-Piped([string]$path, [string]$arguments) {
    $start = New-Object System.Diagnostics.ProcessStartInfo $path, $arguments
    $start.UseShellExecute = $false; $start.RedirectStandardInput = $true; $start.RedirectStandardOutput = $true; $start.CreateNoWindow = $true
    [System.Diagnostics.Process]::Start($start)
}

function Read-Reply($process, [string]$expected, [int]$milliseconds = 15000) {
    $line = $process.StandardOutput.ReadLineAsync()
    if (!$line.Wait($milliseconds)) { throw "No '$expected' reply within $milliseconds ms." }
    if ($line.Result -notlike "$expected*") { throw "Expected '$expected', got '$($line.Result)'." }
    $line.Result
}

function Send-Command($target, [string]$command, [string]$reply) {
    $target.Process.StandardInput.WriteLine($command)
    Read-Reply $target.Process $reply
}

# The monitor the stand-in app itself sees.
function Get-SeenScreen($target) {
    $parts = (Send-Command $target 'screen' 'screen').Split(' ')
    New-Object System.Drawing.Rectangle ([int]$parts[1]), ([int]$parts[2]), ([int]$parts[3]), ([int]$parts[4])
}

function Get-JournalCount([string]$data) {
    $journal = Join-Path $data 'borderless.journal'
    if (!(Test-Path -LiteralPath $journal)) { return 0 }
    @(Get-Content -LiteralPath $journal | Where-Object { $_ }).Count
}

function Wait-Borderless([IntPtr]$window, $region) {
    Wait-Until { !([OutsideFitFixture]::HasCaption($window)) -and [OutsideFitFixture]::Bounds($window) -eq $region } `
        { "The window was not shown without its title bar in $region; it is at $([OutsideFitFixture]::Bounds($window))." }
}

function Wait-Restored([IntPtr]$window, $original, [string]$when) {
    Wait-Until { [OutsideFitFixture]::HasCaption($window) -and [OutsideFitFixture]::Bounds($window) -eq $original } `
        { "$when, the title bar or place did not come back: $([OutsideFitFixture]::Bounds($window)) instead of $original." }
}

function Wait-Frame([IntPtr]$window, $expected, [string]$what) {
    Wait-Until { [OutsideFitFixture]::Frame($window) -eq $expected } { "$what`: the window shows at $([OutsideFitFixture]::Frame($window)), not $expected." }
}

if ($ControllerData) {
    Import-Engine $BuildDirectory
    [DisplayWeave.ActivityLog]::Open($ControllerData)
    $service = New-Service $ControllerData
    Invoke-Tick $service ([IntPtr]$TargetHandle) -Action BorderlessInRegion
    Wait-Borderless ([IntPtr]$TargetHandle) (Get-Region $service)
    [Console]::Out.WriteLine('fitted'); [Console]::Out.Flush()
    Start-Sleep -Seconds 60 # killed by the test before this ends
    exit 1
}

# A running DisplayWeave handles the stand-in window too (automatic handling fits it when it maximizes), which makes
# the results its doing rather than the engine's under test. It is the user's app: ask them to exit it, never stop it here.
$running = @(Get-Process -Name DisplayWeave -ErrorAction SilentlyContinue)
if ($running) { throw "DisplayWeave is running ($($running[0].Path)). Exit it from its tray icon first, then run this test again." }

$root = Split-Path -Parent $PSScriptRoot
$run = Join-Path $root ('test-results\outside-fit-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $run -Force | Out-Null
# A copy, so this session does not lock the build folder's engine.
Copy-Item -LiteralPath (Join-Path (Resolve-Path -LiteralPath $BuildDirectory).Path 'DisplayWeave.Core.dll') -Destination $run
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$fixture = Join-Path $run 'DisplayWeaveTestTarget.exe'
& $compiler /nologo /target:winexe /optimize+ "/out:$fixture" (Join-Path $PSScriptRoot 'fixtures\TargetWindow.cs')
if ($LASTEXITCODE -ne 0) { throw 'The stand-in window did not compile.' }
Import-Engine $run
[DisplayWeave.FullscreenController]::HelperDirectory = (Resolve-Path -LiteralPath $NativeDirectory).Path

$script:failures = 0
$script:scenario = 0
# Runs one scenario on a fresh stand-in window and data folder, and reports it; a failure does not stop the others.
function Test-Scenario([string]$name, [scriptblock]$body) {
    $script:scenario++
    $data = Join-Path $run ("{0:D2}-data" -f $script:scenario)
    New-Item -ItemType Directory -Path $data -Force | Out-Null
    [DisplayWeave.ActivityLog]::Open($data)
    $process = Start-Piped $fixture ''
    $service = $null
    try {
        $window = [IntPtr][long]((Read-Reply $process 'ready').Substring(6))
        Wait-Until { [OutsideFitFixture]::Bounds($window) -ne [System.Drawing.Rectangle]::Empty } { 'The stand-in window has no bounds.' }
        $target = [pscustomobject]@{ Process = $process; Window = $window; Original = [OutsideFitFixture]::Bounds($window); Data = $data }
        & $body $target ([ref]$service)
        Write-Output "PASS $name"
    }
    catch {
        $script:failures++
        Write-Output "FAIL $name`: $($_.Exception.Message)"
        $log = Join-Path $data 'logs\activity.log'
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log | ForEach-Object { "    $_" } }
    }
    finally {
        if ($service) { $service.Dispose() }
        if (!$process.HasExited) {
            try { $process.StandardInput.WriteLine('exit') } catch [System.IO.IOException] { }
            if (!$process.WaitForExit(5000)) { $process.Kill() }
        }
    }
}

Test-Scenario 'Borderless in region: shown borderless and journaled; a pause restores it and empties the journal' {
    param($target, $service)
    $service.Value = New-Service $target.Data
    Invoke-Tick $service.Value $target.Window -Action BorderlessInRegion
    Wait-Borderless $target.Window (Get-Region $service.Value)
    if ((Get-JournalCount $target.Data) -ne 1) { throw 'The borderless window was not journaled.' }
    $service.Value.Engine.Pause()
    Wait-Restored $target.Window $target.Original 'After a pause'
    if ((Get-JournalCount $target.Data) -ne 0) { throw 'The journal kept a restored window.' }
}

Test-Scenario 'Borderless in region: a window not responding at the pause is restored once it answers' {
    param($target, $service)
    $service.Value = New-Service $target.Data
    Invoke-Tick $service.Value $target.Window -Action BorderlessInRegion
    Wait-Borderless $target.Window (Get-Region $service.Value)
    $target.Process.StandardInput.WriteLine('hang 9000')
    Read-Reply $target.Process 'hanging' | Out-Null
    Wait-Until { [OutsideFitFixture]::IsHungAppWindow($target.Window) } { 'Windows never counted the stand-in window as not responding.' } 8000
    $service.Value.Engine.Pause()
    if ([OutsideFitFixture]::HasCaption($target.Window) -or (Get-JournalCount $target.Data) -ne 1) { throw 'A window that was not responding was dropped instead of kept for later.' }
    Read-Reply $target.Process 'awake' 12000 | Out-Null
    Wait-Until { ![OutsideFitFixture]::IsHungAppWindow($target.Window) } { 'The stand-in window kept counting as not responding.' }
    $service.Value.Engine.Resume()
    Invoke-Tick $service.Value $target.Window
    Wait-Restored $target.Window $target.Original 'Once it answered again'
    if ((Get-JournalCount $target.Data) -ne 0) { throw 'The journal kept the window after its title bar came back.' }
}

Test-Scenario 'Borderless in region: the next start restores a window a killed controller left borderless' {
    param($target, $service)
    $powershell = (Get-Process -Id $PID).Path
    $controller = Start-Piped $powershell ('-NoProfile -File "{0}" -BuildDirectory "{1}" -ControllerData "{2}" -TargetHandle {3}' -f $PSCommandPath, $run, $target.Data, $target.Window.ToInt64())
    try { Read-Reply $controller 'fitted' 60000 | Out-Null }
    finally { if (!$controller.HasExited) { $controller.Kill() }; $controller.WaitForExit() }
    if ([OutsideFitFixture]::HasCaption($target.Window) -or (Get-JournalCount $target.Data) -ne 1) { throw 'The killed controller did not leave a borderless, journaled window.' }
    $service.Value = New-Service $target.Data
    Invoke-Tick $service.Value $target.Window
    Wait-Restored $target.Window $target.Original 'After the controller was killed and started again'
    if ((Get-JournalCount $target.Data) -ne 0) { throw 'The journal kept the window after the next start restored it.' }
}

Test-Scenario 'Maximize in region: a maximized window is fitted, stays fitted, and fills its monitor again on pause' {
    param($target, $service)
    $service.Value = New-Service $target.Data
    $region = Get-Region $service.Value
    Send-Command $target 'maximize' 'maximized' | Out-Null
    Wait-Frame $target.Window (Get-Monitor $service.Value).WorkingArea 'Maximized by itself'
    Invoke-Tick $service.Value $target.Window -Action MaximizeInRegion
    Wait-Frame $target.Window $region 'After the fit'
    Start-Sleep -Milliseconds 2500 # past the retry delay: a second tick must leave a fitted window alone
    Invoke-Tick $service.Value $target.Window -Action MaximizeInRegion
    Wait-Frame $target.Window $region 'After another tick'
    if (![OutsideFitFixture]::IsZoomed($target.Window)) { throw 'The fit took the window out of its maximized state.' }
    $service.Value.Engine.Pause()
    Wait-Frame $target.Window (Get-Monitor $service.Value).WorkingArea 'After a pause'
}

Test-Scenario 'Maximize in region: a borderless fullscreen window is fitted and covers its whole monitor again on pause' {
    param($target, $service)
    $service.Value = New-Service $target.Data
    $region = Get-Region $service.Value
    $monitor = (Get-Monitor $service.Value).Bounds
    Send-Command $target 'fullscreen' 'fullscreen' | Out-Null
    Wait-Until { [OutsideFitFixture]::Bounds($target.Window) -eq $monitor } { "The stand-in did not cover its monitor: $([OutsideFitFixture]::Bounds($target.Window))." }
    Invoke-Tick $service.Value $target.Window -Action MaximizeInRegion
    Wait-Until { [OutsideFitFixture]::Bounds($target.Window) -eq $region } { "The borderless fullscreen window was not fitted: $([OutsideFitFixture]::Bounds($target.Window)) instead of $region." }
    $service.Value.Engine.Pause()
    Wait-Until { [OutsideFitFixture]::Bounds($target.Window) -eq $monitor } { "After a pause it did not cover its monitor again: $([OutsideFitFixture]::Bounds($target.Window))." }
}

Test-Scenario 'Automatic handling (no rule): a maximized window is fitted into the region it is in' {
    param($target, $service)
    $service.Value = New-Service $target.Data -Automatic
    $area = Get-Area $service.Value
    $zones = @($service.Value.Layout.Zones | ForEach-Object { $_.ToPixels($area) })
    Send-Command $target 'maximize' 'maximized' | Out-Null
    Wait-Frame $target.Window (Get-Monitor $service.Value).WorkingArea 'Maximized by itself'
    Invoke-Tick $service.Value $target.Window
    Wait-Until { $zones -contains [OutsideFitFixture]::Frame($target.Window) } { "The maximized window was not fitted into a region: $([OutsideFitFixture]::Frame($target.Window))." }
    $service.Value.Engine.Pause()
    Wait-Frame $target.Window (Get-Monitor $service.Value).WorkingArea 'After a pause'
}

Test-Scenario 'Ignore: a maximized window is left filling its monitor even with automatic handling on' {
    param($target, $service)
    $service.Value = New-Service $target.Data -Automatic
    $full = (Get-Monitor $service.Value).WorkingArea
    Send-Command $target 'maximize' 'maximized' | Out-Null
    Wait-Frame $target.Window $full 'Maximized by itself'
    Invoke-Tick $service.Value $target.Window -Action Ignore
    Start-Sleep -Milliseconds 500 # moves are asynchronous; give one the time to show
    if ([OutsideFitFixture]::Frame($target.Window) -ne $full) { throw "An ignored window was moved to $([OutsideFitFixture]::Frame($target.Window))." }
}

Test-Scenario 'Removing a rule: the app is handled like any app without a rule at once, with no pause and resume' {
    param($target, $service)
    $service.Value = New-Service $target.Data -Automatic
    $full = (Get-Monitor $service.Value).WorkingArea
    $area = Get-Area $service.Value
    $zones = @($service.Value.Layout.Zones | ForEach-Object { $_.ToPixels($area) })
    $rule = New-Rule $service.Value $target.Window Ignore
    $service.Value.Rules.Put($rule)
    Send-Command $target 'maximize' 'maximized' | Out-Null
    Wait-Frame $target.Window $full 'Maximized by itself'
    Invoke-Tick $service.Value $target.Window -Saved
    Start-Sleep -Milliseconds 500
    if ([OutsideFitFixture]::Frame($target.Window) -ne $full) { throw 'The Ignore rule did not leave the window alone.' }
    $service.Value.RemoveRule($rule) | Out-Null
    Invoke-Tick $service.Value $target.Window -Saved
    Wait-Until { $zones -contains [OutsideFitFixture]::Frame($target.Window) } { "After its rule was removed, automatic handling left the window at $([OutsideFitFixture]::Frame($target.Window))." }
}

Test-Scenario 'Centered fullscreen: the window is left as it is, windowed and maximized' {
    param($target, $service)
    $service.Value = New-Service $target.Data -Automatic
    Invoke-Tick $service.Value $target.Window -Action CenteredFullscreen
    Start-Sleep -Milliseconds 500
    if ([OutsideFitFixture]::Bounds($target.Window) -ne $target.Original -or ![OutsideFitFixture]::HasCaption($target.Window)) { throw 'A windowed app under a Centered rule was changed.' }
    $full = (Get-Monitor $service.Value).WorkingArea
    Send-Command $target 'maximize' 'maximized' | Out-Null
    Wait-Frame $target.Window $full 'Maximized by itself'
    Invoke-Tick $service.Value $target.Window -Action CenteredFullscreen
    Start-Sleep -Milliseconds 500
    if ([OutsideFitFixture]::Frame($target.Window) -ne $full) { throw "A maximized app under a Centered rule was moved to $([OutsideFitFixture]::Frame($target.Window))." }
}

# Force fullscreen loads the component until handling is told otherwise; Tick until the helper reports it working.
function Start-Forced($service, $target) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    do {
        Invoke-Tick $service $target.Window -Action ForceFullscreen
        $app = $service.Engine.HandledApps() | Where-Object { $_.Target.ProcessId -eq $target.Process.Id } | Select-Object -First 1
        if ($app -and $app.State -eq [DisplayWeave.HandledAppState]::Working) { return }
        if ($app -and $app.State -eq [DisplayWeave.HandledAppState]::Error) { throw "The component did not attach: $($app.Error)" }
        Start-Sleep -Milliseconds 250
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "The component was not working within 20 s: $($service.Engine.LastMessage)"
}

Test-Scenario 'Force fullscreen: the component confines the app to its region, and a pause gives back its monitor, frame and place' {
    param($target, $service)
    $service.Value = New-Service $target.Data
    $region = Get-Region $service.Value
    $monitor = (Get-Monitor $service.Value).Bounds
    Start-Forced $service.Value $target
    $seen = Get-SeenScreen $target
    if ($seen -ne $region) { throw "With the component loaded, the app sees its monitor as $seen, not its region $region." }
    Wait-Until { [OutsideFitFixture]::Bounds($target.Window) -eq $region } { "The forced window is at $([OutsideFitFixture]::Bounds($target.Window)), not its region $region." }
    $service.Value.Engine.Pause()
    Wait-Until { (Get-SeenScreen $target) -eq $monitor } { "After a pause the app still sees $(Get-SeenScreen $target), not its monitor $monitor." }
    Wait-Restored $target.Window $target.Original 'After a pause'
}

# Known issue (open, 2026-10-14): the hook restores the placement it saved at attach, and an app that maximized itself
# meanwhile is left un-maximized at its region's size. Kept failing on purpose until the hook is fixed.
Test-Scenario 'Known issue, Force fullscreen: an app that maximized itself while confined gets its own state back on pause' {
    param($target, $service)
    $service.Value = New-Service $target.Data
    $region = Get-Region $service.Value
    Start-Forced $service.Value $target
    Send-Command $target 'maximize' 'maximized' | Out-Null
    Wait-Frame $target.Window $region 'Maximized with the component loaded'
    $service.Value.Engine.Pause()
    $full = (Get-Monitor $service.Value).WorkingArea
    Wait-Until { ([OutsideFitFixture]::IsZoomed($target.Window) -and [OutsideFitFixture]::Frame($target.Window) -eq $full) -or
        (![OutsideFitFixture]::IsZoomed($target.Window) -and [OutsideFitFixture]::Bounds($target.Window) -eq $target.Original) } `
        { "After a pause the window is left at $([OutsideFitFixture]::Bounds($target.Window)) (maximized: $([OutsideFitFixture]::IsZoomed($target.Window))), neither maximized over its monitor nor back at $($target.Original)." }
}

if ($script:failures) { Write-Output "$script:failures of $script:scenario scenarios failed. Logs: $run"; exit 1 }
Write-Output "All $script:scenario scenarios passed. Logs: $run"
