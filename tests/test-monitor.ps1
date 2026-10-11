#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('x64','x86','all')][string]$Architecture='all',
    [string]$BuildDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [switch]$BootstrapLoader,[switch]$FilteredMessages,[switch]$HeartbeatExpiry,[switch]$Reconnect,[switch]$DeferMessages)
$ErrorActionPreference='Stop'
$BuildDirectory=(Resolve-Path -LiteralPath $BuildDirectory).Path
if (-not ('RegionMonitorFixture' -as [type])) {
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class RegionMonitorFixture {
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hwnd,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd,StringBuilder name,int max);
 [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr hwnd,uint msg,IntPtr wp,IntPtr lp,uint flags,uint timeout,out IntPtr result);
 public static void Send(long handle,int pid,int phase) {
   IntPtr hwnd=new IntPtr(handle); uint owner; GetWindowThreadProcessId(hwnd,out owner);
   var name=new StringBuilder(128); GetClassName(hwnd,name,name.Capacity);
   if(owner!=pid || name.ToString()!="DisplayWeaveMonitorProbe") throw new Exception("Fixture window identity changed.");
   IntPtr result;
   if(SendMessageTimeout(hwnd,0x8000+100,new IntPtr(phase),IntPtr.Zero,3,5000,out result)==IntPtr.Zero)
     throw new Exception("Fixture phase timed out: "+phase);
 }
}
'@
}
$folder=Join-Path (Split-Path -Parent $PSScriptRoot) ('test-results\monitor-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $folder -Force | Out-Null
function Phase([int]$Number) {
    if($probe.HasExited) { throw "Fixture exited before phase $Number" }
    [RegionMonitorFixture]::Send([Convert]::ToInt64($ready.hwnd,16),$probe.Id,$Number)
    $value=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    if($value.phase -ne $Number -or $value.failures -gt 0) {
        Get-Content -LiteralPath ($report+'.txt') | Write-Output
        throw "Monitor fixture failed phase $Number; report $report"
    }
}
function Command([string]$Value) {
    if($helper.HasExited) { throw 'Native helper exited unexpectedly.' }
    $helper.StandardInput.WriteLine($Value); $helper.StandardInput.Flush()
    Start-Sleep -Milliseconds 400
}
foreach($arch in $(if($Architecture -eq 'all'){@('x64','x86')}else{@($Architecture)})) {
    $probe=$null; $helper=$null
    $report=Join-Path $folder ($arch+'.json')
    try {
        $start=New-Object Diagnostics.ProcessStartInfo
        $start.FileName=Join-Path $BuildDirectory "native-v2-$arch\FullscreenProbe.exe"
        $start.Arguments='--verify-monitor --report "'+$report+'"'
        if($FilteredMessages) { $start.Arguments+=' --sent-messages-only' }
        if($DeferMessages) { $start.Arguments+=' --defer-messages' }
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true
        $probe=[Diagnostics.Process]::Start($start)
        $until=[DateTime]::UtcNow.AddSeconds(15)
        while(!(Test-Path -LiteralPath ($report+'.ready.json'))) {
            if($probe.HasExited -or [DateTime]::UtcNow -gt $until) { throw 'Monitor fixture failed to initialize.' }
            Start-Sleep -Milliseconds 100
        }
        $ready=Get-Content -LiteralPath ($report+'.ready.json') -Raw | ConvertFrom-Json
        if($ready.pid -ne $probe.Id) { throw 'Fixture identity mismatch.' }
        $width=[int][Math]::Floor($ready.width/3); $y=[int]$ready.y+80
        $height=[int][Math]::Min(720,$ready.height-160)
        if($width -lt 400 -or $height -lt 300) { throw 'Fixture requires at least a 1200 by 460 pixel primary monitor.' }
        $x=[int]$ready.x; $middle=$x+$width; $right=$middle+$width
        $regions="$x,$y,$width,$height;$middle,$y,$width,$height;$right,$y,$width,$height"
        $start=New-Object Diagnostics.ProcessStartInfo
        $start.FileName=Join-Path $BuildDirectory "native-v2-$arch\RegionHookHost.exe"
        $start.Arguments="$($probe.Id) 0x$($ready.hwnd) $middle $y $width $height --auto --regions `"$regions`""
        if($BootstrapLoader) { $start.Arguments+=' --bootstrap-loader' }
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle='Hidden'
        $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $helper=[Diagnostics.Process]::Start($start)
        $errors=$helper.StandardError.ReadToEndAsync()
        $until=[DateTime]::UtcNow.AddSeconds(16); $log=''
        do {
            $line=$helper.StandardOutput.ReadLineAsync()
            if(!$line.Wait([Math]::Max(1,[int]($until-[DateTime]::UtcNow).TotalMilliseconds))) { throw 'Helper readiness deadline expired.' }
            $value=$line.Result; $log+=$value+"`r`n"
            if($null -eq $value) { throw ('Helper did not start: '+$log+$errors.Result) }
        } while($value -notmatch '^READY ')
        $output=$helper.StandardOutput.ReadToEndAsync()
        if($value -notmatch 'CAPS 5$') { throw ('Expected new USER32 monitor worker: '+$value) }
        if(($BootstrapLoader -or $DeferMessages) -and $log -notmatch 'INIT Standard Windows loader') {
            throw 'The fixture did not exercise the standard loader path.'
        }
        Start-Sleep -Milliseconds 500
        Phase 1
        Phase 11
        Start-Sleep -Milliseconds 400
        Phase 3
        Start-Sleep -Milliseconds 400
        Phase 2
        Start-Sleep -Milliseconds 500
        Phase 3
        Start-Sleep -Milliseconds 400
        Phase 4
        Start-Sleep -Milliseconds 600
        Phase 5
        Command "UPDATE $right $y $width $height"
        Phase 6
        Command "LAYOUT $middle,$y,$width,$height;$right,$y,$width,$height"
        Phase 7
        Command 'MODE FORCE'
        Phase 8
        if($HeartbeatExpiry) {
            $helper.Kill(); $helper.WaitForExit()
            Start-Sleep -Milliseconds 4700
            Phase 10
        } else {
            Command 'OFF'
            Phase 9
            $helper.StandardInput.Close()
            if(!$helper.WaitForExit(5000)) { throw 'Helper failed to exit after EOF.' }
        }
        [IO.File]::WriteAllText((Join-Path $folder ($arch+'-host.txt')),$log+$output.Result+$errors.Result)
        if($Reconnect) {
            $helper.Dispose()
            $helper=[Diagnostics.Process]::Start($start)
            $errors=$helper.StandardError.ReadToEndAsync()
            $until=[DateTime]::UtcNow.AddSeconds(16); $log=''
            do {
                $line=$helper.StandardOutput.ReadLineAsync()
                if(!$line.Wait([Math]::Max(1,[int]($until-[DateTime]::UtcNow).TotalMilliseconds))) { throw 'Reconnection deadline expired.' }
                $value=$line.Result; $log+=$value+"`r`n"
                if($null -eq $value) { throw ('Reconnection failed: '+$log+$errors.Result) }
            } while($value -notmatch '^READY ')
            $output=$helper.StandardOutput.ReadToEndAsync()
            if($value -notmatch '^READY \d+ REUSED CAPS 5$') { throw ('Reconnection did not reuse monitor worker: '+$value) }
            Start-Sleep -Milliseconds 500
            Phase 1
            Command 'MODE FORCE'
            Phase 8
            Command 'OFF'
            Phase 9
            $helper.StandardInput.Close()
            if(!$helper.WaitForExit(5000)) { throw 'Reconnected helper did not exit.' }
            $probe.Refresh()
            if(@($probe.Modules | Where-Object ModuleName -IEQ 'RegionHook.dll').Count -ne 1) { throw 'Reconnection did not retain exactly one native worker.' }
            [IO.File]::WriteAllText((Join-Path $folder ($arch+'-reconnected-host.txt')),$log+$output.Result+$errors.Result)
        }
        $result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        Write-Output "PASS $arch monitor engine: $($result.checks) checks, $($result.frames) frames, $($result.failedPresents) failed presents. Loader=$BootstrapLoader Deferred=$DeferMessages Filtered=$FilteredMessages Heartbeat=$HeartbeatExpiry. $report"
    } finally {
        if($helper -and !$helper.HasExited) { $helper.StandardInput.Close(); if(!$helper.WaitForExit(5000)){$helper.Kill();$helper.WaitForExit()} }
        if($probe -and !$probe.HasExited) { $probe.CloseMainWindow() | Out-Null; if(!$probe.WaitForExit(3000)){$probe.Kill();$probe.WaitForExit()} }
        if($helper){$helper.Dispose()}; if($probe){$probe.Dispose()}
    }
}
