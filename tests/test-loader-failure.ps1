#requires -Version 5.1
[CmdletBinding()]
param([ValidateSet('x64','x86','all')][string]$Architecture='all',
    [string]$BuildDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'))
$ErrorActionPreference='Stop'
$BuildDirectory=(Resolve-Path -LiteralPath $BuildDirectory).Path
$zigRoot=Join-Path (Split-Path -Parent $PSScriptRoot) '.tools\zig-windows-x86_64-0.13.0'
$zig=Join-Path $zigRoot 'zig.exe'
if(!(Test-Path -LiteralPath $zig)){throw 'Portable Zig compiler is missing.'}
$env:ZIG_GLOBAL_CACHE_DIR=Join-Path (Split-Path -Parent $PSScriptRoot) '.tools\zig-cache'
$env:ZIG_LOCAL_CACHE_DIR=Join-Path (Split-Path -Parent $PSScriptRoot) '.tools\zig-local-cache'
$folder=Join-Path (Split-Path -Parent $PSScriptRoot) ('test-results\loader-failure-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $folder | Out-Null
if(-not ('RegionLoaderFailureFixture' -as [type])) {
Add-Type @'
using System;
using System.Runtime.InteropServices;
using System.Text;
public static class RegionLoaderFailureFixture {
 [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
 [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,StringBuilder name,int count);
 [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wp,IntPtr lp,uint flags,uint timeout,out IntPtr result);
 public static void Send(long handle,int pid,int command) {
   var window=new IntPtr(handle); uint actual;
   var name=new StringBuilder(128);
   GetWindowThreadProcessId(window,out actual); GetClassName(window,name,name.Capacity);
   if(actual!=pid || name.ToString()!="DisplayWeaveLoaderFailureProbe") throw new Exception("Owned fixture identity changed.");
   IntPtr result;
   if(SendMessageTimeout(window,0x8064,new IntPtr(command),IntPtr.Zero,3,2000,out result)==IntPtr.Zero || result==IntPtr.Zero)
     throw new Exception("Owned fixture command failed.");
 }
}
'@
}
foreach($arch in $(if($Architecture -eq 'all'){@('x64','x86')}else{@($Architecture)})) {
    $destination=Join-Path $folder $arch
    New-Item -ItemType Directory -Path $destination | Out-Null
    $target=if($arch -eq 'x64'){'x86_64-windows-gnu'}else{'x86-windows-gnu'}
    $entry=if($arch -eq 'x64'){'-Wl,--entry,DllMain'}else{'-Wl,--entry,DllMain@12'}
    $definition=@()
    if($arch -eq 'x86'){$definition=@((Join-Path (Split-Path -Parent $PSScriptRoot) 'native\legacy\tests\RejectingHook-x86.def'))}
    $dll=Join-Path $destination 'RegionHook.dll'
    & $zig cc -target $target -O2 -shared -nostdlib -fno-stack-protector -fno-builtin $entry `
        -isystem (Join-Path $zigRoot 'lib\libc\include\any-windows-any') `
        (Join-Path (Split-Path -Parent $PSScriptRoot) 'native\legacy\tests\RejectingHook.c') @definition -o $dll -lkernel32 -luser32
    if($LASTEXITCODE -ne 0){throw "Rejecting fixture DLL compilation failed ($arch)."}
    $exe=Join-Path $destination 'LoaderFailureProbe.exe'
    & $zig c++ -target $target -std=c++17 -O2 -municode `
        (Join-Path (Split-Path -Parent $PSScriptRoot) 'native\legacy\tests\LoaderFailureProbe.cpp') -o $exe -luser32
    if($LASTEXITCODE -ne 0){throw "Loader fixture compilation failed ($arch)."}
    $hostPath=Join-Path $destination 'RegionHookHost.exe'
    Copy-Item -LiteralPath (Join-Path $BuildDirectory "native-v2-$arch\RegionHookHost.exe") -Destination $hostPath
    $report=Join-Path $destination 'result.json'
    $probe=$null; $helper=$null; $ready=$null
    try {
        $start=New-Object Diagnostics.ProcessStartInfo
        $start.FileName=$exe
        $start.Arguments='--dll "'+$dll+'" --report "'+$report+'"'
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle='Hidden'
        $probe=[Diagnostics.Process]::Start($start)
        $until=[DateTime]::UtcNow.AddSeconds(10)
        while(!(Test-Path -LiteralPath ($report+'.ready.json'))) {
            if($probe.HasExited -or [DateTime]::UtcNow -gt $until){throw 'Owned loader fixture failed to initialize.'}
            Start-Sleep -Milliseconds 50
        }
        $ready=Get-Content -LiteralPath ($report+'.ready.json') -Raw | ConvertFrom-Json
        if($ready.pid -ne $probe.Id -or $ready.localLoaderError -ne 1114){throw 'Fixture did not reproduce actual LoadLibrary error1114.'}
        $start=New-Object Diagnostics.ProcessStartInfo
        $start.FileName=$hostPath
        $start.Arguments="$($probe.Id) 0x$($ready.hwnd) $($ready.x) $($ready.y) $($ready.width) $($ready.height) --auto --bootstrap-loader"
        $start.UseShellExecute=$false; $start.CreateNoWindow=$true; $start.WindowStyle='Hidden'
        $start.RedirectStandardInput=$true; $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
        $helper=[Diagnostics.Process]::Start($start)
        $stdout=$helper.StandardOutput.ReadToEndAsync(); $stderr=$helper.StandardError.ReadToEndAsync()
        if(!$helper.WaitForExit(16000)){throw 'Owned loader failure exceeded helper startup deadline.'}
        $log=$stdout.Result+$stderr.Result
        [IO.File]::WriteAllText((Join-Path $destination 'host.txt'),$log)
        if($helper.ExitCode -ne 1 -or $log -notmatch 'ERROR LOADER_MODULE_NOT_OBSERVED:' -or
            $log -notmatch 'thread result DWORD 0x00000000' -or $log -notmatch 'target state running' -or
            $log -notmatch 'RegionHook\.dll copies 0' -or $log -notmatch 'remote loader error unavailable' -or
            $log -notmatch 'DisplayWeave status 0x20005201' -or $log -match '(?m)^READY '){
            Write-Output $log; throw 'Helper did not report the distinct missing-module category accurately.'
        }
        if($probe.HasExited){throw 'Owned target exited during the rejected DLL load.'}
        [RegionLoaderFailureFixture]::Send([Convert]::ToInt64($ready.hwnd,16),$probe.Id,1)
        $result=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        if(!$result.success -or !$result.windowHidden){Write-Output ($result | ConvertTo-Json); throw 'Loader failure changed the target or left integration active.'}
        Write-Output "PASS ${arch}: actual local1114; separate host category; target alive and unchanged; active0; no initialization or resident DLL. $report"
    } finally {
        if($helper -and !$helper.HasExited){
            $helper.StandardInput.Close()
            if(!$helper.WaitForExit(3000)){$helper.Kill();$helper.WaitForExit()}
        }
        if($probe -and !$probe.HasExited){
            if($ready){try{[RegionLoaderFailureFixture]::Send([Convert]::ToInt64($ready.hwnd,16),$probe.Id,2)}catch{}}
            if(!$probe.WaitForExit(3000)){$probe.Kill();$probe.WaitForExit()}
        }
        if($helper){$helper.Dispose()}; if($probe){$probe.Dispose()}
    }
}
