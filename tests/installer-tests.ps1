#requires -Version 5.1
# Local fixtures only: no GitHub calls, no application launches. The only registry
# write is a temporary HKCU RunOnce value in the in-use uninstall case, restored after it.
[CmdletBinding()]
param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
$installer = Join-Path (Split-Path -Parent $PSScriptRoot) 'installer\install.ps1'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'test-results' }
$testRoot = Join-Path ([IO.Path]::GetFullPath($OutputDirectory)) ('installer-' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
[void][IO.Directory]::CreateDirectory($testRoot)
$utf8 = New-Object Text.UTF8Encoding($false)
$report = New-Object 'System.Collections.Generic.List[string]'
$script:passed = 0
$script:failed = 0

function Assert-Installer([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function Test-Installer([string]$Name, [scriptblock]$Body) {
    try { & $Body; $script:passed++; $report.Add('PASS ' + $Name) }
    catch { $script:failed++; $report.Add('FAIL ' + $Name + ': ' + $_.Exception.Message) }
}

function New-Fixture([string]$Name, [string]$Version, [object[]]$Extra = @(), [string]$Omit = '') {
    $zipPath = Join-Path $testRoot ($Name + '.zip')
    $stream = New-Object IO.FileStream($zipPath, [IO.FileMode]::CreateNew)
    $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Create, $true)
    try {
        $items = @([pscustomobject]@{ Name = 'release.json'; Content = ('{"version":"' + $Version + '"}'); Attributes = 0 })
        foreach ($file in @('DisplayWeave.exe', 'DisplayWeave.Core.dll', 'native-v2-x64/RegionHookHost.exe', 'native-v2-x64/RegionHook.dll',
            'native-v2-x86/RegionHookHost.exe', 'native-v2-x86/RegionHook.dll')) {
            if ($file -ne $Omit) { $items += [pscustomobject]@{ Name = $file; Content = 'Non-executable installer test fixture'; Attributes = 0 } }
        }
        $items += $Extra
        foreach ($item in $items) {
            $entry = $archive.CreateEntry($item.Name)
            $entry.ExternalAttributes = $item.Attributes
            $output = $entry.Open()
            try { $bytes = $utf8.GetBytes($item.Content); $output.Write($bytes, 0, $bytes.Length) }
            finally { $output.Dispose() }
        }
    }
    finally { $archive.Dispose(); $stream.Dispose() }
    $checksumPath = $zipPath + '.sha256'
    $digest = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($checksumPath, $digest + '  ' + [IO.Path]::GetFileName($zipPath) + [Environment]::NewLine, $utf8)
    return [pscustomobject]@{ Zip = $zipPath; Checksum = $checksumPath }
}

function Invoke-Fixture([object]$Fixture, [string]$Root, [string]$Migration = '') {
    $arguments = @{ PackagePath = $Fixture.Zip; ChecksumPath = $Fixture.Checksum; InstallRoot = $Root; Startup = 'Preserve'; NoLaunch = $true }
    if ($Migration.Length -gt 0) { $arguments['MigrateFrom'] = $Migration }
    & $installer @arguments | Out-Null
}

function Assert-Rejected([scriptblock]$Body) {
    $rejected = $false
    try { & $Body } catch { $rejected = $true }
    Assert-Installer $rejected 'Unsafe or incomplete package was accepted.'
}

function Read-StartupSnapshot {
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $false)
    try {
        if ($null -eq $key -or -not (@($key.GetValueNames()) -contains 'DisplayWeave')) { return '<absent>' }
        return ($key.GetValueKind('DisplayWeave').ToString() + ':' + [string]$key.GetValue('DisplayWeave', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames))
    }
    finally { if ($null -ne $key) { $key.Dispose() } }
}

$startupBefore = Read-StartupSnapshot
$startMenuShortcut = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'DisplayWeave.lnk'
$startMenuBefore = if ([IO.File]::Exists($startMenuShortcut)) { (Get-FileHash -LiteralPath $startMenuShortcut -Algorithm SHA256).Hash } else { '<absent>' }
$installation = Join-Path $testRoot 'installed'
$portable = Join-Path $testRoot 'portable'
[void][IO.Directory]::CreateDirectory($portable)
[IO.File]::WriteAllText((Join-Path $portable 'layout.xml'), '<layout>migrated</layout>', $utf8)
[IO.File]::WriteAllText((Join-Path $portable 'rules.xml'), '<rules>migrated</rules>', $utf8)
$first = New-Fixture 'DisplayWeave-0.4.0-preview' '0.4.0-preview'
$second = New-Fixture 'DisplayWeave-0.4.1-preview' '0.4.1-preview'

Test-Installer 'First installation creates a stable pointer and migrates only missing settings' {
    Invoke-Fixture $first $installation $portable
    $pointer = Get-Content -LiteralPath (Join-Path $installation 'current.json') -Raw | ConvertFrom-Json
    Assert-Installer ($pointer.version -eq '0.4.0-preview' -and $pointer.executable -eq 'versions/0.4.0-preview/DisplayWeave.exe' -and $pointer.dataDirectory -eq 'data') 'Unexpected pointer schema.'
    Assert-Installer ([IO.File]::ReadAllText((Join-Path $installation 'data\layout.xml')) -eq '<layout>migrated</layout>') 'Layout was not migrated.'
    Assert-Installer ([IO.File]::ReadAllText((Join-Path $installation 'data\rules.xml')) -eq '<rules>migrated</rules>') 'Rules were not migrated.'
    Assert-Installer ([IO.File]::Exists((Join-Path $installation 'DisplayWeave.lnk'))) 'Install-root shortcut is missing.'
    $errors = $null; $tokens = $null
    [void][Management.Automation.Language.Parser]::ParseFile((Join-Path $installation 'Launch-DisplayWeave.ps1'), [ref]$tokens, [ref]$errors)
    Assert-Installer ($errors.Count -eq 0) 'Generated launcher has PowerShell syntax errors.'
}

Test-Installer 'Update retains old version and existing user settings' {
    [IO.File]::WriteAllText((Join-Path $installation 'data\layout.xml'), '<layout>user edit</layout>', $utf8)
    Invoke-Fixture $second $installation $portable
    $pointer = Get-Content -LiteralPath (Join-Path $installation 'current.json') -Raw | ConvertFrom-Json
    Assert-Installer ($pointer.version -eq '0.4.1-preview') 'Version pointer did not advance.'
    Assert-Installer ([IO.File]::Exists((Join-Path $installation 'versions\0.4.0-preview\DisplayWeave.exe'))) 'Old version was removed.'
    Assert-Installer ([IO.File]::ReadAllText((Join-Path $installation 'data\layout.xml')) -eq '<layout>user edit</layout>') 'Existing settings were overwritten.'
}

Test-Installer 'Installing the identical package again is idempotent' {
    Invoke-Fixture $second $installation
    Assert-Installer (@(Get-ChildItem -LiteralPath (Join-Path $installation 'versions') -Directory).Count -eq 2) 'Idempotent install added another version.'
}

Test-Installer 'The launcher goes back to the replaced version after two failed starts' {
    $pointerPath = Join-Path $installation 'current.json'
    $before = [IO.File]::ReadAllText($pointerPath)
    Assert-Installer (($before | ConvertFrom-Json).previousVersion -eq '0.4.0-preview') 'The pointer does not name the version it replaced.'
    $health = Join-Path $installation 'health'
    [void][IO.Directory]::CreateDirectory($health)
    [IO.File]::WriteAllText((Join-Path $health '0.4.1-preview.starts'), "1`r`n2`r`n", $utf8)
    # The fixture's programs are not executable, so each launch fails after its bookkeeping and nothing starts.
    $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
    $arguments = '-NoProfile -ExecutionPolicy Bypass -File "' + (Join-Path $installation 'Launch-DisplayWeave.ps1') + '"'
    [IO.File]::WriteAllText((Join-Path $health '0.4.1-preview.ok'), '', $utf8)
    Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle Hidden -Wait
    Assert-Installer ([IO.File]::ReadAllText($pointerPath) -eq $before) 'A version that ran cleanly was given up.'
    [IO.File]::Delete((Join-Path $health '0.4.1-preview.ok'))
    Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle Hidden -Wait
    $after = [IO.File]::ReadAllText($pointerPath) | ConvertFrom-Json
    Assert-Installer ($after.version -eq '0.4.0-preview' -and [IO.File]::Exists((Join-Path $health '0.4.1-preview.failed'))) 'The launcher did not go back after two failed starts.'
    Invoke-Fixture $second $installation
    $reinstalled = [IO.File]::ReadAllText($pointerPath) | ConvertFrom-Json
    Assert-Installer ($reinstalled.version -eq '0.4.1-preview' -and -not [IO.File]::Exists((Join-Path $health '0.4.1-preview.starts'))) 'Reinstalling did not reset the failed starts.'
    [IO.File]::Delete((Join-Path $health '0.4.1-preview.failed'))
}

Test-Installer 'A version folder is never replaced by different package bytes' {
    $changed = New-Fixture 'changed-same-version' '0.4.1-preview' @([pscustomobject]@{ Name = 'extra.txt'; Content = 'Different archive'; Attributes = 0 })
    Assert-Rejected { Invoke-Fixture $changed $installation }
    Assert-Installer (-not [IO.File]::Exists((Join-Path $installation 'versions\0.4.1-preview\extra.txt'))) 'Existing immutable version was modified.'
}

Test-Installer 'Bad SHA-256 is rejected before extraction and pointer changes' {
    $badChecksum = Join-Path $testRoot 'bad.sha256'
    [IO.File]::WriteAllText($badChecksum, ('0' * 64) + '  ' + [IO.Path]::GetFileName($first.Zip), $utf8)
    $before = [IO.File]::ReadAllText((Join-Path $installation 'current.json'))
    Assert-Rejected { & $installer -PackagePath $first.Zip -ChecksumPath $badChecksum -InstallRoot $installation -Startup Preserve -NoLaunch | Out-Null }
    Assert-Installer ([IO.File]::ReadAllText((Join-Path $installation 'current.json')) -eq $before) 'Bad checksum changed the pointer.'
}

Test-Installer 'Checksum is mandatory for a local package' {
    Assert-Rejected { & $installer -PackagePath $first.Zip -InstallRoot (Join-Path $testRoot 'missing-checksum') -Startup Preserve -NoLaunch | Out-Null }
}

Test-Installer 'Parent-directory ZIP traversal is rejected' {
    $fixture = New-Fixture 'traversal' '0.4.2-preview' @([pscustomobject]@{ Name = '../escaped.txt'; Content = 'unsafe'; Attributes = 0 })
    Assert-Rejected { Invoke-Fixture $fixture (Join-Path $testRoot 'traversal-install') }
    Assert-Installer (-not [IO.File]::Exists((Join-Path $testRoot 'traversal-install\escaped.txt'))) 'Traversal escaped staging.'
}

Test-Installer 'Rooted ZIP names are rejected' {
    $fixture = New-Fixture 'rooted' '0.4.2-preview' @([pscustomobject]@{ Name = '/outside.txt'; Content = 'unsafe'; Attributes = 0 })
    Assert-Rejected { Invoke-Fixture $fixture (Join-Path $testRoot 'rooted-install') }
}

Test-Installer 'Case-insensitive duplicate ZIP paths are rejected' {
    $fixture = New-Fixture 'duplicate' '0.4.2-preview' @([pscustomobject]@{ Name = 'DISPLAYWEAVE.EXE'; Content = 'duplicate'; Attributes = 0 })
    Assert-Rejected { Invoke-Fixture $fixture (Join-Path $testRoot 'duplicate-install') }
}

Test-Installer 'Unix symbolic-link ZIP entries are rejected' {
    $fixture = New-Fixture 'symlink' '0.4.2-preview' @([pscustomobject]@{ Name = 'link'; Content = '../outside'; Attributes = (0xA1FF -shl 16) })
    Assert-Rejected { Invoke-Fixture $fixture (Join-Path $testRoot 'symlink-install') }
}

Test-Installer 'A ZIP file cannot also act as a parent directory' {
    $fixture = New-Fixture 'parent-file' '0.4.2-preview' @(
        [pscustomobject]@{ Name = 'collision'; Content = 'file'; Attributes = 0 },
        [pscustomobject]@{ Name = 'collision/child.txt'; Content = 'child'; Attributes = 0 })
    Assert-Rejected { Invoke-Fixture $fixture (Join-Path $testRoot 'parent-file-install') }
}

Test-Installer 'A missing native architecture file rejects the candidate' {
    $fixture = New-Fixture 'incomplete' '0.4.2-preview' @() 'native-v2-x86/RegionHook.dll'
    Assert-Rejected { Invoke-Fixture $fixture (Join-Path $testRoot 'incomplete-install') }
}

Test-Installer 'Requested version must agree with the release manifest' {
    Assert-Rejected { & $installer -PackagePath $first.Zip -ChecksumPath $first.Checksum -Version v9.9.9 -InstallRoot (Join-Path $testRoot 'wrong-version') -Startup Preserve -NoLaunch | Out-Null }
}

Test-Installer 'Failed pointer replacement restores the previous launcher and pointer' {
    $pointerPath = Join-Path $installation 'current.json'
    $launcherPath = Join-Path $installation 'Launch-DisplayWeave.ps1'
    $before = [IO.File]::ReadAllText($pointerPath)
    $originalLauncher = '# Existing launcher content must survive rollback.'
    [IO.File]::WriteAllText($launcherPath, $originalLauncher, $utf8)
    $fixture = New-Fixture 'rollback' '0.4.2-preview'
    # Deny delete/write sharing specifically on the pointer, after permitting
    # the installer to read it and update the independent launcher file.
    $lock = New-Object IO.FileStream($pointerPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Assert-Rejected { Invoke-Fixture $fixture $installation } }
    finally { $lock.Dispose() }
    Assert-Installer ([IO.File]::ReadAllText($pointerPath) -eq $before) 'Failed transaction changed the pointer.'
    Assert-Installer ([IO.File]::ReadAllText($launcherPath) -eq $originalLauncher) 'Failed transaction did not restore the launcher.'
}

Test-Installer 'Uninstall removes program files and keeps settings' {
    & $installer -Uninstall -InstallRoot $installation | Out-Null
    foreach ($removed in @('current.json', 'Launch-DisplayWeave.ps1', 'DisplayWeave.lnk', 'versions', 'health', '.install.lock')) {
        Assert-Installer (-not (Test-Path -LiteralPath (Join-Path $installation $removed))) "Uninstall left $removed."
    }
    Assert-Installer ([IO.File]::ReadAllText((Join-Path $installation 'data\layout.xml')) -eq '<layout>user edit</layout>') 'Uninstall removed settings without -RemoveSettings.'
}

Test-Installer 'Uninstall with RemoveSettings removes the whole installation folder' {
    $root = Join-Path $testRoot 'remove-all'
    Invoke-Fixture $first $root $portable
    & $installer -Uninstall -RemoveSettings -InstallRoot $root | Out-Null
    Assert-Installer (-not (Test-Path -LiteralPath $root)) 'Installation folder remained after a full uninstall.'
}

Test-Installer 'Uninstall leaves files in use for a cleanup at the next sign-in' {
    $root = Join-Path $testRoot 'in-use'
    Invoke-Fixture $first $root
    $runOncePath = 'Software\Microsoft\Windows\CurrentVersion\RunOnce'
    $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runOncePath, $false)
    $previous = if ($null -ne $key) { $key.GetValue('DisplayWeaveCleanup') } else { $null }
    if ($null -ne $key) { $key.Dispose() }
    # Stands in for another app that still has DisplayWeave's DLL loaded.
    $lock = New-Object IO.FileStream((Join-Path $root 'versions\0.4.0-preview\native-v2-x64\RegionHook.dll'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $output = (& $installer -Uninstall -InstallRoot $root) -join "`n"
        Assert-Installer ($output -match 'next sign-in') 'Uninstall did not report files in use.'
        Assert-Installer (-not [IO.File]::Exists((Join-Path $root 'current.json'))) 'The version pointer remained.'
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($runOncePath, $false)
        try { $cleanup = [string]$key.GetValue('DisplayWeaveCleanup') } finally { $key.Dispose() }
        Assert-Installer ($cleanup.Contains((Join-Path $root 'versions'))) 'No next-sign-in cleanup was scheduled.'
    }
    finally {
        $lock.Dispose()
        # Restore the user's RunOnce state; this test's folder is removed with the test artifacts.
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($runOncePath)
        try { if ($null -ne $previous) { $key.SetValue('DisplayWeaveCleanup', $previous) } else { $key.DeleteValue('DisplayWeaveCleanup', $false) } }
        finally { $key.Dispose() }
    }
}

Test-Installer 'Uninstall refuses a folder without a DisplayWeave installation' {
    $root = Join-Path $testRoot 'not-an-install'
    [void][IO.Directory]::CreateDirectory($root)
    [IO.File]::WriteAllText((Join-Path $root 'keep.txt'), 'unrelated', $utf8)
    Assert-Rejected { & $installer -Uninstall -RemoveSettings -InstallRoot $root | Out-Null }
    Assert-Installer ([IO.File]::Exists((Join-Path $root 'keep.txt'))) 'Uninstall touched an unrelated folder.'
}

Test-Installer 'Startup Preserve leaves the actual HKCU Run value unchanged' {
    Assert-Installer ((Read-StartupSnapshot) -ceq $startupBefore) 'Installer changed the startup registry value in Preserve mode.'
}

Test-Installer 'Custom install roots leave the actual Start menu shortcut unchanged' {
    $after = if ([IO.File]::Exists($startMenuShortcut)) { (Get-FileHash -LiteralPath $startMenuShortcut -Algorithm SHA256).Hash } else { '<absent>' }
    Assert-Installer ($after -ceq $startMenuBefore) 'Isolated installs changed the Start menu shortcut.'
}

$report.Add('')
$report.Add("$script:passed passed; $script:failed failed.")
$reportPath = Join-Path $testRoot 'installer-test-results.txt'
[IO.File]::WriteAllLines($reportPath, $report, $utf8)
$report | Write-Output
Write-Output "Artifacts: $testRoot"
if ($script:failed -gt 0) { exit 1 }
