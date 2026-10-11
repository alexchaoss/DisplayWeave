#requires -Version 5.1
#requires -RunAsAdministrator
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$DriverPackageDirectory,
    [string]$BuildDirectory=(Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'),
    [ValidateRange(15,120)][int]$Seconds=45,
    [switch]$UseExistingPinnedPackage,
    # Absolute, not-yet-existing folder for this run's journals (the prototype
    # worker passes its session folder; the default stays under the checkout).
    [string]$ReportDirectory,
    # The user explicitly selected their own running app for this session, so
    # the owned-fixture guard against a running Diablo IV does not apply.
    [switch]$SelectedAppSession
)
$ErrorActionPreference='Stop'
if($ReportDirectory){
    if(![IO.Path]::IsPathRooted($ReportDirectory)){throw '-ReportDirectory must be an absolute path.'}
    $folder=[IO.Path]::GetFullPath($ReportDirectory)
    if(Test-Path -LiteralPath $folder){throw 'The report directory must not already exist.'}
}else{$folder=Join-Path (Split-Path -Parent $PSScriptRoot) ('test-results\virtual-display-'+(Get-Date -Format 'yyyyMMdd-HHmmss-fff'))}
New-Item -ItemType Directory -Path $folder | Out-Null
trap {
    [ordered]@{error=$_.Exception.Message;location=$_.InvocationInfo.PositionMessage} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'fatal.json')
    throw $_
}
$BuildDirectory=(Resolve-Path -LiteralPath $BuildDirectory).Path
$DriverPackageDirectory=(Resolve-Path -LiteralPath $DriverPackageDirectory).Path
if(Get-Process -Name DisplayWeave -ErrorAction SilentlyContinue){throw 'Exit DisplayWeave before starting the isolated virtual display fixture.'}
if(!$SelectedAppSession -and (Get-Process -Name 'Diablo IV' -ErrorAction SilentlyContinue)){throw 'Close Diablo IV before this owned display fixture changes the Windows display topology.'}
$leaseExe=Join-Path $BuildDirectory 'projection-x64\RegionDisplayLease.exe'
if(!(Test-Path -LiteralPath $leaseExe -PathType Leaf)){throw 'Build the projection tools first.'}
# Exact unmodified upstream release 25.5.2, Signed-Driver-v24.12.24-x64.zip.
# These hashes are provenance pins, not a replacement for Windows catalog trust.
$expected=@{
    'MttVDD.inf'='550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D'
    'MttVDD.dll'='C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05'
    'mttvdd.cat'='08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6'
}
foreach($name in $expected.Keys){
    $path=Join-Path $DriverPackageDirectory $name
    if((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expected[$name]){throw "Upstream fixture file differs: $name"}
}
foreach($name in @('MttVDD.dll','mttvdd.cat')){
    $signature=Get-AuthenticodeSignature -LiteralPath (Join-Path $DriverPackageDirectory $name)
    if($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '^CN=SignPath Foundation,'){
        throw "Upstream fixture signature is not the expected valid publisher: $name"
    }
}
$configuration='C:\VirtualDisplayDriver'
if(Test-Path -LiteralPath $configuration){throw 'An existing VDD configuration must not be changed by this fixture.'}
if(Test-Path -LiteralPath 'HKLM:\SOFTWARE\MikeTheTech\VirtualDisplayDriver'){throw 'Existing vendor registry configuration must not be changed by this fixture.'}
$existingDevices=@(Get-PnpDevice -Class Display | Where-Object {$_.InstanceId -match 'MttVDD' -or $_.FriendlyName -eq 'Virtual Display Driver'})
if($existingDevices.Count){throw 'A VDD device already exists; this isolated fixture will not reuse or modify it.'}
$preflight=& $leaseExe --preflight
if($LASTEXITCODE -ne 0){throw 'Physical display preflight failed; no driver was staged.'}
$preflight | Set-Content -LiteralPath (Join-Path $folder 'physical-before.json')
$physical=$preflight | ConvertFrom-Json
if(!$physical.success -or !$physical.primaryAndWorkAreaObserved -or !$physical.advancedColorObserved){
    throw 'Cannot fully observe physical display settings; no driver was staged.'
}
$before=@(Get-WindowsDriver -Online -All | Where-Object {$_.ClassName -eq 'Display'})
$beforeNames=@($before | ForEach-Object {$_.Driver})
$existingPackages=@($before | Where-Object {(Split-Path -Leaf $_.OriginalFileName) -ieq 'MttVDD.inf'})
$reused=$null
function Assert-PinnedStorePackage($Package) {
    $storeDirectory=Split-Path -Parent $Package.OriginalFileName
    foreach($name in $expected.Keys){
        if((Get-FileHash -LiteralPath (Join-Path $storeDirectory $name) -Algorithm SHA256).Hash -ne $expected[$name]){throw "Existing driver-store package differs from the pinned fixture: $name"}
    }
}
if($existingPackages.Count){
    if(!$UseExistingPinnedPackage){throw 'A driver package is already staged. Use -UseExistingPinnedPackage only to verify and preserve that exact package.'}
    if($existingPackages.Count -ne 1){throw 'Existing driver package identity is ambiguous.'}
    Assert-PinnedStorePackage $existingPackages[0]
    $reused=$existingPackages[0]
}
$installed=$null; $lease=$null; $report=Join-Path $folder 'lease.json'; $configurationCreated=$false; $configurationHash=$null
$cleanupErrors=New-Object 'System.Collections.Generic.List[string]'
$outcome=[ordered]@{success=$false;report=$report;deviceRemoved=$false;driverRemoved=$false;existingDriverPreserved=$false;reusedDriverPackage=$(if($reused){$reused.Driver}else{$null});configurationRemoved=$false;physicalRestored=$false;controlledRelease=$false;releaseCompleted=$false;releaseTransitionObserved=$false;physicalPreservedSinceReady=$null;physicalRestoredAfterRelease=$false;originalTopologyObservedAfterClose=$false;failure=$null;cleanupErrors=@()}
function Find-OwnedPackage {
    $candidates=@(Get-WindowsDriver -Online -All | Where-Object {
        $_.ClassName -eq 'Display' -and $_.Driver -notin $beforeNames -and (Split-Path -Leaf $_.OriginalFileName) -ieq 'MttVDD.inf'
    })
    if($candidates.Count -gt 1){throw 'New driver package ownership is ambiguous.'}
    if($candidates.Count -eq 1){
        if((Get-FileHash -LiteralPath $candidates[0].OriginalFileName -Algorithm SHA256).Hash -ne $expected['MttVDD.inf']){throw 'New driver package does not match the pinned fixture INF.'}
        return $candidates[0].Driver
    }
    return $null
}
try{
    New-Item -ItemType Directory -Path $configuration -ErrorAction Stop | Out-Null
    $configurationCreated=$true
    @'
<?xml version="1.0" encoding="utf-8"?>
<vdd_settings>
  <monitors><count>1</count></monitors>
  <gpu><friendlyname>default</friendlyname></gpu>
  <global><g_refresh_rate>60</g_refresh_rate></global>
  <resolutions><resolution><width>1280</width><height>720</height><refresh_rate>60</refresh_rate></resolution></resolutions>
  <options><CustomEdid>false</CustomEdid><PreventSpoof>false</PreventSpoof><EdidCeaOverride>false</EdidCeaOverride><HardwareCursor>true</HardwareCursor><SDR10bit>false</SDR10bit><HDRPlus>false</HDRPlus><logging>false</logging><debuglogging>false</debuglogging></options>
</vdd_settings>
'@ | Set-Content -LiteralPath (Join-Path $configuration 'vdd_settings.xml') -Encoding UTF8
    $configurationHash=(Get-FileHash -LiteralPath (Join-Path $configuration 'vdd_settings.xml') -Algorithm SHA256).Hash
    if(!$reused){
        & "$env:WINDIR\System32\pnputil.exe" /add-driver (Join-Path $DriverPackageDirectory 'MttVDD.inf') 2>&1 | Set-Content -LiteralPath (Join-Path $folder 'driver-stage.txt')
        if($LASTEXITCODE -ne 0){throw "Windows rejected the signed fixture driver (exit $LASTEXITCODE)."}
        $installed=Find-OwnedPackage
        if(!$installed){throw 'Cannot establish ownership of exactly one newly staged fixture driver package.'}
        $installed | Set-Content -LiteralPath (Join-Path $folder 'owned-driver.txt')
    }else{$reused.Driver | Set-Content -LiteralPath (Join-Path $folder 'preserved-existing-driver.txt')}
    $start=New-Object Diagnostics.ProcessStartInfo
    $start.FileName=$leaseExe; $start.Arguments='--vdd-fixture --seconds '+$Seconds+' --report "'+$report+'"'
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    $lease=[Diagnostics.Process]::Start($start)
    $stdout=$lease.StandardOutput.ReadToEndAsync();$stderr=$lease.StandardError.ReadToEndAsync()
    [ordered]@{leasePid=$lease.Id;report=$report;folder=$folder;ownedDriver=$installed;reusedDriver=$outcome.reusedDriverPackage} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'session.json')
    if(!$lease.WaitForExit(($Seconds+20)*1000)){throw 'The temporary display exceeded its bounded lifetime.'}
    $stdout.Result | Set-Content -LiteralPath (Join-Path $folder 'lease.stdout.txt')
    $stderr.Result | Set-Content -LiteralPath (Join-Path $folder 'lease.stderr.txt')
    $state=Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
    $outcome.controlledRelease=[bool]$state.releaseCompleted
    $outcome.releaseCompleted=[bool]$state.releaseCompleted
    $outcome.releaseTransitionObserved=[bool]$state.releaseTransitionObserved
    $outcome.physicalPreservedSinceReady=$state.physicalPreservedSinceReady
    $outcome.physicalRestoredAfterRelease=[bool]$state.physicalRestoredAfterRelease
    $outcome.originalTopologyObservedAfterClose=[bool]$state.originalTopologyObservedAfterClose
    if($lease.ExitCode -ne 0){throw ('Temporary display fixture failed: '+$stdout.Result+$stderr.Result)}
    # A coordinated source fullscreen exit may change path refresh during its
    # acknowledged release window. Native success still requires the original
    # physical topology after device removal; do not label that transition as
    # continuous preservation or accept an unannounced change.
    $preservationPassed=if($state.releaseCompleted){$state.physicalRestoredAfterRelease}else{$state.physicalPreserved -and $state.physicalPreservedSinceReady}
    if(!$state.success -or !$state.ownershipVerified -or !$preservationPassed -or !$state.originalTopologyObservedAfterClose){throw 'Display preservation or controlled release/restoration did not pass.'}
    $outcome.success=$true
}catch{
    $outcome.failure=$_.Exception.Message
}finally{
    try{if($lease -and !$lease.HasExited){$lease.Kill();if(!$lease.WaitForExit(5000)){throw 'Lease process did not exit.'}}}catch{$cleanupErrors.Add($_.Exception.Message)}
    # SwDeviceClose/process exit retires the handle-bound node. Only remove the
    # exact instance created by this fixture; no global display reset is used.
    try{
        $instance=$null
        foreach($journal in @(($report+'.created.json'),($report+'.ready.json'),$report)){
            if(Test-Path -LiteralPath $journal){
                try{$state=Get-Content -LiteralPath $journal -Raw | ConvertFrom-Json}catch{$cleanupErrors.Add('Cannot read ownership journal: '+$journal);continue}
                if($state.instanceId -like 'SWD\DisplayWeaveProjection\Fixture-*'){$instance=$state.instanceId;break}
            }
        }
        if($instance){
            & "$env:WINDIR\System32\pnputil.exe" /remove-device $instance 2>&1 | Set-Content -LiteralPath (Join-Path $folder 'device-remove.txt')
            $remaining=@(Get-PnpDevice | Where-Object {$_.InstanceId -ieq $instance})
            $outcome.deviceRemoved=$remaining.Count -eq 0
            if(!$outcome.deviceRemoved){throw 'The owned PnP device remains registered.'}
        }elseif(!$lease){$outcome.deviceRemoved=$true}
        else{throw 'No durable device ownership journal is available; inspect the fixture log.'}
    }catch{$cleanupErrors.Add($_.Exception.Message)}
    try{
        if($reused){
            $existingAfter=@(Get-WindowsDriver -Online -All | Where-Object {$_.Driver -ieq $reused.Driver})
            if($existingAfter.Count -ne 1){throw 'The pre-existing driver package is no longer registered.'}
            Assert-PinnedStorePackage $existingAfter[0]
            $outcome.existingDriverPreserved=$true
        }else{
          if(!$installed){$installed=Find-OwnedPackage}
          if($installed -and $installed -match '^oem\d+\.inf$' -and $installed -notin $beforeNames){
            & "$env:WINDIR\System32\pnputil.exe" /delete-driver $installed 2>&1 | Set-Content -LiteralPath (Join-Path $folder 'driver-remove.txt')
            $remaining=@(Get-WindowsDriver -Online -All | Where-Object {$_.Driver -ieq $installed})
            $outcome.driverRemoved=$remaining.Count -eq 0
            if(!$outcome.driverRemoved){throw 'The owned driver package remains staged.'}
          }elseif(!$installed){$outcome.driverRemoved=$true}
          else{throw 'Refusing removal of an unowned driver package.'}
        }
    }catch{$cleanupErrors.Add($_.Exception.Message)}
    try{
        if($configurationCreated){
            # Explicitly named, previously absent folder. Remove only the one file
            # created here; leave unexpected vendor/user files for review.
            $ownedFile=Join-Path $configuration 'vdd_settings.xml'
            if(Test-Path -LiteralPath $ownedFile){
                if(!$configurationHash -or (Get-FileHash -LiteralPath $ownedFile -Algorithm SHA256).Hash -ne $configurationHash){throw 'Temporary configuration changed; preserving it for review.'}
                Remove-Item -LiteralPath $ownedFile -Force
            }
            if(@(Get-ChildItem -LiteralPath $configuration -Force).Count -eq 0){Remove-Item -LiteralPath $configuration}
            $outcome.configurationRemoved=!(Test-Path -LiteralPath $configuration)
            if(!$outcome.configurationRemoved){throw 'Unexpected files remain in the temporary driver configuration folder.'}
        }else{$outcome.configurationRemoved=$true}
    }catch{$cleanupErrors.Add($_.Exception.Message)}
    try{
        $after=& $leaseExe --preflight
        if($LASTEXITCODE -ne 0){throw 'Final physical display preflight failed.'}
        $after | Set-Content -LiteralPath (Join-Path $folder 'physical-after.json')
        $outcome.physicalRestored=($preflight -join "`n") -ceq ($after -join "`n")
        if(!$outcome.physicalRestored){throw 'Physical display state differs after cleanup.'}
    }catch{$cleanupErrors.Add($_.Exception.Message)}
    $outcome.cleanupErrors=@($cleanupErrors)
    $outcome.success=$outcome.success -and $outcome.deviceRemoved -and ($outcome.driverRemoved -or $outcome.existingDriverPreserved) -and
        $outcome.configurationRemoved -and $outcome.physicalRestored -and $cleanupErrors.Count -eq 0
    $outcome | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'result.json')
}
if(!$outcome.success -or !$outcome.deviceRemoved -or !($outcome.driverRemoved -or $outcome.existingDriverPreserved) -or !$outcome.configurationRemoved -or !$outcome.physicalRestored -or $cleanupErrors.Count){throw ('Virtual-display fixture incomplete. Review '+$folder+': '+$outcome.failure+' '+($cleanupErrors -join '; '))}
Write-Output ('PASS temporary display lifecycle and cleanup. '+$folder)
