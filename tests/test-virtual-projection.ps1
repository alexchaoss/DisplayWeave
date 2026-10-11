#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$LeaseReport,
    [string]$BuildDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [ValidateSet('x64','x86')][string]$Architecture='x64',
    [ValidateRange(3,30)][int]$Seconds=6,
    [switch]$Interactive,
    [switch]$ExerciseSyntheticInput,
    [switch]$ExerciseReleaseShortcut,
    [switch]$ExerciseGuardianRecovery
)
$ErrorActionPreference='Stop'
if($ExerciseSyntheticInput -and !$Interactive){throw '-ExerciseSyntheticInput requires -Interactive.'}
if($ExerciseReleaseShortcut -and !$Interactive){throw '-ExerciseReleaseShortcut requires -Interactive.'}
if($ExerciseGuardianRecovery -and (!$Interactive -or $Seconds -lt 6 -or $ExerciseSyntheticInput -or $ExerciseReleaseShortcut)){throw '-ExerciseGuardianRecovery requires -Interactive, at least 6 seconds, and no synthetic input or release-shortcut exercise.'}
if(![IO.Path]::IsPathRooted($LeaseReport)){throw '-LeaseReport must be an absolute lease.json path.'}
$LeaseReport=[IO.Path]::GetFullPath($LeaseReport)
$BuildDirectory=(Resolve-Path -LiteralPath $BuildDirectory).Path
if(Get-Process -Name DisplayWeave -ErrorAction SilentlyContinue){throw 'Exit DisplayWeave before the isolated virtual-output projection fixture.'}
if(Get-Process -Name 'Diablo IV' -ErrorAction SilentlyContinue){throw 'Close Diablo IV before the isolated virtual-output projection fixture.'}
$leaseExe=Join-Path $BuildDirectory 'projection-x64\RegionDisplayLease.exe'
$bin=Join-Path $BuildDirectory ('projection-'+$Architecture)
$probeExe=Join-Path $bin 'ProjectionProbe.exe'
$presenterExe=Join-Path $bin 'RegionPresenter.exe'
foreach($file in @($leaseExe,$probeExe,$presenterExe)){if(!(Test-Path -LiteralPath $file -PathType Leaf)){throw "Missing executable: $file"}}
$readyPath=$LeaseReport+'.ready.json'
if(!(Test-Path -LiteralPath $readyPath -PathType Leaf)){throw 'The temporary display lease has no ready journal.'}
$leaseReady=Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
if(!$leaseReady.ownershipVerified -or !$leaseReady.physicalPreserved -or !$leaseReady.primaryAndWorkAreaObserved -or
    [string]::IsNullOrWhiteSpace($leaseReady.outputDevice) -or [int64]$leaseReady.pid -le 0){throw 'Lease ownership or physical display preservation was not verified.'}
if(!$leaseReady.releaseProtocol -or $leaseReady.releaseProtocol.maxDurationMs -ne 5000 -or
    [string]::IsNullOrWhiteSpace($leaseReady.releaseProtocol.beginEvent) -or [string]::IsNullOrWhiteSpace($leaseReady.releaseProtocol.completeEvent) -or
    !$leaseReady.releaseProtocol.beginEvent.StartsWith('Local\') -or !$leaseReady.releaseProtocol.completeEvent.StartsWith('Local\') -or
    $leaseReady.releaseProtocol.beginEvent -ceq $leaseReady.releaseProtocol.completeEvent){throw 'The lease does not advertise the bounded two-phase release protocol.'}
if(-not ('RegionProjectionProcessIdentity' -as [type])){
Add-Type @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
public static class RegionProjectionProcessIdentity {
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenProcess(uint access,bool inherit,uint pid);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,StringBuilder path,ref uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(Microsoft.Win32.SafeHandles.SafeWaitHandle process,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetExitCodeProcess(Microsoft.Win32.SafeHandles.SafeWaitHandle process,out uint code);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint WaitForSingleObject(Microsoft.Win32.SafeHandles.SafeWaitHandle process,uint milliseconds);
    [DllImport("kernel32.dll",EntryPoint="QueryFullProcessImageNameW",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)] static extern bool QueryPinnedImage(Microsoft.Win32.SafeHandles.SafeWaitHandle process,uint flags,StringBuilder path,ref uint size);
    public sealed class RetainedProcess : IDisposable {
        readonly Microsoft.Win32.SafeHandles.SafeWaitHandle handle;
        internal RetainedProcess(int pid,long expectedCreation,string expectedPath) {
            // Keep the exact kernel process object, including after termination.
            IntPtr native=OpenProcess(0x101000,false,(uint)pid);
            if(native==IntPtr.Zero)throw new Win32Exception(Marshal.GetLastWin32Error());
            handle=new Microsoft.Win32.SafeHandles.SafeWaitHandle(native,true);
            try {
                long created,exited,kernel,user;
                if(!GetProcessTimes(handle,out created,out exited,out kernel,out user))throw new Win32Exception(Marshal.GetLastWin32Error());
                if(created!=expectedCreation || HasExited)throw new InvalidOperationException("Lease process lifetime changed before its handle was retained.");
                uint size=32768;var path=new StringBuilder((int)size);
                if(!QueryPinnedImage(handle,0,path,ref size))throw new Win32Exception(Marshal.GetLastWin32Error());
                if(!String.Equals(System.IO.Path.GetFullPath(path.ToString()),System.IO.Path.GetFullPath(expectedPath),StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException("Lease executable identity changed before its handle was retained.");
            } catch {handle.Dispose();throw;}
        }
        public bool HasExited {get{return WaitForExit(0);}}
        public bool WaitForExit(int milliseconds) {
            if(milliseconds<0 || milliseconds>15000)throw new ArgumentOutOfRangeException("milliseconds");
            uint result=WaitForSingleObject(handle,(uint)milliseconds);
            if(result==0)return true;
            if(result==258)return false;
            throw new Win32Exception(Marshal.GetLastWin32Error(),"Waiting on the exact retained lease process failed.");
        }
        public uint ExitCode {
            get {
                if(!HasExited)throw new InvalidOperationException("The retained lease process is still running.");
                uint code;if(!GetExitCodeProcess(handle,out code))throw new Win32Exception(Marshal.GetLastWin32Error());
                return code;
            }
        }
        public void Dispose(){handle.Dispose();}
    }
    public static RetainedProcess Retain(int pid,long expectedCreation,string expectedPath){return new RetainedProcess(pid,expectedCreation,expectedPath);}
    public static string Image(int pid) {
        IntPtr handle=OpenProcess(0x1000,false,(uint)pid);
        if(handle==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            uint size=32768; var path=new StringBuilder((int)size);
            if(!QueryFullProcessImageName(handle,0,path,ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return path.ToString();
        } finally {CloseHandle(handle);}
    }
}
'@
}
$leaseProcess=Get-Process -Id ([int]$leaseReady.pid) -ErrorAction Stop
$leaseStarted=$leaseProcess.StartTime.ToUniversalTime()
if(![string]::Equals([IO.Path]::GetFullPath([RegionProjectionProcessIdentity]::Image($leaseProcess.Id)),[IO.Path]::GetFullPath($leaseExe),[StringComparison]::OrdinalIgnoreCase)){
    throw 'Lease PID is not the expected RegionDisplayLease executable.'
}
if($leaseStarted -gt (Get-Item -LiteralPath $readyPath).LastWriteTimeUtc -or
    $leaseReady.instanceId -notlike ('SWD\DisplayWeaveProjection\Fixture-'+$leaseProcess.Id+'-*')){
    throw 'Lease ready journal does not belong to the current process lifetime.'
}
if($leaseStarted.AddSeconds([int]$leaseReady.seconds) -lt [DateTime]::UtcNow.AddSeconds($Seconds+15)){
    throw 'The temporary display lease has insufficient remaining time for the bounded test and cleanup.'
}
if(-not ('RegionVirtualProjectionFixture' -as [type])){
Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class RegionVirtualProjectionFixture {
    [DllImport("kernel32.dll")] static extern uint WaitForSingleObject(IntPtr handle,uint timeout);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetProcessTimes(IntPtr process,out long created,out long exited,out long kernel,out long user);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool QueryFullProcessImageName(IntPtr process,uint flags,System.Text.StringBuilder path,ref uint size);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern IntPtr OpenEventW(uint access,bool inherit,string name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetEvent(IntPtr handle);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr window,out uint pid);
    [DllImport("user32.dll",SetLastError=true)] static extern IntPtr SendMessageTimeout(IntPtr window,uint message,IntPtr wp,IntPtr lp,uint flags,uint timeout,out IntPtr result);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    delegate bool EnumWindowCallback(IntPtr window,IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowCallback callback,IntPtr data);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int GetClassName(IntPtr window,System.Text.StringBuilder name,int capacity);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] struct Rect {public int left,top,right,bottom;}
    [DllImport("user32.dll")] static extern bool GetClipCursor(out Rect rectangle);
    [StructLayout(LayoutKind.Sequential)] struct MouseInput {public int dx,dy;public uint mouseData,flags,time;public UIntPtr extra;}
    [StructLayout(LayoutKind.Sequential)] struct KeyboardInput {public ushort key,scan;public uint flags,time;public UIntPtr extra;}
    [StructLayout(LayoutKind.Explicit)] struct InputUnion {[FieldOffset(0)]public MouseInput mouse;[FieldOffset(0)]public KeyboardInput keyboard;}
    [StructLayout(LayoutKind.Sequential)] struct Input {public uint type;public InputUnion data;}
    [DllImport("user32.dll",SetLastError=true)] static extern uint SendInput(uint count,Input[] inputs,int size);
    public static void SignalRelease(string name) {
        if(String.IsNullOrEmpty(name) || !name.StartsWith("Local\\",StringComparison.Ordinal)) throw new ArgumentException("Invalid private release event name.");
        IntPtr handle=OpenEventW(0x0002,false,name); // EVENT_MODIFY_STATE only; no managed-runtime overload dependency.
        if(handle==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Opening the private release event failed.");
        try {if(!SetEvent(handle)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Signaling the private release event failed.");}
        finally {CloseHandle(handle);}
    }
    public static void Exercise(long handle,int pid) {
        uint actual; IntPtr hwnd=new IntPtr(handle); GetWindowThreadProcessId(hwnd,out actual);
        if(actual!=(uint)pid || GetForegroundWindow()!=hwnd) throw new Exception("Owned source must have foreground focus before synthetic fixture input.");
        foreach(int key in new int[]{0x10,0x11,0x12,0x5B,0x5C,0x5A})
            if((GetAsyncKeyState(key)&0x8000)!=0) throw new Exception("Release modifier keys and Z before the tagged synthetic fixture test.");
        // One harmless tagged character and a small pointer delta, only while
        // our newly started probe owns the foreground. Never sends clicks,
        // shortcuts, Enter or untagged events; this is not hardware validation.
        var inputs=new Input[3];
        inputs[0].type=1;inputs[0].data.keyboard.key=0x5A;inputs[0].data.keyboard.extra=new UIntPtr(0x52535054);
        inputs[1]=inputs[0];inputs[1].data.keyboard.flags=2;
        inputs[2].type=0;inputs[2].data.mouse.dx=4;inputs[2].data.mouse.dy=2;inputs[2].data.mouse.flags=1;inputs[2].data.mouse.extra=new UIntPtr(0x52535054);
        uint inserted=SendInput((uint)inputs.Length,inputs,Marshal.SizeOf(typeof(Input)));
        if(inserted!=inputs.Length) {
            if(inserted==1) SendInput(1,new Input[]{inputs[1]},Marshal.SizeOf(typeof(Input))); // Balance a partial key-down.
            throw new Exception("Synthetic fixture input was not completely accepted.");
        }
    }
    static void VerifyOwned(System.Diagnostics.Process process,long created,string expectedPath,IntPtr window,string expectedClass) {
        long actualCreated,exit,kernel,user;uint pid;
        if(process==null || process.HasExited || WaitForSingleObject(process.Handle,0)!=258 ||
            !GetProcessTimes(process.Handle,out actualCreated,out exit,out kernel,out user) || actualCreated!=created)
            throw new Exception("Owned fixture process lifetime changed before release input.");
        uint size=32768;var path=new System.Text.StringBuilder((int)size);
        if(!QueryFullProcessImageName(process.Handle,0,path,ref size) ||
            !String.Equals(System.IO.Path.GetFullPath(path.ToString()),System.IO.Path.GetFullPath(expectedPath),StringComparison.OrdinalIgnoreCase))
            throw new Exception("Owned fixture executable changed before release input.");
        var name=new System.Text.StringBuilder(128);
        if(GetWindowThreadProcessId(window,out pid)==0 || pid!=(uint)process.Id ||
            GetClassName(window,name,name.Capacity)==0 || name.ToString()!=expectedClass || !IsWindowVisible(window) || IsIconic(window))
            throw new Exception("Owned fixture HWND, class or visibility changed before release input.");
    }
    static Input Key(ushort key,bool up) {
        var input=new Input();input.type=1;input.data.keyboard.key=key;input.data.keyboard.flags=up?2U:0U;
        input.data.keyboard.extra=new UIntPtr(0x5253524C);return input;
    }
    // Pure prefix accounting: only release key-downs actually accepted from
    // this tagged batch. Initial key state is checked before the batch is sent.
    static Input[] BalanceShortcutPrefix(uint inserted) {
        var ups=new System.Collections.Generic.List<Input>();
        if(inserted==3)ups.Add(Key(0x79,true));
        if(inserted>=2 && inserted<5)ups.Add(Key(0x12,true));
        if(inserted>=1 && inserted<6)ups.Add(Key(0x11,true));
        return ups.ToArray();
    }
    public static void ReleaseShortcut(long sourceWindow,System.Diagnostics.Process source,long sourceCreated,string sourcePath,
        long presenterWindow,System.Diagnostics.Process presenter,long presenterCreated,string presenterPath,int left,int top,int right,int bottom) {
        IntPtr sourceHwnd=new IntPtr(sourceWindow),presenterHwnd=new IntPtr(presenterWindow);
        VerifyOwned(source,sourceCreated,sourcePath,sourceHwnd,"DisplayWeaveProjectionProbe");
        VerifyOwned(presenter,presenterCreated,presenterPath,presenterHwnd,"DisplayWeaveProjectionPreview");
        Rect clip;
        if(GetForegroundWindow()!=sourceHwnd || !GetClipCursor(out clip) || clip.left!=left || clip.top!=top || clip.right!=right || clip.bottom!=bottom)
            throw new Exception("Owned source is no longer foreground with its active input clip.");
        foreach(int key in new int[]{0x10,0x11,0x12,0x5B,0x5C,0x79})
            if((GetAsyncKeyState(key)&0x8000)!=0)throw new Exception("Release all modifiers and F10 before the tagged release-shortcut fixture.");
        if(source.HasExited || presenter.HasExited || GetForegroundWindow()!=sourceHwnd)
            throw new Exception("Owned input session ended before the tagged release shortcut.");
        var inputs=new Input[]{Key(0x11,false),Key(0x12,false),Key(0x79,false),Key(0x79,true),Key(0x12,true),Key(0x11,true)};
        uint inserted=SendInput((uint)inputs.Length,inputs,Marshal.SizeOf(typeof(Input)));
        if(inserted!=(uint)inputs.Length){
            var ups=BalanceShortcutPrefix(inserted);
            uint balanced=ups.Length==0?0:SendInput((uint)ups.Length,ups,Marshal.SizeOf(typeof(Input)));
            if(balanced!=(uint)ups.Length)throw new Exception("Release-shortcut insertion was partial and balancing its owned key-downs was incomplete.");
            throw new Exception("Tagged release-shortcut input was not completely accepted; accepted key-downs were balanced.");
        }
    }
    public static void Close(long handle,int pid) {
        uint actual; GetWindowThreadProcessId(new IntPtr(handle),out actual);
        if(actual!=(uint)pid) throw new Exception("Owned source window identity changed.");
        IntPtr result;
        if(SendMessageTimeout(new IntPtr(handle),0x10,IntPtr.Zero,IntPtr.Zero,3,2000,out result)==IntPtr.Zero)
            throw new Exception("Owned source did not acknowledge its close request.");
    }
    public static long PresenterWindow(int pid) {
        IntPtr found=IntPtr.Zero;
        EnumWindows(delegate(IntPtr hwnd,IntPtr unused) {
            uint actual;GetWindowThreadProcessId(hwnd,out actual);
            if(actual==(uint)pid){var name=new System.Text.StringBuilder(128);GetClassName(hwnd,name,name.Capacity);
                if(name.ToString()=="DisplayWeaveProjectionPreview"){found=hwnd;return false;}}
            return true;
        },IntPtr.Zero);
        return found.ToInt64();
    }
}
'@
}
$folder=Join-Path (Split-Path -Parent $PSScriptRoot) ('test-results\virtual-projection-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $folder | Out-Null
$probeReport=Join-Path $folder 'source.json'
$presenterReport=Join-Path $folder 'presenter.json'
$probe=$null;$presenter=$null;$sourceReady=$null;$source=$null;$result=$null
$leasePin=$null
$activeReady=$null;$inputExercised=$false;$activeReadyWritten=$null;$lastObservedFrame=0
$releaseShortcutSent=$false
$inventoryBefore=$null;$topologyBefore=$null
$releaseSignalTime=$null;$releaseBegun=$false;$releaseCompleteSent=$false;$releaseFinal=$null
$cleanupErrors=New-Object 'System.Collections.Generic.List[string]'
$outcome=[ordered]@{
    success=$false;failure=$null;leasePid=$leaseProcess.Id;leaseReport=$LeaseReport;
    outputDevice=$leaseReady.outputDevice;sourceReport=$probeReport;presenterReport=$presenterReport;
    exclusiveApiReported=$false;fixtureColorsVerified=$false;sourceRestored=$false;
    exclusiveDuringProjectionObserved=$false;exclusiveDuringProjectionSamples=0;exclusiveDuringInteractionObserved=$false;
    activeExclusiveEvidence=(Join-Path $folder 'source-during-projection.json');
    releaseBeginAcknowledged=$false;releaseCompleteSent=$false;releaseCompleted=$false;releaseTransitionObserved=$false;
    leaseExited=$false;originalTopologyObservedAfterClose=$false;physicalRestoredAfterRelease=$false;
    leaseExitCodeObserved=$false;leaseExitCode=$null;managedLeaseExitCode=$null;
    ownedVirtualOutputRemoved=$false;remainingPhysicalInventoryMatches=$false;remainingPhysicalTopologyMatches=$false;
    regionHookLoaded=$null;cleanupErrors=@()
    interactiveRequested=[bool]$Interactive;inputActiveObserved=$false;syntheticInputRequested=[bool]$ExerciseSyntheticInput;syntheticInputVerified=$false;hardwareInputVerified=$false
    guardianRecoveryRequested=[bool]$ExerciseGuardianRecovery;guardianRecoveryVerified=$false
    releaseShortcutRequested=[bool]$ExerciseReleaseShortcut;syntheticReleaseShortcutAttempted=$false;syntheticReleaseShortcutSent=$false;syntheticReleaseShortcutVerified=$false
    releaseShortcutMethod='tagged SendInput Ctrl+Alt+F10';releaseShortcutTag='0x5253524C';releaseShortcutObservedReason=$null;physicalReleaseShortcutVerified=$false
}
function Assert-Lease {
    $leaseProcess.Refresh()
    if(!$leasePin -or $leasePin.HasExited){throw 'The exact retained temporary display lease is not alive.'}
    if($leaseProcess.StartTime.ToUniversalTime() -ne $leaseStarted){throw 'Lease process identity changed.'}
    $current=Get-Content -LiteralPath $readyPath -Raw | ConvertFrom-Json
    if($current.pid -ne $leaseProcess.Id -or !$current.ownershipVerified -or !$current.physicalPreserved -or
        $current.instanceId -ine $leaseReady.instanceId -or $current.outputDevice -ine $leaseReady.outputDevice){throw 'Lease journal identity or ownership changed.'}
    if($current.releaseProtocol.beginEvent -cne $leaseReady.releaseProtocol.beginEvent -or
        $current.releaseProtocol.completeEvent -cne $leaseReady.releaseProtocol.completeEvent){throw 'Lease release endpoints changed.'}
}
function Begin-LeaseRelease {
    if($script:releaseBegun){return}
    Assert-Lease
    if(!$script:releaseSignalTime){
        $signaledAt=[DateTime]::UtcNow
        [RegionVirtualProjectionFixture]::SignalRelease([string]$leaseReady.releaseProtocol.beginEvent)
        $script:releaseSignalTime=$signaledAt
    }
    $until=[DateTime]::UtcNow.AddMilliseconds(1000)
    while([DateTime]::UtcNow -lt $until){
        Assert-Lease
        $journal=$LeaseReport+'.release.json'
        if(Test-Path -LiteralPath $journal){
            $phase=Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
            if($phase.pid -ne $leaseProcess.Id -or $phase.instanceId -ine $leaseReady.instanceId -or
                $phase.outputDevice -ine $leaseReady.outputDevice -or $phase.maxDurationMs -ne 5000){throw 'Lease release acknowledgement identity changed.'}
            if($phase.begun -and !$phase.completed -and (Get-Item -LiteralPath $journal).LastWriteTimeUtc -ge $script:releaseSignalTime){
                $script:releaseBegun=$true;$outcome.releaseBeginAcknowledged=$true
                $phase | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $folder 'release-begun.json')
                return
            }
        }
        Start-Sleep -Milliseconds 25
    }
    throw 'The lease did not acknowledge BEGIN before the cleanup deadline.'
}
function Complete-LeaseRelease {
    if($script:releaseCompleteSent){return}
    if(!$script:releaseBegun){throw 'Cannot complete a lease release that was not acknowledged.'}
    foreach($owned in @($presenter,$probe)){if($owned -and !$owned.Process.HasExited){throw 'Cannot complete release while an owned application is still running.'}}
    if($Interactive -and $presenter){
        if(!(Test-Path -LiteralPath $presenterReport)){throw 'No presenter input cleanup report exists; COMPLETE was not signaled.'}
        $cleanup=Get-Content -LiteralPath $presenterReport -Raw | ConvertFrom-Json
        if(!$cleanup.input -or $cleanup.input.active -or $cleanup.input.releaseIncomplete){throw 'Input cleanup is incomplete; COMPLETE was not signaled.'}
    }
    Assert-Lease
    [RegionVirtualProjectionFixture]::SignalRelease([string]$leaseReady.releaseProtocol.completeEvent)
    $script:releaseCompleteSent=$true;$outcome.releaseCompleteSent=$true
}
function Send-OwnedReleaseShortcut {
    if($script:releaseShortcutSent){return}
    if(!$ExerciseReleaseShortcut -or !$releaseBegun -or !$activeReady -or !$outcome.inputActiveObserved){throw 'The tagged release shortcut requires acknowledged BEGIN and observed interactive readiness.'}
    Assert-Lease
    if(Test-Path -LiteralPath $presenterReport){throw 'Presenter already reported completion before the release-shortcut test.'}
    $journal=$presenterReport+'.active.ready.json'
    $current=Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json
    if((Get-Item -LiteralPath $journal).LastWriteTimeUtc -ne $activeReadyWritten -or
        $current.pid -ne $presenter.Process.Id -or $current.hwndValue -ne $activeReady.hwndValue -or
        $current.sourcePid -ne $probe.Process.Id -or $current.sourceHwndValue -ne $sourceReady.hwndValue -or
        !$current.inputActive -or !$current.focused -or !$current.guardianReady){throw 'Interactive readiness identity changed before the release shortcut.'}
    $client=$sourceReady.client
    $outcome.syntheticReleaseShortcutAttempted=$true
    [RegionVirtualProjectionFixture]::ReleaseShortcut([int64]$sourceReady.hwndValue,$probe.Process,[int64]$probe.Created,$probeExe,
        [int64]$activeReady.hwndValue,$presenter.Process,[int64]$presenter.Created,$presenterExe,
        [int]$client.x,[int]$client.y,[int]($client.x+$client.width),[int]($client.y+$client.height))
    $script:releaseShortcutSent=$true;$outcome.syntheticReleaseShortcutSent=$true
}
function Start-Owned([string]$Executable,[string]$Arguments) {
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$Executable;$start.Arguments=$Arguments
    $start.UseShellExecute=$false;$start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    $process=[Diagnostics.Process]::Start($start)
    return @{Process=$process;Created=$process.StartTime.ToUniversalTime().ToFileTimeUtc();Out=$process.StandardOutput.ReadToEndAsync();Err=$process.StandardError.ReadToEndAsync()}
}
function Save-Output($Owned,[string]$Prefix) {
    if($Owned -and $Owned.Process.HasExited){
        if($Owned.Out.Wait(1000)){$Owned.Out.Result | Set-Content -LiteralPath ($Prefix+'.stdout.txt')}
        if($Owned.Err.Wait(1000)){$Owned.Err.Result | Set-Content -LiteralPath ($Prefix+'.stderr.txt')}
    }
}
function Contains-Rect($Outer,$Inner) {
    return [int64]$Inner.width -gt 0 -and [int64]$Inner.height -gt 0 -and
        [int64]$Inner.x -ge [int64]$Outer.x -and [int64]$Inner.y -ge [int64]$Outer.y -and
        ([int64]$Inner.x+[int64]$Inner.width) -le ([int64]$Outer.x+[int64]$Outer.width) -and
        ([int64]$Inner.y+[int64]$Inner.height) -le ([int64]$Outer.y+[int64]$Outer.height)
}
function Read-Inventory([string]$Suffix) {
    $text=(& $presenterExe --list-outputs) -join "`n"
    if($LASTEXITCODE -ne 0){throw 'DXGI output discovery failed.'}
    $text | Set-Content -LiteralPath (Join-Path $folder ('outputs-'+$Suffix+'.json'))
    return $text
}
function Read-Topology([string]$Suffix) {
    $text=(& $leaseExe --preflight) -join "`n"
    if($LASTEXITCODE -ne 0){throw 'Read-only physical display snapshot failed.'}
    $text | Set-Content -LiteralPath (Join-Path $folder ('topology-'+$Suffix+'.json'))
    $value=$text | ConvertFrom-Json
    if(!$value.success -or !$value.primaryAndWorkAreaObserved -or !$value.advancedColorObserved){throw 'Physical display snapshot is incomplete.'}
    return $text
}
function Physical-Outputs([string]$Snapshot,[bool]$Topology) {
    $value=$Snapshot | ConvertFrom-Json
    $items=@($value.outputs | Where-Object {$_.device -ine $leaseReady.outputDevice} | Sort-Object device | ForEach-Object {
        $record=[ordered]@{}
        foreach($property in $_.PSObject.Properties){
            # These are indexes into a newly queried mode array, not persistent
            # display identities or physical settings. All resolved data stays.
            if(!$Topology -or $property.Name -notin @('sourceModeInfoRaw','targetModeInfoRaw')){$record[$property.Name]=$property.Value}
        }
        [pscustomobject]$record
    })
    return ConvertTo-Json -InputObject $items -Depth 20 -Compress
}
try{
    $leasePin=[RegionProjectionProcessIdentity]::Retain($leaseProcess.Id,$leaseStarted.ToFileTimeUtc(),$leaseExe)
    Assert-Lease
    $inventoryBefore=Read-Inventory 'before'
    $topologyBefore=Read-Topology 'before'
    $inventory=$inventoryBefore | ConvertFrom-Json
    $topology=$topologyBefore | ConvertFrom-Json
    $virtual=@($inventory.outputs | Where-Object {$_.attached -and $_.device -ieq $leaseReady.outputDevice})
    $virtualTopology=@($topology.outputs | Where-Object {$_.device -ieq $leaseReady.outputDevice})
    if($virtual.Count -ne 1 -or $virtualTopology.Count -ne 1 -or $null -eq $virtualTopology[0].primary -or $virtualTopology[0].primary){
        throw 'Lease output is not one uniquely identified, non-primary attached output.'
    }
    $virtual=$virtual[0]
    if(!$virtual.colorSpaceKnown){throw 'The virtual source color space is unknown; conversion will not be guessed.'}
    $destination=[pscustomobject]@{x=900;y=100;width=1280;height=720}
    $destinations=@($inventory.outputs | Where-Object {$_.attached -and $_.device -ine $virtual.device -and (Contains-Rect $_.bounds $destination)})
    if($destinations.Count -ne 1){throw 'Requested physical projection region is not inside one separate output.'}
    $destinationTopology=@($topology.outputs | Where-Object {$_.device -ieq $destinations[0].device})
    if($destinationTopology.Count -ne 1 -or !$destinationTopology[0].primary){throw 'Projection destination is not the known physical primary output.'}
    $rectangle='{0},{1},{2},{3}' -f $virtual.bounds.x,$virtual.bounds.y,$virtual.bounds.width,$virtual.bounds.height
    Assert-Lease
    $returnForeground=[RegionVirtualProjectionFixture]::GetForegroundWindow().ToInt64()
    $probe=Start-Owned $probeExe ('--exclusive --output-device "'+$virtual.device+'" --rect '+$rectangle+' --seconds '+($Seconds+15)+' --report "'+$probeReport+'"')
    $until=[DateTime]::UtcNow.AddSeconds(10)
    while(!(Test-Path -LiteralPath ($probeReport+'.ready.json'))){
        Assert-Lease
        if($probe.Process.HasExited -or [DateTime]::UtcNow -gt $until){
            Save-Output $probe ($probeReport+'.process')
            $detail=if(Test-Path -LiteralPath $probeReport){Get-Content -LiteralPath $probeReport -Raw}else{'No source report.'}
            throw ('Owned exclusive source did not become ready: '+$detail)
        }
        Start-Sleep -Milliseconds 100
    }
    $sourceReady=Get-Content -LiteralPath ($probeReport+'.ready.json') -Raw | ConvertFrom-Json
    if($sourceReady.pid -ne $probe.Process.Id -or !$sourceReady.ready -or !$sourceReady.success -or
        !$sourceReady.requestedExclusive -or !$sourceReady.exclusive -or $sourceReady.outputDevice -ine $virtual.device -or
        $sourceReady.regionHookLoaded -or !(Contains-Rect $virtual.bounds $sourceReady.client)){
        throw 'Owned source identity, exclusive state, output, client crop, or DisplayWeave isolation check failed.'
    }
    $outcome.exclusiveApiReported=$true
    $crop='{0},{1},{2},{3}' -f $sourceReady.client.x,$sourceReady.client.y,$sourceReady.client.width,$sourceReady.client.height
    $arguments='--source-device "'+$virtual.device+'" --fixture-source-rect '+$crop+' --destination 900,100,1280,720 --seconds '+$Seconds+' --report "'+$presenterReport+'" --verify-fixture-colors'
    if($Interactive){$arguments+=' --interactive-source-hwnd '+$sourceReady.hwndValue+' --interactive-source-pid '+$probe.Process.Id+' --interactive-entry-center'}
    if($Interactive -and $returnForeground -ne 0){$arguments+=' --interactive-return-hwnd '+$returnForeground}
    if($ExerciseGuardianRecovery){$arguments+=' --fixture-input-stall-ms 3500'}
    # Never infer HDR from the physical destination; only the selected source's
    # actual color-space observation can opt this owned fixture into conversion.
    if(!$virtual.sdr){$arguments+=' --allow-sdr-conversion'}
    $presenter=Start-Owned $presenterExe $arguments
    $until=[DateTime]::UtcNow.AddSeconds($Seconds+12)
    while(!$presenter.Process.WaitForExit(200)){
        Assert-Lease
        if($Interactive -and !$activeReady -and (Test-Path -LiteralPath ($presenterReport+'.active.ready.json'))){
            $activeReady=Get-Content -LiteralPath ($presenterReport+'.active.ready.json') -Raw | ConvertFrom-Json
            if($activeReady.pid -ne $presenter.Process.Id -or $activeReady.sourcePid -ne $probe.Process.Id -or
                $activeReady.sourceHwndValue -ne $sourceReady.hwndValue -or !$activeReady.inputActive -or !$activeReady.focused -or !$activeReady.guardianReady){
                throw 'Interactive readiness did not establish the owned source, focus, confinement, and guardian.'
            }
            $activeReadyWritten=(Get-Item -LiteralPath ($presenterReport+'.active.ready.json')).LastWriteTimeUtc
            $outcome.inputActiveObserved=$true
            if($ExerciseGuardianRecovery){
                if($activeReady.fixtureInputStallMs -ne 3500){throw 'Presenter did not acknowledge the requested bounded guardian fixture stall.'}
                Begin-LeaseRelease
            }
            if($ExerciseSyntheticInput){[RegionVirtualProjectionFixture]::Exercise([int64]$sourceReady.hwndValue,$probe.Process.Id);$inputExercised=$true}
        }
        # The source writes an atomic live snapshot once per second. Retain
        # evidence observed while this presenter is alive, after input entry
        # where requested. A later deliberate return-focus action may make DXGI
        # leave exclusive mode; that cleanup transition is not a test failure.
        if((!$Interactive -or $activeReady) -and (Test-Path -LiteralPath $probeReport)){
            $sampleWritten=(Get-Item -LiteralPath $probeReport).LastWriteTimeUtc
            $minimumSampleTime=if($Interactive){$activeReadyWritten}else{$presenter.Process.StartTime.ToUniversalTime()}
            if($sampleWritten -ge $minimumSampleTime){
                $sampleText=Get-Content -LiteralPath $probeReport -Raw
                $sample=$sampleText | ConvertFrom-Json
                if($sample.pid -ne $probe.Process.Id -or $sample.hwndValue -ne $sourceReady.hwndValue -or $sample.regionHookLoaded){
                    throw 'Live source snapshot identity or DisplayWeave isolation changed.'
                }
                if(!$sample.finished -and $sample.success -and $sample.frame -gt $lastObservedFrame -and
                    $sample.current.fullscreenHr -eq 0 -and $sample.current.exclusive -and
                    $sample.current.outputDevice -ieq $virtual.device -and
                    (!$Interactive -or $sample.current.foregroundIsSource) -and !$presenter.Process.HasExited){
                    $lastObservedFrame=$sample.frame
                    $outcome.exclusiveDuringProjectionObserved=$true
                    $outcome.exclusiveDuringProjectionSamples++
                    if($Interactive){$outcome.exclusiveDuringInteractionObserved=$true}
                    # Save exactly the parsed sample rather than recopying a
                    # path which the source could atomically replace meanwhile.
                    $sampleText | Set-Content -LiteralPath $outcome.activeExclusiveEvidence
                }
            }
        }
        if($probe.Process.HasExited){throw 'Exclusive source ended before projection completed.'}
        if($Interactive -and !$releaseBegun -and [DateTime]::UtcNow -ge $presenter.Process.StartTime.ToUniversalTime().AddSeconds($Seconds-2)){
            if(!$activeReady){throw 'Interactive entry was not ready before the controlled release window.'}
            Begin-LeaseRelease
            if($ExerciseReleaseShortcut){Send-OwnedReleaseShortcut}
        }
        if([DateTime]::UtcNow -gt $until){throw 'Presenter exceeded the bounded test duration.'}
    }
    Save-Output $presenter ($presenterReport+'.process')
    if($presenter.Process.ExitCode -ne 0){throw ('Virtual-output capture/presentation failed: '+$presenter.Out.Result+$presenter.Err.Result)}
    $result=Get-Content -LiteralPath $presenterReport -Raw | ConvertFrom-Json
    if(!$result.success -or !$result.fixtureVerified -or $result.presentedFrames -lt 2 -or $result.failedPresents -ne 0){
        throw 'Projected fixture pixels or frame presentation did not pass verification.'
    }
    $outcome.fixtureColorsVerified=$true
    if(!$outcome.exclusiveDuringProjectionObserved -or ($Interactive -and !$outcome.exclusiveDuringInteractionObserved)){
        throw 'No fresh exclusive source snapshot was observed during active projection.'
    }
    if($Interactive -and (!$outcome.inputActiveObserved -or !$result.input.entered -or $result.input.active -or $result.input.releaseIncomplete)){
        throw 'Interactive entry or release did not complete.'
    }
    if($ExerciseGuardianRecovery){
        $outcome.guardianRecoveryVerified=$result.fixtureInputStall.requested -and $result.fixtureInputStall.completed -and
            $result.fixtureInputStall.requestedMs -eq 3500 -and $result.input.guardianRecovered -and $result.input.guardianFocusRestored -and
            $result.input.focusRestored -and $result.input.clipRestored -and $result.input.cursorRestored -and !$result.input.releaseIncomplete
        if(!$outcome.guardianRecoveryVerified){throw 'The bounded presenter stall did not verify guardian focus, cursor and clip restoration.'}
    }
    if($ExerciseReleaseShortcut){
        $outcome.releaseShortcutObservedReason=[string]$result.input.reason
        $outcome.syntheticReleaseShortcutVerified=$releaseShortcutSent -and $result.input.reason -ceq 'release_shortcut' -and
            $result.input.focusRequestAttempted -and $result.input.focusRequestAccepted -and $result.input.focusRestored -and
            $result.input.clipRestored -and $result.input.cursorRestored -and !$result.input.releaseIncomplete
        if(!$outcome.syntheticReleaseShortcutVerified){throw 'The tagged Windows release shortcut did not verify its actual handler and complete owned focus/cursor/clip restoration.'}
    }
    Begin-LeaseRelease
    [RegionVirtualProjectionFixture]::Close([int64]$sourceReady.hwndValue,$probe.Process.Id)
    if(!$probe.Process.WaitForExit(2000)){throw 'Owned source did not leave exclusive mode and close normally.'}
    Save-Output $probe ($probeReport+'.process')
    $source=Get-Content -LiteralPath $probeReport -Raw | ConvertFrom-Json
    if($ExerciseSyntheticInput){
        $outcome.syntheticInputVerified=$inputExercised -and $source.syntheticFixtureInput.keyDown -ge 1 -and $source.syntheticFixtureInput.keyUp -ge 1 -and $source.syntheticFixtureInput.mouseMoves -ge 1
        if(!$outcome.syntheticInputVerified){throw 'The owned probe did not observe the explicitly tagged synthetic input.'}
    }
    $outcome.regionHookLoaded=[bool]$source.regionHookLoaded
    if($probe.Process.ExitCode -ne 0 -or !$source.success -or $source.failedPresents -ne 0 -or $source.regionHookLoaded -or
        $source.leaveFullscreenHr -ne 0 -or $source.restored.exclusive){
        throw 'Source did not render cleanly or restore its own windowed state after projection release.'
    }
    $outcome.sourceRestored=$true
    Complete-LeaseRelease
}catch{
    $outcome.failure=$_.Exception.Message
}finally{
    # Before any fallback close, request the same private release transition.
    # Failure to acknowledge remains a failure; cleanup must still close only
    # our processes, and the native lease enforces its own five-second limit.
    if(!$releaseBegun){try{Begin-LeaseRelease}catch{$cleanupErrors.Add($_.Exception.Message)}}
    foreach($owned in @($presenter,$probe)){
        if(!$owned){continue}
        try{
            if(!$owned.Process.HasExited){
                if($owned -eq $presenter){
                    $presenterWindow=[RegionVirtualProjectionFixture]::PresenterWindow($owned.Process.Id)
                    if($presenterWindow){try{[RegionVirtualProjectionFixture]::Close($presenterWindow,$owned.Process.Id)}catch{}}
                }elseif($sourceReady -and $sourceReady.pid -eq $owned.Process.Id){
                    try{[RegionVirtualProjectionFixture]::Close([int64]$sourceReady.hwndValue,$owned.Process.Id)}catch{}
                }
                if(!$owned.Process.WaitForExit(2000)){
                    # Only process objects returned by this script's own Start.
                    # The display lease receives only its validated release protocol.
                    $owned.Process.Kill()
                    if(!$owned.Process.WaitForExit(5000)){throw 'An owned fixture process did not terminate.'}
                }
            }
        }catch{$cleanupErrors.Add($_.Exception.Message)}
    }
    Save-Output $presenter ($presenterReport+'.process')
    Save-Output $probe ($probeReport+'.process')
    try{Complete-LeaseRelease}catch{$cleanupErrors.Add($_.Exception.Message)}
    # An incomplete focus release must remain a failed test. Still observe the
    # exact owned lease through its bounded timeout so the report distinguishes
    # failed release from successful eventual physical-display restoration.
    try{
        if(!$leasePin -or !$leasePin.WaitForExit(15000)){throw 'The lease did not finish removal and restoration within the bounded cleanup observation.'}
        $outcome.leaseExited=$true
        $outcome.leaseExitCode=$leasePin.ExitCode;$outcome.leaseExitCodeObserved=$true
        try{$outcome.managedLeaseExitCode=$leaseProcess.ExitCode}catch{}
        if(!(Test-Path -LiteralPath $LeaseReport)){throw 'No final native lease report exists.'}
        $releaseFinal=Get-Content -LiteralPath $LeaseReport -Raw | ConvertFrom-Json
        $outcome.releaseCompleted=[bool]$releaseFinal.releaseCompleted
        $outcome.releaseTransitionObserved=[bool]$releaseFinal.releaseTransitionObserved
        $outcome.originalTopologyObservedAfterClose=[bool]$releaseFinal.originalTopologyObservedAfterClose
        $outcome.physicalRestoredAfterRelease=[bool]$releaseFinal.physicalRestoredAfterRelease
        # The final lease schema carries the unique instance/output identity;
        # its PID/lifetime was held and verified through the Process object.
        if($releaseFinal.instanceId -ine $leaseReady.instanceId -or $releaseFinal.outputDevice -ine $leaseReady.outputDevice){throw 'Final lease report identity changed.'}
        $inventoryAfter=Read-Inventory 'after'
        $topologyAfter=Read-Topology 'after'
        $afterOutputs=$inventoryAfter | ConvertFrom-Json
        $outcome.ownedVirtualOutputRemoved=@($afterOutputs.outputs | Where-Object {$_.device -ieq $leaseReady.outputDevice}).Count -eq 0
        $outcome.remainingPhysicalInventoryMatches=$inventoryBefore -and ((Physical-Outputs $inventoryBefore $false) -ceq (Physical-Outputs $inventoryAfter $false))
        $outcome.remainingPhysicalTopologyMatches=$topologyBefore -and ((Physical-Outputs $topologyBefore $true) -ceq (Physical-Outputs $topologyAfter $true))
        if(!$outcome.ownedVirtualOutputRemoved -or !$outcome.remainingPhysicalInventoryMatches -or !$outcome.remainingPhysicalTopologyMatches){throw 'The owned output was not removed or the remaining physical display state differs.'}
        if(!$outcome.leaseExitCodeObserved -or $outcome.leaseExitCode -ne 0 -or !$releaseFinal.success -or !$outcome.releaseCompleted -or
            !$outcome.originalTopologyObservedAfterClose -or !$outcome.physicalRestoredAfterRelease){
            throw 'Native controlled release failed; eventual removal and physical restoration observations were retained.'
        }
    }catch{$cleanupErrors.Add($_.Exception.Message)}
    if($leasePin){$leasePin.Dispose();$leasePin=$null}
    $outcome.cleanupErrors=$cleanupErrors.ToArray()
    $outcome.success=!$outcome.failure -and $cleanupErrors.Count -eq 0 -and $outcome.exclusiveApiReported -and
        $outcome.exclusiveDuringProjectionObserved -and $outcome.fixtureColorsVerified -and $outcome.sourceRestored -and
        $outcome.releaseCompleted -and $outcome.leaseExited -and $outcome.physicalRestoredAfterRelease -and $outcome.ownedVirtualOutputRemoved -and
        $outcome.remainingPhysicalInventoryMatches -and $outcome.remainingPhysicalTopologyMatches
    $outcome | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $folder 'result.json')
}
if(!$outcome.success){throw ('Virtual-output projection did not pass. Reports: '+$folder+'. '+$outcome.failure+' '+($cleanupErrors -join '; '))}
Write-Output ('PASS owned virtual-output projection: '+$result.presentedFrames+' frames, verified colors, restored source, completed lease release and removal, original physical displays restored. Reports: '+$folder)
