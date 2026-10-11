#requires -Version 5.1
[CmdletBinding()]
param(
    [ValidateSet('x64','x86','all')][string]$Architecture='all',
    [string]$BuildDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [ValidateRange(3,30)][int]$Seconds=6
)
$ErrorActionPreference='Stop'
$BuildDirectory=(Resolve-Path -LiteralPath $BuildDirectory).Path
if(Get-Process -Name DisplayWeave -ErrorAction SilentlyContinue){
    throw 'Exit DisplayWeave from its tray before running the isolated projection test, so automatic application handling cannot alter the fixture.'
}
if(-not ('RegionProjectionFixture' -as [type])){
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RegionProjectionFixture {
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wp,IntPtr lp,uint flags,uint timeout,out IntPtr result);
    public static void Close(long handle,int pid) {
        uint actual; GetWindowThreadProcessId(new IntPtr(handle),out actual);
        if(actual!=(uint)pid) throw new Exception("Owned fixture window identity changed.");
        IntPtr result;
        SendMessageTimeout(new IntPtr(handle),0x10,IntPtr.Zero,IntPtr.Zero,3,2000,out result);
    }
}
'@
}
$folder=Join-Path (Split-Path -Parent $PSScriptRoot) ('test-results\projection-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $folder | Out-Null
function Start-Owned([string]$Executable,[string]$Arguments) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$Executable; $start.Arguments=$Arguments
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    return @{ Process=$process; Out=$process.StandardOutput.ReadToEndAsync(); Err=$process.StandardError.ReadToEndAsync() }
}
function Save-Output($Owned,[string]$Prefix) {
    if($Owned -and $Owned.Process.HasExited){
        $Owned.Out.Result | Set-Content -LiteralPath ($Prefix+'.stdout.txt')
        $Owned.Err.Result | Set-Content -LiteralPath ($Prefix+'.stderr.txt')
    }
}
foreach($arch in $(if($Architecture -eq 'all'){@('x64','x86')}else{@($Architecture)})){
    $probe=$null; $presenter=$null; $ready=$null
    $probeReport=Join-Path $folder ($arch+'-source.json')
    $presenterReport=Join-Path $folder ($arch+'-presenter.json')
    $bin=Join-Path $BuildDirectory ('projection-'+$arch)
    $presenterExe=Join-Path $bin 'RegionPresenter.exe'
    $probeExe=Join-Path $bin 'ProjectionProbe.exe'
    try {
        foreach($file in @($presenterExe,$probeExe)){if(!(Test-Path -LiteralPath $file -PathType Leaf)){throw "Missing executable: $file"}}
        $inventory=& $presenterExe --list-outputs
        if($LASTEXITCODE -ne 0){throw 'Output discovery failed.'}
        $inventory | Set-Content -LiteralPath (Join-Path $folder ($arch+'-outputs-before.json'))
        # Small disjoint source and destination on the primary physical output.
        # The test only samples this newly created application's client pixels.
        $probe=Start-Owned $probeExe ('--rect 80,100,640,360 --seconds '+($Seconds+20)+' --report "'+$probeReport+'"')
        $until=[DateTime]::UtcNow.AddSeconds(10)
        while(!(Test-Path -LiteralPath ($probeReport+'.ready.json'))){
            if($probe.Process.HasExited -or [DateTime]::UtcNow -gt $until){throw 'Projection source did not become ready.'}
            Start-Sleep -Milliseconds 100
        }
        $ready=Get-Content -LiteralPath ($probeReport+'.ready.json') -Raw | ConvertFrom-Json
        if($ready.pid -ne $probe.Process.Id -or $ready.regionHookLoaded){throw 'Source identity changed or an in-app DisplayWeave component interfered.'}
        $crop='{0},{1},{2},{3}' -f $ready.client.x,$ready.client.y,$ready.client.width,$ready.client.height
        if([string]::IsNullOrWhiteSpace($ready.outputDevice)){throw 'Source did not identify its DXGI output.'}
        $arguments='--source-device "'+$ready.outputDevice+'" --fixture-source-rect '+$crop+' --destination 900,100,640,360 --seconds '+$Seconds+' --report "'+$presenterReport+'" --verify-fixture-colors --allow-sdr-conversion'
        $presenter=Start-Owned $presenterExe $arguments
        if(!$presenter.Process.WaitForExit(($Seconds+15)*1000)){throw 'Presenter exceeded its bounded test duration.'}
        Save-Output $presenter ($presenterReport+'.process')
        if($presenter.Process.ExitCode -ne 0){throw ('Presenter failed: '+$presenter.Err.Result+$presenter.Out.Result)}
        $result=Get-Content -LiteralPath $presenterReport -Raw | ConvertFrom-Json
        if(!$result.success -or !$result.fixtureVerified -or $result.presentedFrames -lt 2 -or $result.failedPresents -ne 0){throw 'Presenter output or GPU color verification failed.'}
        [RegionProjectionFixture]::Close([Convert]::ToInt64($ready.hwnd,16),$probe.Process.Id)
        if(!$probe.Process.WaitForExit(5000)){throw 'Source did not close normally.'}
        Save-Output $probe ($probeReport+'.process')
        $source=Get-Content -LiteralPath $probeReport -Raw | ConvertFrom-Json
        if($probe.Process.ExitCode -ne 0 -or $source.failedPresents -ne 0 -or $source.regionHookLoaded){throw 'Source rendering or isolation check failed.'}
        $after=& $presenterExe --list-outputs
        if($LASTEXITCODE -ne 0){throw 'Final output discovery failed.'}
        $after | Set-Content -LiteralPath (Join-Path $folder ($arch+'-outputs-after.json'))
        if(($inventory -join "`n") -ne ($after -join "`n")){throw 'Display topology changed during the test.'}
        Write-Output "PASS $arch GPU projection: $($result.presentedFrames) presented frames, verified fixture colors, no failed presents, unchanged display topology. Report: $presenterReport"
    } finally {
        foreach($owned in @($presenter,$probe)){
            if($owned -and !$owned.Process.HasExited){
                # These are only Process objects returned by this test's own Start.
                if($owned -eq $probe -and $ready){
                    try{[RegionProjectionFixture]::Close([Convert]::ToInt64($ready.hwnd,16),$owned.Process.Id)}catch{}
                }
                if(!$owned.Process.WaitForExit(2000)){$owned.Process.Kill(); $owned.Process.WaitForExit()}
            }
        }
        Save-Output $presenter ($presenterReport+'.process')
        Save-Output $probe ($probeReport+'.process')
    }
}
