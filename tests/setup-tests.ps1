#requires -Version 5.1
# End-to-end checks of release\DisplayWeave-Setup-<version>.exe (Inno Setup). Every run passes /testmode=1
# so the setup touches no shared state: no Start menu group, no Settings > Apps entry, no HKCU Run change,
# and it starts nothing. Installs go to a temporary root and are uninstalled by the setup's own uninstaller.
[CmdletBinding()]
param([string]$SetupPath, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repository 'src\DisplayWeave\version.txt') -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($SetupPath)) { $SetupPath = Join-Path $repository "release\DisplayWeave-Setup-$version.exe" }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repository 'test-results' }
$testRoot = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('setup-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[void][IO.Directory]::CreateDirectory($testRoot)
$report = New-Object 'System.Collections.Generic.List[string]'
$script:passed = 0; $script:failed = 0

function Assert-Setup([bool]$Condition, [string]$Message) { if (-not $Condition) { throw $Message } }
function Test-Setup([string]$Name, [scriptblock]$Body) {
    try { & $Body; $script:passed++; $report.Add('PASS ' + $Name) }
    catch { $script:failed++; $report.Add('FAIL ' + $Name + ': ' + $_.Exception.Message) }
}
function Invoke-Install([string]$Root) {
    $log = Join-Path $testRoot ([Guid]::NewGuid().ToString('N') + '.log')
    $process = Start-Process -FilePath $SetupPath -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/testmode=1',
        ('/DIR="' + $Root + '"'), ('/LOG="' + $log + '"')) -Wait -PassThru
    $text = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Raw } else { '' }
    return [pscustomobject]@{ Code = $process.ExitCode; Log = $text }
}
function Invoke-Uninstall([string]$Root) {
    $uninstaller = Join-Path $Root 'unins000.exe'
    if (-not [IO.File]::Exists($uninstaller)) { throw "Uninstaller not found in $Root." }
    $process = Start-Process -FilePath $uninstaller -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru
    # Inno's uninstaller spawns a copy of itself to finish; wait for the files to disappear.
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while ((Test-Path -LiteralPath (Join-Path $Root 'versions')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    return $process.ExitCode
}
function Read-RunKey {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $false)
    try { if ($null -eq $key) { return '<absent>' }; return [string]$key.GetValue('DisplayWeave', '<absent>', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) }
    finally { if ($null -ne $key) { $key.Dispose() } }
}

$runKeyBefore = Read-RunKey
$startMenu = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'DisplayWeave'
$startMenuBefore = Test-Path -LiteralPath $startMenu
$root = Join-Path $testRoot 'installed'

Test-Setup 'Silent install lays out a versioned installation with the stable launcher' {
    $result = Invoke-Install $root
    Assert-Setup ($result.Code -eq 0) ("Setup exited with $($result.Code): " + $result.Log)
    $pointer = Get-Content -LiteralPath (Join-Path $root 'current.json') -Raw | ConvertFrom-Json
    Assert-Setup ($pointer.version -ceq $version -and $pointer.executable -ceq "versions/$version/DisplayWeave.exe" -and $pointer.dataDirectory -ceq 'data') 'Unexpected current.json.'
    Assert-Setup (-not ($pointer.PSObject.Properties.Name -contains 'previousVersion')) 'A first install recorded a previous version.'
    Assert-Setup ([IO.File]::Exists((Join-Path $root "versions\$version\DisplayWeave.exe"))) 'DisplayWeave.exe was not installed.'
    Assert-Setup ([IO.File]::Exists((Join-Path $root 'DisplayWeave.Launcher.exe'))) 'The launcher is missing.'
    Assert-Setup ([IO.File]::Exists((Join-Path $root 'DisplayWeave.lnk'))) 'The install-root shortcut is missing.'
    Assert-Setup ([IO.File]::Exists((Join-Path $root 'unins000.exe'))) 'The uninstaller is missing.'
}

Test-Setup 'An update records the replaced version and keeps the old version folder' {
    # Make the current install look like an older version with its own version folder, then update over it.
    $old = '0.0.1-test'
    [void][IO.Directory]::CreateDirectory((Join-Path $root "versions\$old"))
    [IO.File]::WriteAllText((Join-Path $root "versions\$old\DisplayWeave.exe"), 'old version stand-in')
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'health'))
    [IO.File]::WriteAllText((Join-Path $root "health\$version.ok"), '')  # stale mark an update must clear
    [IO.File]::WriteAllText((Join-Path $root 'current.json'), "{`r`n    `"version`":  `"$old`",`r`n    `"executable`":  `"versions/$old/DisplayWeave.exe`",`r`n    `"dataDirectory`":  `"data`"`r`n}`r`n")
    $result = Invoke-Install $root
    Assert-Setup ($result.Code -eq 0) ("Update exited with $($result.Code): " + $result.Log)
    $pointer = Get-Content -LiteralPath (Join-Path $root 'current.json') -Raw | ConvertFrom-Json
    Assert-Setup ($pointer.version -ceq $version -and $pointer.previousVersion -ceq $old) 'Update did not record the replaced version.'
    Assert-Setup ([IO.File]::Exists((Join-Path $root "versions\$old\DisplayWeave.exe"))) 'The old version folder was removed.'
    Assert-Setup (-not [IO.File]::Exists((Join-Path $root "health\$version.ok"))) 'The stale health mark was not cleared.'
}

Test-Setup 'Uninstall removes the program and the pointer but keeps user settings' {
    # A fresh install of its own: in test mode there is no uninstall registry key, so a reinstall over the
    # updated root above would leave a second uninstaller. One clean install has a single unins000.exe.
    $uninstallRoot = Join-Path $testRoot 'uninstall'
    Assert-Setup ((Invoke-Install $uninstallRoot).Code -eq 0) 'Install for the uninstall test failed.'
    [IO.File]::WriteAllText((Join-Path $uninstallRoot 'data\layout.xml'), '<layout>kept</layout>')
    $code = Invoke-Uninstall $uninstallRoot
    Assert-Setup ($code -eq 0) "Uninstall exited with $code."
    Assert-Setup (-not (Test-Path -LiteralPath (Join-Path $uninstallRoot 'versions'))) 'Program files remained.'
    Assert-Setup (-not [IO.File]::Exists((Join-Path $uninstallRoot 'current.json'))) 'The version pointer remained.'
    Assert-Setup (-not [IO.File]::Exists((Join-Path $uninstallRoot 'DisplayWeave.Launcher.exe'))) 'The launcher remained.'
    Assert-Setup ([IO.File]::ReadAllText((Join-Path $uninstallRoot 'data\layout.xml')) -eq '<layout>kept</layout>') 'Settings were not kept.'
}

Test-Setup 'A running DisplayWeave that will not close stops the install and is left running' {
    # A stand-in DisplayWeave.exe (a copy of ping) under the install root counts as this install's instance.
    # It cannot honour --exit-for-update, and if another DisplayWeave is running the setup must not ask at all;
    # either way the setup has to give up before installing and terminate nothing.
    $busyRoot = Join-Path $testRoot 'running'
    $standIn = Join-Path $busyRoot 'stand-in\DisplayWeave.exe'
    [void][IO.Directory]::CreateDirectory((Split-Path -Parent $standIn))
    Copy-Item -LiteralPath (Join-Path ([Environment]::SystemDirectory) 'PING.EXE') -Destination $standIn
    $busy = Start-Process -FilePath $standIn -ArgumentList '-n 120 127.0.0.1' -WindowStyle Hidden -PassThru
    try {
        $result = Invoke-Install $busyRoot
        Assert-Setup ($result.Code -eq 7) ("Expected the preparing step to fail (7), got $($result.Code): " + $result.Log)
        Assert-Setup (-not $busy.HasExited) 'The setup stopped the running instance.'
        Assert-Setup (-not [IO.File]::Exists((Join-Path $busyRoot 'current.json'))) 'The setup installed anyway.'
    }
    finally { if (-not $busy.HasExited) { $busy.Kill() }; $busy.Dispose() }
}

Test-Setup 'Test-mode installs leave the HKCU Run value and the Start menu untouched' {
    Assert-Setup ((Read-RunKey) -ceq $runKeyBefore) 'The startup registry value changed.'
    Assert-Setup ((Test-Path -LiteralPath $startMenu) -eq $startMenuBefore) 'The Start menu group changed.'
}

$report.Add('')
$report.Add("$script:passed passed; $script:failed failed.")
[IO.File]::WriteAllLines((Join-Path $testRoot 'setup-test-results.txt'), $report)
$report | Write-Output
Write-Output "Artifacts: $testRoot"
if ($script:failed -gt 0) { exit 1 }
