# Runs the prototype's elevated-display bootstrap without elevation, against a
# temporary root standing in for Program Files and a stand-in fixture script.
# Nothing is installed and no display, driver or window is touched.
param([Parameter(Mandatory = $true)][string]$Executable)
$ErrorActionPreference = 'Stop'
$results = New-Object Collections.Generic.List[string]
$failed = 0
function Check([bool]$Condition, [string]$Name) { $script:results.Add($(if ($Condition) { "PASS $Name" } else { $script:failed++; "FAIL $Name" })) }

[void][Reflection.Assembly]::LoadFile((Resolve-Path -LiteralPath $Executable).Path)
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('DisplayWeavePrototype-elevation-' + [Guid]::NewGuid().ToString('N'))
# Quotes in the install path must stay inside the bootstrap's literals.
$source = Join-Path $temporary ("app O'Neil " + [char]0x2019 + 'x')
$root = Join-Path $temporary 'protected sessions'
try {
    foreach ($name in @('projection-x64\RegionDisplayLease.exe', 'driver\MttVDD.inf', 'driver\MttVDD.dll', 'driver\mttvdd.cat')) {
        New-Item -ItemType Directory -Path (Split-Path -Parent (Join-Path $source $name)) -Force | Out-Null
        Set-Content -LiteralPath (Join-Path $source $name) -Value "stand-in $name"
    }
    Set-Content -LiteralPath (Join-Path $source 'test-virtual-display.ps1') -Value @'
param($DriverPackageDirectory, $BuildDirectory, $Seconds, [switch]$UseExistingPinnedPackage, [switch]$SelectedAppSession, $ReportDirectory)
New-Item -ItemType Directory -Path $ReportDirectory | Out-Null
[ordered]@{ script = $PSCommandPath; driver = $DriverPackageDirectory; build = $BuildDirectory; seconds = $Seconds; pinned = [bool]$UseExistingPinnedPackage
    selected = [bool]$SelectedAppSession; driverFiles = @(Get-ChildItem -LiteralPath $DriverPackageDirectory | Sort-Object Name | ForEach-Object { $_.Name }) } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $ReportDirectory 'args.json')
'@
    $verified = New-Object 'Collections.Generic.Dictionary[string,string]'
    foreach ($name in @('test-virtual-display.ps1', 'projection-x64\RegionDisplayLease.exe')) {
        $verified[$name] = (Get-FileHash -LiteralPath (Join-Path $source $name) -Algorithm SHA256).Hash
    }
    $old = New-Item -ItemType Directory -Path (Join-Path $root 'old-session') -Force
    $old.CreationTimeUtc = [DateTime]::UtcNow.AddDays(-8)
    $recent = New-Item -ItemType Directory -Path (Join-Path $root 'recent-session') -Force
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    function Invoke-Bootstrap([string]$Session, $Hashes) {
        $arguments = [DisplayWeave.Prototype.ElevatedFixture]::Arguments($source, $root, $Session, 45, $Hashes)
        return (Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle Hidden -Wait -PassThru).ExitCode
    }

    $code = Invoke-Bootstrap 'good' $verified
    $report = Join-Path $root 'good\display\args.json'
    $ran = Test-Path -LiteralPath $report
    Check ($code -eq 0 -and $ran) 'verified copies run the fixture'
    if ($ran) {
        $seen = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
        $tools = Join-Path $root 'good\tools'
        Check ($seen.script -eq (Join-Path $tools 'test-virtual-display.ps1') -and $seen.build -eq $tools -and $seen.driver -eq (Join-Path $tools 'driver')) 'fixture, lease and driver run from the protected copy'
        Check (($seen.driverFiles -join ',') -eq 'mttvdd.cat,MttVDD.dll,MttVDD.inf' -and $seen.seconds -eq 45 -and $seen.pinned -and $seen.selected) 'fixture receives the session arguments'
    }
    Check (-not (Test-Path -LiteralPath $old.FullName) -and (Test-Path -LiteralPath $recent.FullName)) 'only week-old protected sessions are removed'

    $tampered = New-Object 'Collections.Generic.Dictionary[string,string]' -ArgumentList (, $verified)
    $tampered['projection-x64\RegionDisplayLease.exe'] = '0' * 64
    $code = Invoke-Bootstrap 'tampered' $tampered
    $fatal = Join-Path $root 'tampered\display\fatal.json'
    Check ($code -ne 0 -and (Test-Path -LiteralPath $fatal) -and -not (Test-Path -LiteralPath (Join-Path $root 'tampered\display\args.json'))) 'a changed lease stops the bootstrap before the fixture'
    if (Test-Path -LiteralPath $fatal) { Check ((Get-Content -LiteralPath $fatal -Raw | ConvertFrom-Json).error -like '*RegionDisplayLease.exe does not match*') 'the mismatch is reported to the worker' }
}
finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force } }

$results.Add("$($results.Where({ $_ -like 'PASS *' }).Count) passed; $failed failed.")
$results | Write-Output
if ($failed) { throw 'Prototype elevation bootstrap checks failed.' }
exit 0
