# Isolated checks for install-prototype.ps1 against a packaged ZIP. Uses a
# temporary install root, no shortcuts, no launch and no network: the driver
# comes from -DriverPackageDirectory (real pinned files when available).
param([string]$Package, [string]$DriverPackageDirectory, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repository 'src\DisplayWeave.Prototype\version.txt') -Raw).Trim()
if (-not $Package) { $Package = Join-Path $repository "release\DisplayWeavePrototype-$version.zip" }
if (-not $DriverPackageDirectory) { $DriverPackageDirectory = Join-Path $repository '.tools\research\vdd-signed-24.12.24\package' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository 'test-results' }
$root = Join-Path $OutputDirectory ('prototype-installer-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
$results = New-Object Collections.Generic.List[string]
$failed = 0
function Check([bool]$Condition, [string]$Name) { $script:results.Add($(if ($Condition) { "PASS $Name" } else { $script:failed++; "FAIL $Name" })) }
function Install([string]$From, [string]$Driver, [string]$Target) {
    try {
        & powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $From 'install-prototype.ps1') -InstallRoot $Target -DriverPackageDirectory $Driver -NoLaunch -NoShortcut *>&1 | Out-Null
        return $LASTEXITCODE -eq 0
    } catch { return $false }
}

$extracted = Join-Path $root 'package'
Expand-Archive -LiteralPath $Package -DestinationPath $extracted
$fakeDriver = Join-Path $root 'fake-driver'
New-Item -ItemType Directory -Path $fakeDriver | Out-Null
foreach ($name in @('MttVDD.inf', 'MttVDD.dll', 'mttvdd.cat')) { Set-Content -LiteralPath (Join-Path $fakeDriver $name) -Value 'not the pinned file' }

$rejected = Join-Path $root 'install-rejected-driver'
Check (-not (Install $extracted $fakeDriver $rejected)) 'unpinned driver files are rejected'
Check (-not (Test-Path -LiteralPath (Join-Path $rejected 'current.json'))) 'rejected driver leaves no current pointer'

$tampered = Join-Path $root 'tampered'
Copy-Item -LiteralPath $extracted -Destination $tampered -Recurse
Add-Content -LiteralPath (Join-Path $tampered 'test-virtual-display.ps1') -Value '# changed'
Check (-not (Install $tampered $fakeDriver (Join-Path $root 'install-tampered'))) 'tampered payload is rejected before copying'
Check (-not (Test-Path -LiteralPath (Join-Path $root 'install-tampered\versions'))) 'tampered payload creates no version folder'

if (Test-Path -LiteralPath (Join-Path $DriverPackageDirectory 'MttVDD.inf')) {
    $installed = Join-Path $root 'install'
    Check (Install $extracted $DriverPackageDirectory $installed) 'clean install with pinned driver succeeds'
    $current = Get-Content -LiteralPath (Join-Path $installed 'current.json') -Raw | ConvertFrom-Json
    Check ($current.version -ceq $version -and (Test-Path -LiteralPath (Join-Path $current.path 'DisplayWeavePrototype.exe'))) 'current pointer names the installed version'
    Check (@(Get-ChildItem -LiteralPath (Join-Path $current.path 'driver')).Count -eq 3) 'exactly the three driver files are stored'
    Check (-not (Test-Path -LiteralPath (Join-Path $current.path 'native-v2-x64'))) 'no legacy hook binaries are installed'
    Check (Install $extracted $DriverPackageDirectory $installed) 'reinstalling the same version verifies and reuses it'
    Add-Content -LiteralPath (Join-Path $current.path 'version.txt') -Value 'x'
    Check (-not (Install $extracted $DriverPackageDirectory $installed)) 'a modified installed version is not overwritten'
} else { $results.Add('SKIP pinned driver install cases (driver package not available)') }

$results.Add("$($results.Where({ $_ -like 'PASS *' }).Count) passed; $failed failed.")
New-Item -ItemType Directory -Path $root -Force | Out-Null
$results | Set-Content -LiteralPath (Join-Path $root 'results.txt')
$results | Write-Output
if ($failed) { throw 'Prototype installer checks failed.' }
exit 0
