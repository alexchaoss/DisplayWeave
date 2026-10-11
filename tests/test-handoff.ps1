#requires -Version 5.1
# Interactive integration test. Controls only FullscreenProbe processes launched
# by this script; never attaches a helper to an existing application.
[CmdletBinding()]
param(
    [ValidateSet('x64', 'x86', 'all')][string]$Architecture = 'all',
    [string]$OldBuildDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'release\build-0.4.0'),
    [string]$NewBuildDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'release\build-0.4.1'),
    [string]$OutputDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'test-results')
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
if (-not [Environment]::Is64BitProcess) { throw 'Run this test from 64-bit PowerShell so both target architectures can be inspected.' }

if (-not ('DisplayWeaveHandoffNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

public static class DisplayWeaveHandoffNative
{
    [StructLayout(LayoutKind.Sequential)] public struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public Rect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hwnd, StringBuilder name, int length);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint message, IntPtr wparam, IntPtr lparam, uint flags, uint timeout, out IntPtr result);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr OpenFileMapping(uint access, bool inherit, string name);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateFileMapping(IntPtr file, IntPtr security, uint protection, uint sizeHigh, uint sizeLow, string name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr size);
    [DllImport("kernel32.dll")] private static extern bool UnmapViewOfFile(IntPtr view);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GetProcessTimes(IntPtr process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref uint length);

    public static long CreationTime(Process process)
    {
        long created, exited, kernel, user;
        if (!GetProcessTimes(process.Handle, out created, out exited, out kernel, out user)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return created;
    }

    public static string ImagePath(Process process)
    {
        StringBuilder path = new StringBuilder(32768); uint length = (uint)path.Capacity;
        if (!QueryFullProcessImageName(process.Handle, 0, path, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        return path.ToString();
    }

    public static Rect PrimaryBounds()
    {
        IntPtr old = SetThreadDpiAwarenessContext(new IntPtr(-4));
        try {
            MonitorInfo info = new MonitorInfo(); info.Size = Marshal.SizeOf(typeof(MonitorInfo));
            if (!GetMonitorInfo(MonitorFromPoint(new Point(), 1), ref info)) throw new Win32Exception();
            return info.Monitor;
        } finally { if (old != IntPtr.Zero) SetThreadDpiAwarenessContext(old); }
    }

    public static void Send(int pid, long hwndValue, uint message)
    {
        IntPtr hwnd = new IntPtr(hwndValue); uint owner;
        StringBuilder name = new StringBuilder(128);
        if (!IsWindow(hwnd) || GetWindowThreadProcessId(hwnd, out owner) == 0 || owner != (uint)pid ||
            GetClassName(hwnd, name, name.Capacity) == 0 || name.ToString() != "DisplayWeaveFullscreenProbe")
            throw new InvalidOperationException("The fixture window identity changed; no message was sent.");
        if (message < 0x8001 || message > 0x8004) throw new ArgumentException("Not a fixture command.");
        IntPtr result;
        if (SendMessageTimeout(hwnd, message, IntPtr.Zero, IntPtr.Zero, 2, 2000, out result) == IntPtr.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "The fixture did not handle its test command.");
    }

    public static byte[] ReadMapping(int pid)
    {
        IntPtr mapping = OpenFileMapping(4, false, "Local\\DisplayWeave_" + pid);
        if (mapping == IntPtr.Zero) {
            int error = Marshal.GetLastWin32Error();
            if (error == 2) return null;
            throw new Win32Exception(error);
        }
        IntPtr view = IntPtr.Zero;
        try {
            view = MapViewOfFile(mapping, 4, 0, 0, new UIntPtr(96));
            if (view == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            for (int attempt = 0; attempt < 100; attempt++) {
                int sequence = Marshal.ReadInt32(view, 12);
                if ((sequence & 1) != 0) { System.Threading.Thread.Sleep(1); continue; }
                byte[] bytes = new byte[96]; Marshal.Copy(view, bytes, 0, bytes.Length);
                System.Threading.Thread.MemoryBarrier();
                if (sequence == Marshal.ReadInt32(view, 12)) return bytes;
            }
            throw new InvalidOperationException("Mapping remained unstable.");
        } finally { if (view != IntPtr.Zero) UnmapViewOfFile(view); CloseHandle(mapping); }
    }

    public sealed class IncompatibleMapping : IDisposable
    {
        private IntPtr mapping, view;
        public IncompatibleMapping(int pid)
        {
            mapping = CreateFileMapping(new IntPtr(-1), IntPtr.Zero, 4, 0, 96, "Local\\DisplayWeave_" + pid);
            int error = Marshal.GetLastWin32Error();
            if (mapping == IntPtr.Zero) throw new Win32Exception(error);
            if (error == 183) { CloseHandle(mapping); mapping = IntPtr.Zero; throw new InvalidOperationException("An existing fixture mapping will not be modified."); }
            try {
                view = MapViewOfFile(mapping, 0xF001F, 0, 0, new UIntPtr(96));
                if (view == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
                byte[] bytes = new byte[96];
                Array.Copy(BitConverter.GetBytes(0x52534731u), 0, bytes, 0, 4);
                Array.Copy(BitConverter.GetBytes(1u), 0, bytes, 4, 4);
                Array.Copy(BitConverter.GetBytes(88u), 0, bytes, 8, 4);
                Array.Copy(BitConverter.GetBytes(pid), 0, bytes, 16, 4);
                bytes[68] = 0x5a; bytes[84] = 0x36; bytes[95] = 0x7b;
                Marshal.Copy(bytes, 0, view, bytes.Length);
            } catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (view != IntPtr.Zero) { UnmapViewOfFile(view); view = IntPtr.Zero; }
            if (mapping != IntPtr.Zero) { CloseHandle(mapping); mapping = IntPtr.Zero; }
        }
    }
}
'@
}

function Assert-Handoff([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Assert-Fixture([object]$Fixture) {
    if ($Fixture.Process.HasExited) { throw "The owned fixture exited (code $($Fixture.Process.ExitCode))." }
    $Fixture.Process.Refresh()
    try {
        $actualPath = [DisplayWeaveHandoffNative]::ImagePath($Fixture.Process)
        $actualCreated = [DisplayWeaveHandoffNative]::CreationTime($Fixture.Process)
    }
    catch {
        if ($Fixture.Process.HasExited) { throw "The owned fixture exited during identity inspection (code $($Fixture.Process.ExitCode))." }
        throw
    }
    if ($Fixture.Process.HasExited) { throw "The owned fixture exited after identity inspection (code $($Fixture.Process.ExitCode))." }
    if (-not [string]::Equals($actualPath, $Fixture.Path, [StringComparison]::OrdinalIgnoreCase) -or $actualCreated -ne $Fixture.StartTicks) {
        throw "Fixture process identity changed; no further target operations are permitted. Expected path '$($Fixture.Path)' and creation $($Fixture.StartTicks); observed '$actualPath' and creation $actualCreated."
    }
}

function Get-HookModules([object]$Fixture) {
    Assert-Fixture $Fixture
    return @($Fixture.Process.Modules | Where-Object { $_.ModuleName -ieq 'RegionHook.dll' } | ForEach-Object { $_.FileName })
}

function Get-Mapping([object]$Fixture) {
    Assert-Fixture $Fixture
    [byte[]]$bytes = [DisplayWeaveHandoffNative]::ReadMapping($Fixture.Process.Id)
    if ($null -eq $bytes) { return $null }
    return [pscustomobject]@{
        Bytes = $bytes; Magic = [BitConverter]::ToUInt32($bytes, 0); Version = [BitConverter]::ToUInt32($bytes, 4)
        Size = [BitConverter]::ToUInt32($bytes, 8); TargetPid = [BitConverter]::ToUInt32($bytes, 16)
        ControllerPid = [BitConverter]::ToUInt32($bytes, 20); TargetHwnd = [BitConverter]::ToUInt64($bytes, 24)
        Active = [BitConverter]::ToInt32($bytes, 56); HookReady = [BitConverter]::ToInt32($bytes, 64)
        AcknowledgedPid = [BitConverter]::ToUInt32($bytes, 72); Capabilities = [BitConverter]::ToUInt32($bytes, 76)
        DxgiTransitions = [BitConverter]::ToInt32($bytes, 80); ResizeIntercepts = [BitConverter]::ToInt32($bytes, 84)
        Mode = [BitConverter]::ToInt32($bytes, 88); WindowState = [BitConverter]::ToInt32($bytes, 92)
        Configuration = [Convert]::ToBase64String([byte[]]($bytes[0..59] + $bytes[88..91]))
        ExceptHeartbeat = [Convert]::ToBase64String([byte[]]($bytes[0..59] + $bytes[64..95]))
    }
}

function Get-ProbeSnapshot([object]$Fixture, [string]$SaveAs = '') {
    Assert-Fixture $Fixture
    [DisplayWeaveHandoffNative]::Send($Fixture.Process.Id, $Fixture.Hwnd, 0x8003)
    $text = [IO.File]::ReadAllText($Fixture.Report)
    $snapshot = $text | ConvertFrom-Json
    Assert-Handoff ($snapshot.pid -eq $Fixture.Process.Id -and $snapshot.hwnd -eq $Fixture.Hwnd) 'Snapshot identity does not match the owned fixture.'
    if ($SaveAs) { [IO.File]::WriteAllText($SaveAs, $text) }
    return $snapshot
}

function Start-Fixture([string]$Executable, [string]$Report, [int]$X, [int]$Y) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.Arguments = '--report "' + $Report + '" --initial-position ' + $X + ' ' + $Y
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true
    # This rendering fixture is intentionally visible; no user application is selected.
    $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
    $process = [Diagnostics.Process]::Start($info)
    $fixture = [pscustomobject]@{ Process = $process; Path = $Executable; StartTicks = [DisplayWeaveHandoffNative]::CreationTime($process); Hwnd = 0L; Report = $Report }
    $script:ownedFixtures.Add($fixture)
    $deadline = [DateTime]::UtcNow.AddSeconds(12)
    $ready = $null
    while ($null -eq $ready) {
        Assert-Fixture $fixture
        if ([IO.File]::Exists($Report + '.ready.json')) {
            try { $ready = [IO.File]::ReadAllText($Report + '.ready.json') | ConvertFrom-Json }
            catch { $ready = $null }
        }
        if ($null -ne $ready) { break }
        if ([DateTime]::UtcNow -gt $deadline) { throw 'The fixture did not initialize within 12 seconds.' }
        Start-Sleep -Milliseconds 50
    }
    Assert-Handoff ($ready.pid -eq $process.Id) 'The ready marker does not belong to the launched fixture.'
    $fixture.Hwnd = [long]$ready.hwnd
    Assert-Handoff (@(Get-HookModules $fixture).Count -eq 0) 'A controller already injected this fresh fixture; it will not be replaced.'
    Assert-Handoff ($null -eq (Get-Mapping $fixture)) 'A controller already owns this fresh fixture mapping; it will not be changed.'
    $initial = Get-ProbeSnapshot $fixture
    return $fixture
}

function Read-ControllerOutput([object]$Controller) {
    foreach ($channel in @('Output', 'Error')) {
        $property = $channel + 'Read'
        $remainingLines = 1024
        while ($null -ne $Controller.$property -and $Controller.$property.IsCompleted -and $remainingLines-- -gt 0) {
            $line = $Controller.$property.Result
            if ($null -eq $line) { $Controller.$property = $null; break }
            $Controller.Lines.Add($line)
            $reader = if ($channel -eq 'Output') { $Controller.Process.StandardOutput } else { $Controller.Process.StandardError }
            $Controller.$property = $reader.ReadLineAsync()
        }
    }
}

function Start-Controller([string]$Executable, [object]$Fixture, [object]$Region, [string]$LogPath) {
    Assert-Fixture $Fixture
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $Executable
    $info.Arguments = "$($Fixture.Process.Id) $($Fixture.Hwnd) $($Region.x) $($Region.y) $($Region.width) $($Region.height) --auto"
    $info.UseShellExecute = $false; $info.CreateNoWindow = $true; $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $info.RedirectStandardInput = $true; $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
    $process = [Diagnostics.Process]::Start($info)
    $controller = [pscustomobject]@{ Process = $process; Path = $Executable; LogPath = $LogPath; Lines = (New-Object 'System.Collections.Generic.List[string]'); OutputRead = $process.StandardOutput.ReadLineAsync(); ErrorRead = $process.StandardError.ReadLineAsync() }
    $script:ownedControllers.Add($controller)
    return $controller
}

function Wait-ControllerReady([object]$Controller, [object]$Fixture, [bool]$Reused) {
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    $expected = 'READY ' + $Fixture.Process.Id + $(if ($Reused) { ' REUSED' } else { '' })
    do {
        Assert-Fixture $Fixture
        Read-ControllerOutput $Controller
        if ($Controller.Lines.Contains($expected) -or $Controller.Lines.Contains($expected + ' CAPS 3')) {
            $mapping = Get-Mapping $Fixture
            Assert-Handoff ($mapping.Version -eq 2 -and $mapping.Size -eq 96 -and $mapping.ControllerPid -eq $Controller.Process.Id -and
                $mapping.TargetPid -eq $Fixture.Process.Id -and $mapping.TargetHwnd -eq $Fixture.Hwnd -and $mapping.Active -eq 1 -and
                $mapping.HookReady -eq 1 -and $mapping.AcknowledgedPid -eq $Fixture.Process.Id -and ($mapping.Capabilities -band 3) -eq 3 -and $mapping.Mode -eq 0) 'READY did not identify an active compatible AUTO mapping.'
            return
        }
        if ($Controller.Process.HasExited) { throw ('Controller exited before ' + $expected + ': ' + ($Controller.Lines -join ' | ')) }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw ('Controller did not report ' + $expected + ': ' + ($Controller.Lines -join ' | '))
}

function Finish-ControllerLog([object]$Controller) {
    $deadline = [DateTime]::UtcNow.AddSeconds(2)
    do {
        Read-ControllerOutput $Controller
        if ($null -eq $Controller.OutputRead -and $null -eq $Controller.ErrorRead) { break }
        Start-Sleep -Milliseconds 20
    } while ([DateTime]::UtcNow -lt $deadline)
    [IO.File]::WriteAllLines($Controller.LogPath, $Controller.Lines.ToArray())
}

function Stop-Controller([object]$Controller) {
    if (-not $Controller.Process.HasExited) {
        $Controller.Process.StandardInput.WriteLine('QUIT'); $Controller.Process.StandardInput.Flush()
        if (-not $Controller.Process.WaitForExit(4000)) { throw 'The owned controller did not exit after QUIT.' }
    }
    Finish-ControllerLog $Controller
    Assert-Handoff ($Controller.Process.ExitCode -eq 0) ('Controller failed: ' + ($Controller.Lines -join ' | '))
}

function Assert-RejectedController([object]$Controller, [int]$ExpectedError) {
    if (-not $Controller.Process.WaitForExit(5000)) { throw 'A controller that should have been refused is still running.' }
    Finish-ControllerLog $Controller
    $output = $Controller.Lines -join [Environment]::NewLine
    Assert-Handoff ($Controller.Process.ExitCode -ne 0 -and $output -match ('Windows error ' + $ExpectedError + '\)') -and $output -notmatch '(?m)^READY ') ('Expected controller refusal ' + $ExpectedError + ': ' + $output)
}

function Test-Rectangle([object]$Actual, [object]$Expected) {
    return [Math]::Abs($Actual.x - $Expected.x) -le 2 -and [Math]::Abs($Actual.y - $Expected.y) -le 2 -and
        [Math]::Abs($Actual.width - $Expected.width) -le 2 -and [Math]::Abs($Actual.height - $Expected.height) -le 2
}

function Assert-NormalRestored([object]$Fixture, [object]$Initial, [string]$SaveAs) {
    $deadline = [DateTime]::UtcNow.AddSeconds(5)
    do {
        $first = Get-ProbeSnapshot $Fixture
        $normal = $first.after
        $mapping = Get-Mapping $Fixture
        $stateNormal = $null -eq $mapping -or $mapping.WindowState -eq 0
        if ($stateNormal -and -not $normal.reported_fullscreen -and $normal.description_windowed -and -not $normal.maximized -and
            $normal.get_state_hr -eq 0 -and $normal.get_desc_hr -eq 0 -and (Test-Rectangle $normal.window $Initial.window) -and
            $normal.style -eq $Initial.style -and $normal.ex_style -eq $Initial.ex_style) {
            Start-Sleep -Milliseconds 200
            $second = Get-ProbeSnapshot $Fixture $SaveAs
            # In manual probe mode the stored fullscreen snapshot persists. Its
            # frame count advances only while the *current* DXGI state is full.
            if ($second.presented_frames -ge ($first.presented_frames + 3) -and $second.fullscreen_frames -eq $first.fullscreen_frames) {
                Assert-Handoff ($second.failed_presents -eq 0) 'Rendering failed while restoring normal placement.'
                return $second
            }
        }
        Start-Sleep -Milliseconds 50
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'The fixture did not restore its original normal bounds, frame, DXGI state, and rendering.'
}

function Test-FullscreenCycle([object]$Fixture, [object]$Region, [object]$Initial, [string]$Prefix) {
    $baseline = (Get-Mapping $Fixture).DxgiTransitions
    [DisplayWeaveHandoffNative]::Send($Fixture.Process.Id, $Fixture.Hwnd, 0x8001)
    $deadline = [DateTime]::UtcNow.AddSeconds(6)
    $verified = $false
    do {
        $snapshot = Get-ProbeSnapshot $Fixture
        $full = $snapshot.fullscreen
        $mapping = Get-Mapping $Fixture
        if ($snapshot.enter_hr -eq 0 -and $snapshot.hook_ready_at_entry -and $snapshot.hook_ready -and
            $full.get_state_hr -eq 0 -and $full.reported_fullscreen -and $full.get_desc_hr -eq 0 -and -not $full.description_windowed -and
            (Test-Rectangle $full.window $Region) -and (Test-Rectangle $full.reported_monitor $Region) -and
            $full.buffer_width -eq $Region.width -and $full.buffer_height -eq $Region.height -and
            $snapshot.fullscreen_frames -ge 3 -and $snapshot.failed_presents -eq 0 -and $mapping.DxgiTransitions -gt $baseline) {
            $verified = $true; break
        }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    Assert-Handoff $verified 'Fullscreen region, logical DXGI state, rendering, or interception counter did not match.'
    $snapshot = Get-ProbeSnapshot $Fixture ($Prefix + '-fullscreen.json')
    [DisplayWeaveHandoffNative]::Send($Fixture.Process.Id, $Fixture.Hwnd, 0x8002)
    $restored = Assert-NormalRestored $Fixture $Initial ($Prefix + '-normal.json')
    Assert-Handoff ($restored.leave_hr -eq 0) 'The probe could not leave logical fullscreen.'
    return [pscustomobject]@{ region = $Region; bufferWidth = $snapshot.fullscreen.buffer_width; bufferHeight = $snapshot.fullscreen.buffer_height; fullscreenFrames = $snapshot.fullscreen_frames; transitionsBefore = $baseline; transitionsAfter = $mapping.DxgiTransitions; normalRestored = $true }
}

$oldBuild = [IO.Path]::GetFullPath($OldBuildDirectory)
$newBuild = [IO.Path]::GetFullPath($NewBuildDirectory)
Assert-Handoff (-not [string]::Equals($oldBuild.TrimEnd('\'), $newBuild.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) 'Old and new builds must use distinct directories.'
$architectures = if ($Architecture -eq 'all') { @('x64', 'x86') } else { @($Architecture) }
foreach ($arch in $architectures) {
    foreach ($file in @((Join-Path $oldBuild "native-v2-$arch\FullscreenProbe.exe"), (Join-Path $oldBuild "native-v2-$arch\RegionHookHost.exe"),
        (Join-Path $oldBuild "native-v2-$arch\RegionHook.dll"), (Join-Path $newBuild "native-v2-$arch\RegionHookHost.exe"), (Join-Path $newBuild "native-v2-$arch\RegionHook.dll"))) {
        Assert-Handoff ([IO.File]::Exists($file)) "Required built fixture component is missing: $file"
    }
}
$bounds = [DisplayWeaveHandoffNative]::PrimaryBounds()
Assert-Handoff (($bounds.Right - $bounds.Left) -ge 1320 -and ($bounds.Bottom - $bounds.Top) -ge 810) 'The primary monitor must be at least 1320 x 810 physical pixels for these fixture regions.'
$regionA = [pscustomobject]@{ x = $bounds.Left + 240; y = $bounds.Top + 180; width = 800; height = 450 }
$regionB = [pscustomobject]@{ x = $bounds.Left + 340; y = $bounds.Top + 240; width = 960; height = 540 }
$regionDenied = [pscustomobject]@{ x = $bounds.Left + 600; y = $bounds.Top + 300; width = 640; height = 360 }
$testDirectory = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('handoff-' + (Get-Date -Format 'yyyyMMdd-HHmmss-fff') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[void][IO.Directory]::CreateDirectory($testDirectory)
$script:ownedFixtures = New-Object 'System.Collections.Generic.List[object]'
$script:ownedControllers = New-Object 'System.Collections.Generic.List[object]'
$results = New-Object 'System.Collections.Generic.List[object]'
$failed = $false

foreach ($arch in $architectures) {
    $synthetic = $null
    $result = [ordered]@{ architecture = $arch; success = $false; mode = 'AUTO'; oldBuild = $oldBuild; newBuild = $newBuild; message = 'running' }
    try {
        $probePath = Join-Path $oldBuild "native-v2-$arch\FullscreenProbe.exe"
        $oldHostPath = Join-Path $oldBuild "native-v2-$arch\RegionHookHost.exe"
        $newHostPath = Join-Path $newBuild "native-v2-$arch\RegionHookHost.exe"
        $oldDll = Join-Path $oldBuild "native-v2-$arch\RegionHook.dll"
        $fixture = Start-Fixture $probePath (Join-Path $testDirectory "$arch-probe-live.json") ($bounds.Left + 80) ($bounds.Top + 80)
        $initialReport = Get-ProbeSnapshot $fixture (Join-Path $testDirectory "$arch-initial.json")
        $initial = $initialReport.initial
        $result.pid = $fixture.Process.Id; $result.hwnd = $fixture.Hwnd

        $oldController = Start-Controller $oldHostPath $fixture $regionA (Join-Path $testDirectory "$arch-old-host.txt")
        Wait-ControllerReady $oldController $fixture $false
        $null = Assert-NormalRestored $fixture $initial (Join-Path $testDirectory "$arch-old-attach-normal.json")
        $beforeConcurrent = Get-Mapping $fixture
        $denied = Start-Controller $newHostPath $fixture $regionDenied (Join-Path $testDirectory "$arch-denied-while-old.txt")
        Assert-RejectedController $denied 183
        Assert-Handoff ((Get-Mapping $fixture).ExceptHeartbeat -ceq $beforeConcurrent.ExceptHeartbeat) 'Rejected controller changed the old mapping beyond its existing heartbeat.'
        $result.oldCycle = Test-FullscreenCycle $fixture $regionA $initial (Join-Path $testDirectory "$arch-old")
        Stop-Controller $oldController
        Assert-Handoff ((Get-Mapping $fixture).Active -eq 0) 'The old controller did not deactivate its mapping on QUIT.'
        $null = Assert-NormalRestored $fixture $initial (Join-Path $testDirectory "$arch-between-hosts.json")
        $modules = @(Get-HookModules $fixture)
        Assert-Handoff ($modules.Count -eq 1 -and [string]::Equals($modules[0], $oldDll, [StringComparison]::OrdinalIgnoreCase)) 'The old hook must remain the only resident RegionHook.dll after its controller exits.'

        $newController = Start-Controller $newHostPath $fixture $regionB (Join-Path $testDirectory "$arch-new-host.txt")
        Wait-ControllerReady $newController $fixture $true
        $null = Assert-NormalRestored $fixture $initial (Join-Path $testDirectory "$arch-new-attach-normal.json")
        $beforeConcurrent = Get-Mapping $fixture
        $denied = Start-Controller $newHostPath $fixture $regionDenied (Join-Path $testDirectory "$arch-denied-while-new.txt")
        Assert-RejectedController $denied 183
        Assert-Handoff ((Get-Mapping $fixture).ExceptHeartbeat -ceq $beforeConcurrent.ExceptHeartbeat) 'Rejected controller changed the reused mapping beyond its existing heartbeat.'
        $result.newCycle = Test-FullscreenCycle $fixture $regionB $initial (Join-Path $testDirectory "$arch-new")
        $modules = @(Get-HookModules $fixture)
        Assert-Handoff ($modules.Count -eq 1 -and [string]::Equals($modules[0], $oldDll, [StringComparison]::OrdinalIgnoreCase)) 'Takeover loaded another RegionHook.dll instead of keeping the old one.'
        $result.residentHookPaths = $modules; $result.sameTargetProcess = $true; $result.concurrentControllersRefusedWithoutConfigWrites = $true
        Stop-Controller $newController
        $null = Assert-NormalRestored $fixture $initial (Join-Path $testDirectory "$arch-final-normal.json")
        Assert-Handoff ((Get-Mapping $fixture).Active -eq 0) 'The new controller did not deactivate its mapping on QUIT.'
        [DisplayWeaveHandoffNative]::Send($fixture.Process.Id, $fixture.Hwnd, 0x8004)
        Assert-Handoff ($fixture.Process.WaitForExit(3000) -and $fixture.Process.ExitCode -eq 0) 'The owned rendering fixture did not exit cleanly.'

        # This separate fresh fixture has no DLL. The synthetic incompatible
        # mapping is created exclusively by this harness and is never an app's mapping.
        $incompatible = Start-Fixture $probePath (Join-Path $testDirectory "$arch-incompatible-live.json") ($bounds.Left + 80) ($bounds.Top + 80)
        $synthetic = New-Object 'DisplayWeaveHandoffNative+IncompatibleMapping' ($incompatible.Process.Id)
        $beforeInvalid = [Convert]::ToBase64String((Get-Mapping $incompatible).Bytes)
        $denied = Start-Controller $newHostPath $incompatible $regionB (Join-Path $testDirectory "$arch-incompatible-host.txt")
        Assert-RejectedController $denied 1306
        Assert-Handoff ([Convert]::ToBase64String((Get-Mapping $incompatible).Bytes) -ceq $beforeInvalid) 'Rejected incompatible mapping was modified.'
        Assert-Handoff (@(Get-HookModules $incompatible).Count -eq 0) 'The incompatible mapping target received a RegionHook.dll.'
        $synthetic.Dispose(); $synthetic = $null
        [DisplayWeaveHandoffNative]::Send($incompatible.Process.Id, $incompatible.Hwnd, 0x8004)
        Assert-Handoff ($incompatible.Process.WaitForExit(3000) -and $incompatible.Process.ExitCode -eq 0) 'The incompatible fixture did not exit cleanly.'
        $result.incompatibleMappingRefusedWithoutWrites = $true
        $result.success = $true; $result.message = 'Old controller exited; new controller reused the sole old DLL in the same process, changed regions, rendered fullscreen, and restored normal placement.'
        Write-Output "PASS $arch resident hook handoff, concurrent-controller exclusion, and incompatible-mapping refusal."
    }
    catch {
        $failed = $true; $result.message = $_.Exception.Message
        Write-Warning "FAIL $arch handoff: $($result.message)"
    }
    finally {
        foreach ($controller in $script:ownedControllers) {
            try {
                if (-not $controller.Process.HasExited) {
                    $controller.Process.StandardInput.WriteLine('QUIT'); $controller.Process.StandardInput.Flush()
                    if (-not $controller.Process.WaitForExit(3000)) { $controller.Process.Kill(); $null = $controller.Process.WaitForExit(2000) }
                }
                Finish-ControllerLog $controller
            } catch { Write-Warning ('Owned controller cleanup: ' + $_.Exception.Message) }
            finally { $controller.Process.Dispose() }
        }
        $script:ownedControllers.Clear()
        if ($null -ne $synthetic) { $synthetic.Dispose() }
        foreach ($fixtureToClose in $script:ownedFixtures) {
            try {
                if (-not $fixtureToClose.Process.HasExited) {
                    Assert-Fixture $fixtureToClose
                    $null = $fixtureToClose.Process.CloseMainWindow()
                    if (-not $fixtureToClose.Process.WaitForExit(2000)) { $fixtureToClose.Process.Kill(); $null = $fixtureToClose.Process.WaitForExit(2000) }
                }
            } catch { Write-Warning ('Owned fixture cleanup: ' + $_.Exception.Message) }
            finally { $fixtureToClose.Process.Dispose() }
        }
        $script:ownedFixtures.Clear()
        $results.Add([pscustomobject]$result)
        [IO.File]::WriteAllText((Join-Path $testDirectory "$arch-handoff.json"), ($result | ConvertTo-Json -Depth 8))
    }
}
[IO.File]::WriteAllText((Join-Path $testDirectory 'summary.json'), (ConvertTo-Json -InputObject $results.ToArray() -Depth 8))
Write-Output "Handoff reports: $testDirectory"
if ($failed) { exit 1 }
