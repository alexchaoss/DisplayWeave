#requires -Version 5.1
# End-to-end checks of release\DisplayWeave-Setup-<version>.exe in quiet mode with a
# temporary install root: no app launch, no startup, Start menu or Settings > Apps changes.
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
function Invoke-Setup([string]$Executable, [string[]]$Arguments) {
    $log = Join-Path $testRoot ([Guid]::NewGuid().ToString('N') + '.log')
    $process = Start-Process -FilePath $Executable -ArgumentList ($Arguments + @('--quiet', '--log', ('"' + $log + '"'))) -Wait -PassThru
    $text = if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Raw } else { '' }
    return [pscustomobject]@{ Code = $process.ExitCode; Log = $text }
}
function Read-Snapshot {
    $values = @()
    foreach ($path in @('Software\Microsoft\Windows\CurrentVersion\Uninstall\DisplayWeave', 'Software\Microsoft\Windows\CurrentVersion\Run', 'Software\Microsoft\Windows\CurrentVersion\RunOnce')) {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($path, $false)
        if ($null -eq $key) { $values += "$path=<absent>"; continue }
        try { foreach ($name in @($key.GetValueNames() | Where-Object { $_ -like 'DisplayWeave*' -or $path -like '*Uninstall*' } | Sort-Object)) { $values += "$path\$name=" + [string]$key.GetValue($name) } }
        finally { $key.Dispose() }
    }
    $shortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'DisplayWeave.lnk'
    $values += 'StartMenu=' + $(if ([IO.File]::Exists($shortcut)) { (Get-FileHash -LiteralPath $shortcut).Hash } else { '<absent>' })
    return ($values -join "`n")
}

$before = Read-Snapshot
$root = Join-Path $testRoot 'installed'

Test-Setup 'Quiet install creates a versioned installation and an uninstaller copy' {
    $result = Invoke-Setup $SetupPath @('--install-root', ('"' + $root + '"'), '--no-launch', '--startup', 'Preserve')
    Assert-Setup ($result.Code -eq 0) ("Setup exited with $($result.Code): " + $result.Log)
    $pointer = Get-Content -LiteralPath (Join-Path $root 'current.json') -Raw | ConvertFrom-Json
    Assert-Setup ($pointer.version -ceq $version) "Installed version is $($pointer.version)."
    Assert-Setup ([IO.File]::Exists((Join-Path $root "versions\$version\DisplayWeave.exe"))) 'DisplayWeave.exe was not installed.'
    Assert-Setup ([IO.File]::Exists((Join-Path $root 'Uninstall DisplayWeave.exe'))) 'The uninstaller copy is missing.'
    Assert-Setup ([IO.File]::Exists((Join-Path $root 'Launch-DisplayWeave.ps1'))) 'The launcher is missing.'
}

Test-Setup 'Running the same setup again is accepted' {
    $result = Invoke-Setup $SetupPath @('--install-root', ('"' + $root + '"'), '--no-launch')
    Assert-Setup ($result.Code -eq 0) ("Repeated setup exited with $($result.Code): " + $result.Log)
}

Test-Setup 'The installed uninstaller removes the program, keeps settings, then deletes itself' {
    [void][IO.Directory]::CreateDirectory((Join-Path $root 'data'))
    [IO.File]::WriteAllText((Join-Path $root 'data\layout.xml'), '<layout>kept</layout>')
    $result = Invoke-Setup (Join-Path $root 'Uninstall DisplayWeave.exe') @('--install-root', ('"' + $root + '"'))
    Assert-Setup ($result.Code -eq 0) ("Uninstall exited with $($result.Code): " + $result.Log)
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    while ([IO.File]::Exists((Join-Path $root 'Uninstall DisplayWeave.exe')) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 250 }
    Assert-Setup (-not [IO.File]::Exists((Join-Path $root 'Uninstall DisplayWeave.exe'))) 'The uninstaller did not delete itself.'
    Assert-Setup (-not (Test-Path -LiteralPath (Join-Path $root 'versions'))) 'Program files remained.'
    Assert-Setup ([IO.File]::ReadAllText((Join-Path $root 'data\layout.xml')) -eq '<layout>kept</layout>') 'Settings were not kept.'
}

Test-Setup 'Removing settings deletes the whole installation folder' {
    $full = Join-Path $testRoot 'full'
    Assert-Setup ((Invoke-Setup $SetupPath @('--install-root', ('"' + $full + '"'), '--no-launch')).Code -eq 0) 'Install failed.'
    $result = Invoke-Setup $SetupPath @('--uninstall', '--remove-settings', '--install-root', ('"' + $full + '"'))
    Assert-Setup ($result.Code -eq 0) ("Uninstall exited with $($result.Code): " + $result.Log)
    Assert-Setup (-not (Test-Path -LiteralPath $full)) 'The installation folder remained.'
}

Test-Setup 'Uninstalling an already removed installation succeeds without changes' {
    $result = Invoke-Setup $SetupPath @('--uninstall', '--install-root', ('"' + (Join-Path $testRoot 'never-installed') + '"'))
    Assert-Setup ($result.Code -eq 0) ("Uninstall of a missing installation exited with $($result.Code).")
    Assert-Setup (-not (Test-Path -LiteralPath (Join-Path $testRoot 'never-installed'))) 'Uninstall created a folder.'
}

Test-Setup 'Test roots leave Settings > Apps, startup and the Start menu unchanged' {
    Assert-Setup ((Read-Snapshot) -ceq $before) 'Registry or Start menu state changed.'
}

$report.Add('')
$report.Add("$script:passed passed; $script:failed failed.")
[IO.File]::WriteAllLines((Join-Path $testRoot 'setup-test-results.txt'), $report)
$report | Write-Output
Write-Output "Artifacts: $testRoot"
if ($script:failed -gt 0) { exit 1 }
