#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$PackagePath,
    [string]$ChecksumPath,
    [ValidatePattern('^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$')]
    [string]$Repository = 'alexchaoss/DisplayWeave',
    [string]$Version,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'DisplayWeave'),
    [string]$MigrateFrom,
    [ValidateSet('Preserve', 'Enable', 'Disable')]
    [string]$Startup = 'Preserve',
    [switch]$NoLaunch,
    [switch]$Uninstall,
    [switch]$RemoveSettings
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0
Add-Type -AssemblyName System.IO.Compression

function Get-NormalPath([string]$Path) {
    $absolute = [IO.Path]::GetFullPath($Path)
    $volume = [IO.Path]::GetPathRoot($absolute)
    if ($absolute.Length -le $volume.Length) { return $absolute }
    return $absolute.TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar)
}

function Test-UnderRoot([string]$Path, [string]$Root) {
    $candidate = Get-NormalPath $Path
    $parent = (Get-NormalPath $Root).TrimEnd([IO.Path]::DirectorySeparatorChar, [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    return $candidate.StartsWith($parent, [StringComparison]::OrdinalIgnoreCase)
}

function Assert-PlainPath([string]$Path, [bool]$Directory) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Refusing a symbolic link or reparse point: $Path"
    }
    if ($item.PSIsContainer -ne $Directory) { throw "Unexpected file or directory type: $Path" }
}

function New-PlainDirectory([string]$Path) {
    Assert-PlainPath $Path $true
    if (-not (Test-Path -LiteralPath $Path)) { [void][IO.Directory]::CreateDirectory($Path) }
    Assert-PlainPath $Path $true
}

function Assert-PlainTree([string]$Path) {
    Assert-PlainPath $Path $true
    foreach ($entry in [IO.Directory]::EnumerateFileSystemEntries($Path)) {
        $attributes = [IO.File]::GetAttributes($entry)
        if (($attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw "Refusing a reparse point inside installation files: $entry" }
        if (($attributes -band [IO.FileAttributes]::Directory) -ne 0) { Assert-PlainTree $entry }
    }
}

function Remove-OwnedTemporaryDirectory([string]$Path, [string]$Parent) {
    if ([string]::IsNullOrEmpty($Path) -or -not (Test-Path -LiteralPath $Path)) { return }
    $absolute = Get-NormalPath $Path
    $expectedParent = Get-NormalPath $Parent
    if (-not [string]::Equals([IO.Path]::GetDirectoryName($absolute), $expectedParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($absolute) -notmatch '^(?:\.install-stage-|DisplayWeave-download-)[0-9a-f]{32}$') {
        throw "Refusing cleanup outside this installer's uniquely named temporary directory: $absolute"
    }
    Assert-PlainTree $absolute
    Remove-Item -LiteralPath $absolute -Recurse -Force
}

function Write-AtomicBytes([string]$Path, [byte[]]$Bytes) {
    Assert-PlainPath $Path $false
    $temporary = $Path + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $backup = $temporary + '.backup'
    try {
        $stream = New-Object IO.FileStream($temporary, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $stream.Write($Bytes, 0, $Bytes.Length); $stream.Flush($true) }
        finally { $stream.Dispose() }
        if ([IO.File]::Exists($Path)) { [IO.File]::Replace($temporary, $Path, $backup) }
        else { [IO.File]::Move($temporary, $Path) }
    }
    finally {
        foreach ($cleanup in @($temporary, $backup)) {
            try { if ([IO.File]::Exists($cleanup)) { [IO.File]::Delete($cleanup) } }
            catch { Write-Warning ("Atomic-write temporary file retained at " + $cleanup + ': ' + $_.Exception.Message) }
        }
    }
}

function Write-LauncherShortcut([string]$Path, [string]$LauncherPath, [string]$IconPath) {
    Assert-PlainPath $Path $false
    $temporary = [IO.Path]::ChangeExtension($Path, [Guid]::NewGuid().ToString('N') + '.lnk')
    $shell = $null
    $shortcut = $null
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($temporary)
        $shortcut.TargetPath = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $shortcut.Arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $LauncherPath + '"'
        $shortcut.WorkingDirectory = [IO.Path]::GetDirectoryName($LauncherPath)
        $shortcut.Description = 'Open DisplayWeave settings'
        $shortcut.IconLocation = $IconPath + ',0'
        $shortcut.WindowStyle = 7
        $shortcut.Save()
        Write-AtomicBytes $Path ([IO.File]::ReadAllBytes($temporary))
    }
    finally {
        if ($null -ne $shortcut) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shortcut) }
        if ($null -ne $shell) { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Get-ExpectedChecksum([string]$Path, [string]$ArchiveName) {
    Assert-PlainPath $Path $false
    if (-not [IO.File]::Exists($Path)) { throw "Required SHA-256 file was not found: $Path" }
    if ((Get-Item -LiteralPath $Path).Length -gt 65536) { throw 'SHA-256 file is unexpectedly large.' }
    $matchesForPackage = @()
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }
        if ($line.Trim() -notmatch '^(?<digest>[0-9A-Fa-f]{64})(?:[ \t]+\*?(?<filename>.+))?$') {
            throw 'SHA-256 file must contain standard SHA-256 checksum lines.'
        }
        $digest = $Matches['digest']
        $file = $Matches['filename']
        if ([string]::IsNullOrEmpty($file) -or [string]::Equals($file, $ArchiveName, [StringComparison]::OrdinalIgnoreCase)) {
            $matchesForPackage += $digest.ToLowerInvariant()
        }
    }
    if ($matchesForPackage.Count -ne 1) { throw "SHA-256 file must contain exactly one checksum for $ArchiveName." }
    return $matchesForPackage[0]
}

function Expand-VerifiedPackage([string]$ArchivePath, [string]$ExpectedHash, [string]$Destination) {
    Assert-PlainPath $ArchivePath $false
    # Keep the same read-only, non-write-shared handle from hash verification
    # through extraction so a concurrent writer cannot swap the checked bytes.
    $stream = New-Object IO.FileStream($ArchivePath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $archive = $null
    try {
        if ($stream.Length -gt 1073741824) { throw 'Package exceeds the 1 GiB installation limit.' }
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { $actualHash = ([BitConverter]::ToString($algorithm.ComputeHash($stream))).Replace('-', '').ToLowerInvariant() }
        finally { $algorithm.Dispose() }
        if ($actualHash -ne $ExpectedHash) { throw 'Package SHA-256 does not match the required checksum. Nothing was installed.' }
        $stream.Position = 0
        $archive = New-Object IO.Compression.ZipArchive($stream, [IO.Compression.ZipArchiveMode]::Read, $true)
        if ($archive.Entries.Count -gt 10000) { throw 'Package has too many entries.' }
        $paths = New-Object 'System.Collections.Generic.Dictionary[string,bool]' ([StringComparer]::OrdinalIgnoreCase)
        $entries = New-Object 'System.Collections.Generic.List[object]'
        [long]$totalBytes = 0
        foreach ($entry in $archive.Entries) {
            $name = $entry.FullName.Replace('\', '/')
            $directory = $name.EndsWith('/')
            if ($name.StartsWith('/') -or [IO.Path]::IsPathRooted($name)) { throw "Rooted ZIP entry is not allowed: $name" }
            $relative = $name.TrimEnd('/')
            $segments = $relative.Split('/')
            if ($segments.Count -gt 32) { throw "ZIP entry is nested too deeply: $name" }
            foreach ($segment in $segments) {
                if ([string]::IsNullOrEmpty($segment) -or $segment -eq '.' -or $segment -eq '..' -or
                    $segment -match '[<>:"|?*\x00-\x1F]' -or $segment.EndsWith('.') -or $segment.EndsWith(' ') -or
                    $segment -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|$)') {
                    throw "Unsafe ZIP entry name: $name"
                }
            }
            [long]$attributes = [long]$entry.ExternalAttributes -band 0xFFFFFFFFL
            $unixType = ($attributes -shr 16) -band 0xF000
            if (($attributes -band 0x400) -ne 0 -or ($unixType -ne 0 -and $unixType -ne 0x4000 -and $unixType -ne 0x8000)) {
                throw "Links and special files are not allowed in a package: $name"
            }
            if (($unixType -eq 0x4000 -and -not $directory) -or ($unixType -eq 0x8000 -and $directory)) {
                throw "ZIP entry has conflicting file attributes: $name"
            }
            if ($paths.ContainsKey($relative)) { throw "Duplicate ZIP entry: $name" }
            $paths.Add($relative, $directory)
            $target = Get-NormalPath (Join-Path $Destination ($relative.Replace('/', '\')))
            if (-not (Test-UnderRoot $target $Destination)) { throw "ZIP entry escapes the extraction directory: $name" }
            if ($entry.Length -gt 268435456) { throw "ZIP entry exceeds the 256 MiB limit: $name" }
            $totalBytes += $entry.Length
            if ($totalBytes -gt 1073741824) { throw 'Expanded package exceeds the 1 GiB installation limit.' }
            $entries.Add([pscustomobject]@{ Entry = $entry; Relative = $relative; Target = $target; Directory = $directory })
        }
        foreach ($item in $entries) {
            $parent = $item.Relative
            while ($parent.Contains('/')) {
                $parent = $parent.Substring(0, $parent.LastIndexOf('/'))
                if ($paths.ContainsKey($parent) -and -not $paths[$parent]) { throw "ZIP file is also used as a parent directory: $parent" }
            }
        }
        New-PlainDirectory $Destination
        foreach ($item in $entries) {
            if ($item.Directory) { New-PlainDirectory $item.Target; continue }
            New-PlainDirectory ([IO.Path]::GetDirectoryName($item.Target))
            $inputStream = $item.Entry.Open()
            $outputStream = $null
            try {
                $outputStream = New-Object IO.FileStream($item.Target, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
                $buffer = New-Object byte[] 65536
                [long]$written = 0
                while (($count = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    $written += $count
                    if ($written -gt $item.Entry.Length) { throw "ZIP entry expands beyond its declared length: $($item.Relative)" }
                    $outputStream.Write($buffer, 0, $count)
                }
                if ($written -ne $item.Entry.Length) { throw "ZIP entry has an invalid expanded length: $($item.Relative)" }
            }
            finally { if ($null -ne $outputStream) { $outputStream.Dispose() }; $inputStream.Dispose() }
        }
    }
    finally { if ($null -ne $archive) { $archive.Dispose() }; $stream.Dispose() }
}

function Read-CandidateVersion([string]$Directory) {
    $manifestPath = Join-Path $Directory 'release.json'
    if (-not [IO.File]::Exists($manifestPath)) { throw 'The ZIP must contain release.json at its root.' }
    if ((Get-Item -LiteralPath $manifestPath).Length -gt 1048576) { throw 'Release manifest is unexpectedly large.' }
    $manifest = [IO.File]::ReadAllText($manifestPath) | ConvertFrom-Json
    if ($null -eq $manifest -or $null -eq $manifest.PSObject.Properties['version'] -or
        $manifest.version -isnot [string] -or $manifest.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw 'Release manifest must contain a valid version such as 0.4.0-preview.'
    }
    foreach ($required in @('DisplayWeave.exe', 'DisplayWeave.Core.dll', 'native-v2-x64\RegionHookHost.exe', 'native-v2-x64\RegionHook.dll',
        'native-v2-x86\RegionHookHost.exe', 'native-v2-x86\RegionHook.dll')) {
        $file = Join-Path $Directory $required
        Assert-PlainPath $file $false
        if (-not [IO.File]::Exists($file) -or (Get-Item -LiteralPath $file).Length -eq 0) { throw "Release is missing a required nonempty file: $required" }
    }
    return $manifest.version
}

# Public halves of the release signing keys, the same list as ReleaseKeys in src\DisplayWeave\Updater.cs.
# scripts\sign-release.ps1 -NewKey or -PinKey adds a key to both.
$releaseKeys = @('RUNTMSAAAAATu9pkeZZN3cVNNw4GOUxueHEixTzm9R7e7MN0k3itVRAOfmWB0wJJnw1R+TlDITlNIiV/J1hxl/u9rDcSzX0m')

# The release's .sha256 must carry a signature from a pinned key; it names the ZIP, and so its version.
function Assert-ReleaseSignature([string]$ChecksumPath) {
    $data = [IO.File]::ReadAllBytes($ChecksumPath)
    try { $signature = [Convert]::FromBase64String([IO.File]::ReadAllText($ChecksumPath + '.sig').Trim()) }
    catch { throw 'The release signature is unreadable. Nothing was installed.' }
    foreach ($key in $releaseKeys) {
        $publicKey = [Security.Cryptography.CngKey]::Import([Convert]::FromBase64String($key), [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
        $ecdsa = New-Object Security.Cryptography.ECDsaCng($publicKey)
        try {
            $ecdsa.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
            if ($ecdsa.VerifyData($data, $signature)) { return }
        }
        catch [Security.Cryptography.CryptographicException] { }
        finally { $ecdsa.Dispose(); $publicKey.Dispose() }
    }
    throw 'The release is not signed with a DisplayWeave release key. Nothing was installed.'
}

function Get-OnlineRelease([string]$Destination) {
    # The repository is public: releases are read anonymously and trusted only through their signature.
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $ProgressPreference = 'SilentlyContinue' # the progress bar slows Windows PowerShell downloads several times over
    $headers = @{ 'User-Agent' = 'DisplayWeave-installer' }
    $tag = $Version
    if ([string]::IsNullOrWhiteSpace($tag)) {
        # GitHub's /releases/latest omits prereleases. DisplayWeave previews
        # should update to the highest release version, including previews, as
        # the app's updater does; a final release beats its own previews.
        # prototype-v* tags belong to the separate prototype app.
        $list = Invoke-RestMethod -UseBasicParsing -Headers $headers -Uri "https://api.github.com/repos/$Repository/releases?per_page=100"
        $published = @($list | Where-Object { -not $_.draft -and $_.tag_name -match '^v[0-9]+\.[0-9]+\.[0-9]+' } |
            Sort-Object { [version]($_.tag_name -replace '^v([0-9]+\.[0-9]+\.[0-9]+).*$', '$1') }, { $_.tag_name -notmatch '^v[0-9.]+-' }, published_at -Descending)
        if ($published.Count -eq 0) { throw 'No published DisplayWeave releases were found.' }
        $tag = [string]$published[0].tag_name
    }
    if ($tag -notmatch '^v?[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw "Release tag is not a supported version: $tag"
    }
    if (-not $tag.StartsWith('v')) { $tag = 'v' + $tag }
    $release = Invoke-RestMethod -UseBasicParsing -Headers $headers -Uri "https://api.github.com/repos/$Repository/releases/tags/$tag"
    $packages = @($release.assets | Where-Object { $_.name -match '^DisplayWeave-[A-Za-z0-9.+_-]+\.zip$' })
    if ($packages.Count -ne 1) { throw 'The selected release must contain exactly one DisplayWeave ZIP asset.' }
    $packageName = [string]$packages[0].name
    $assets = foreach ($name in @($packageName, ($packageName + '.sha256'), ($packageName + '.sha256.sig'))) {
        $asset = @($release.assets | Where-Object { $_.name -ceq $name })
        if ($asset.Count -ne 1) { throw "The release is missing $name, so it was not installed." }
        $asset[0]
    }
    foreach ($asset in $assets) { Invoke-WebRequest -UseBasicParsing -Headers $headers -Uri $asset.browser_download_url -OutFile (Join-Path $Destination $asset.name) }
    $checksum = Join-Path $Destination ($packageName + '.sha256')
    Assert-ReleaseSignature $checksum
    return [pscustomobject]@{ Package = (Join-Path $Destination $packageName); Checksum = $checksum; Tag = [string]$release.tag_name }
}

function Get-DisplayWeaveProcesses([string]$Root, [string]$MigrationDirectory) {
    $sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    $result = @()
    foreach ($process in @(Get-Process -Name DisplayWeave -ErrorAction SilentlyContinue)) {
        try {
            if ($process.SessionId -ne $sessionId) { continue }
            $path = $process.Path
            $owned = -not [string]::IsNullOrWhiteSpace($path) -and (Test-UnderRoot $path $Root)
            if (-not $owned -and -not [string]::IsNullOrEmpty($MigrationDirectory) -and -not [string]::IsNullOrWhiteSpace($path)) {
                $owned = [string]::Equals((Get-NormalPath $path), (Join-Path $MigrationDirectory 'DisplayWeave.exe'), [StringComparison]::OrdinalIgnoreCase)
            }
            $result += [pscustomobject]@{ Process = $process; Owned = $owned }
        }
        catch { $result += [pscustomobject]@{ Process = $process; Owned = $false } }
    }
    return $result
}

function Stop-OwnedDisplayWeave([string]$Candidate, [object[]]$Processes) {
    $owned = @($Processes | Where-Object { $_.Owned })
    if ($owned.Count -eq 0) { return }
    if (@($Processes | Where-Object { -not $_.Owned }).Count -gt 0) {
        throw 'Another DisplayWeave installation is running. Close DisplayWeave yourself before updating; no unrelated instance was stopped.'
    }
    $request = Start-Process -FilePath $Candidate -ArgumentList '--exit-for-update' -WindowStyle Hidden -PassThru
    try {
        if (-not $request.WaitForExit(10000)) { throw 'DisplayWeave did not finish its shutdown request. No process was forcibly terminated.' }
        if ($request.ExitCode -ne 0) { throw "DisplayWeave rejected the graceful update shutdown (exit $($request.ExitCode))." }
    }
    finally { $request.Dispose() }
    $deadline = [DateTime]::UtcNow.AddSeconds(15)
    do {
        $remaining = @($owned | Where-Object { -not $_.Process.HasExited })
        if ($remaining.Count -eq 0) { return }
        Start-Sleep -Milliseconds 100
    } while ([DateTime]::UtcNow -lt $deadline)
    throw 'DisplayWeave is still running, possibly waiting to save settings. Close it and retry. No process was forcibly terminated.'
}

$launcherSource = @'
# Stable per-user launcher generated by the DisplayWeave installer.
# Version updates replace current.json; user data stays in data\.
#requires -Version 5.1
param([switch]$Minimized)
$ErrorActionPreference = 'Stop'
try {
    $root = [IO.Path]::GetFullPath($PSScriptRoot)
    $pointerPath = Join-Path $root 'current.json'
    $pointer = [IO.File]::ReadAllText($pointerPath) | ConvertFrom-Json
    if ($pointer.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z.-]+)?$' -or
        $pointer.executable -cne ('versions/' + $pointer.version + '/DisplayWeave.exe') -or $pointer.dataDirectory -cne 'data') {
        throw 'DisplayWeave current.json is invalid. Run the installer again.'
    }
    $exe = Join-Path $root $pointer.executable
    $data = Join-Path $root 'data'
    if (-not [IO.File]::Exists($exe)) { throw 'The selected DisplayWeave version is missing. Run the installer again.' }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.Arguments = '--data-dir "' + $data + '"'
    if ($Minimized) { $info.Arguments += ' --minimized' }
    $process = [Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw 'Windows did not start DisplayWeave.' }
    $process.Dispose()
}
catch { Write-Error $_; exit 1 }
'@

$root = Get-NormalPath $InstallRoot
if ([string]::Equals($root.TrimEnd('\', '/'), ([IO.Path]::GetPathRoot($root)).TrimEnd('\', '/'), [StringComparison]::OrdinalIgnoreCase)) {
    throw 'InstallRoot must be a dedicated folder, not a drive or network-share root.'
}

if ($Uninstall) {
    # Removes only what this installer creates. data\ (layout and rules) stays unless
    # -RemoveSettings. Files still loaded by other apps are deleted at the next sign-in.
    $pointerPath = Join-Path $root 'current.json'
    $versionsDirectory = Join-Path $root 'versions'
    $launcherPath = Join-Path $root 'Launch-DisplayWeave.ps1'
    if (-not [IO.File]::Exists($pointerPath) -and -not [IO.Directory]::Exists($versionsDirectory)) {
        throw "No DisplayWeave installation was found in $root. Nothing was removed."
    }
    Assert-PlainPath $root $true
    $lockPath = Join-Path $root '.install.lock'
    Assert-PlainPath $lockPath $false
    $installLock = $null
    $running = @()
    try {
        try { $installLock = New-Object IO.FileStream($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
        catch { throw 'Another DisplayWeave installer may be running, or the installation directory is not writable.' }
        $running = @(Get-DisplayWeaveProcesses $root '')
        if (@($running | Where-Object { $_.Owned }).Count -gt 0) {
            $executable = @(Get-ChildItem -LiteralPath $versionsDirectory -Filter 'DisplayWeave.exe' -Recurse -File -ErrorAction SilentlyContinue | Select-Object -First 1)
            if ($executable.Count -eq 0) { throw 'DisplayWeave is running but its program files are missing. Exit it from the tray icon and retry.' }
            Stop-OwnedDisplayWeave $executable[0].FullName $running
        }
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey('Software\Microsoft\Windows\CurrentVersion\Run', $true)
        if ($null -ne $key) {
            try {
                # Only a startup entry that launches this installation is removed.
                $value = [string]$key.GetValue('DisplayWeave', '', [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
                if ($value.IndexOf($launcherPath, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $key.DeleteValue('DisplayWeave', $false) }
            }
            finally { $key.Dispose() }
        }
        $files = @($pointerPath, $launcherPath, (Join-Path $root 'DisplayWeave.lnk'))
        if ([string]::Equals($root, (Get-NormalPath (Join-Path $env:LOCALAPPDATA 'DisplayWeave')), [StringComparison]::OrdinalIgnoreCase)) {
            $files += Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) 'DisplayWeave.lnk'
        }
        foreach ($file in $files) {
            Assert-PlainPath $file $false
            if ([IO.File]::Exists($file)) { [IO.File]::Delete($file) }
        }
        $inUse = $false
        if ([IO.Directory]::Exists($versionsDirectory)) {
            Assert-PlainTree $versionsDirectory
            Remove-Item -LiteralPath $versionsDirectory -Recurse -Force -ErrorAction SilentlyContinue
            $inUse = [IO.Directory]::Exists($versionsDirectory)
        }
        $dataDirectory = Join-Path $root 'data'
        if ($RemoveSettings -and [IO.Directory]::Exists($dataDirectory)) {
            Assert-PlainTree $dataDirectory
            Remove-Item -LiteralPath $dataDirectory -Recurse -Force
        }
        $scheduled = $false
        if ($inUse) {
            # Apps keep DisplayWeave's component loaded until they exit; finish at the next sign-in.
            # RunOnce commands are limited to 260 characters, so drop the folder removal if needed.
            $cleanup = 'cmd.exe /d /c rd /s /q "' + $versionsDirectory + '"'
            if (($cleanup + ' & rd "' + $root + '"').Length -le 260) { $cleanup += ' & rd "' + $root + '"' }
            if ($cleanup.Length -le 260) {
                $runOnce = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\RunOnce')
                try { $runOnce.SetValue('DisplayWeaveCleanup', $cleanup, [Microsoft.Win32.RegistryValueKind]::String); $scheduled = $true }
                finally { $runOnce.Dispose() }
            }
        }
        Write-Output "Removed DisplayWeave from $root"
        if ($RemoveSettings) { Write-Output 'Settings: removed' } else { Write-Output "Settings: kept in $dataDirectory" }
        if ($scheduled) { Write-Output 'Some files are still in use by running apps and will be removed at your next sign-in.' }
        elseif ($inUse) { Write-Output "Some files are still in use by running apps. Delete $versionsDirectory after closing them." }
    }
    finally {
        foreach ($entry in $running) { $entry.Process.Dispose() }
        if ($null -ne $installLock) { $installLock.Dispose(); try { [IO.File]::Delete($lockPath) } catch { } }
    }
    if (@([IO.Directory]::EnumerateFileSystemEntries($root)).Count -eq 0) { [IO.Directory]::Delete($root) }
    return
}

$temporaryParent = Get-NormalPath ([IO.Path]::GetTempPath())
$downloadDirectory = $null
$stageDirectory = $null
$installLock = $null
$oldPointer = $null
$oldLauncher = $null
$pointerChanged = $false
$launcherChanged = $false
$startupChanged = $false
$oldStartup = $null
$oldStartupKind = $null
$hadStartup = $false
$shortcutChanges = New-Object 'System.Collections.Generic.List[object]'
$registryPath = 'Software\Microsoft\Windows\CurrentVersion\Run'
$migrationDirectory = $null
$running = @()
$utf8 = New-Object Text.UTF8Encoding($false)

try {
    $startupCommand = $null
    if ($Startup -eq 'Enable') {
        $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $startupLauncher = Join-Path $root 'Launch-DisplayWeave.ps1'
        $startupCommand = '"' + $powershell + '" -NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $startupLauncher + '" -Minimized'
        if ($startupCommand.Length -gt 260) { throw 'Windows Run startup commands are limited to 260 characters. Choose a shorter -InstallRoot or use -Startup Preserve.' }
    }
    if (-not [string]::IsNullOrWhiteSpace($Version) -and $Version -notmatch '^v?[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z.-]+)?$') {
        throw 'Version must be a release version such as v0.4.0-preview.'
    }
    if ([string]::IsNullOrWhiteSpace($PackagePath) -xor [string]::IsNullOrWhiteSpace($ChecksumPath)) {
        throw '-PackagePath and the required -ChecksumPath must be supplied together.'
    }
    if (-not [string]::IsNullOrWhiteSpace($MigrateFrom)) {
        $migrationDirectory = Get-NormalPath $MigrateFrom
        if (-not [IO.Directory]::Exists($migrationDirectory)) { throw "Migration directory does not exist: $migrationDirectory" }
        Assert-PlainPath $migrationDirectory $true
    }
    $requestedVersion = $Version
    if ([string]::IsNullOrWhiteSpace($PackagePath)) {
        $downloadDirectory = Join-Path $temporaryParent ('DisplayWeave-download-' + [Guid]::NewGuid().ToString('N'))
        New-PlainDirectory $downloadDirectory
        $download = Get-OnlineRelease $downloadDirectory
        $PackagePath = $download.Package
        $ChecksumPath = $download.Checksum
        $requestedVersion = $download.Tag
    }
    $PackagePath = Get-NormalPath $PackagePath
    $ChecksumPath = Get-NormalPath $ChecksumPath
    $expectedHash = Get-ExpectedChecksum $ChecksumPath ([IO.Path]::GetFileName($PackagePath))
    New-PlainDirectory $root
    $lockPath = Join-Path $root '.install.lock'
    Assert-PlainPath $lockPath $false
    try { $installLock = New-Object IO.FileStream($lockPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw 'Another DisplayWeave installer may be running, or the installation directory is not writable.' }
    $versionsDirectory = Join-Path $root 'versions'
    $dataDirectory = Join-Path $root 'data'
    $pointerPath = Join-Path $root 'current.json'
    $launcherPath = Join-Path $root 'Launch-DisplayWeave.ps1'
    foreach ($file in @($pointerPath, $launcherPath)) { Assert-PlainPath $file $false }
    if ([IO.File]::Exists($pointerPath)) { $oldPointer = [IO.File]::ReadAllBytes($pointerPath) }
    if ([IO.File]::Exists($launcherPath)) { $oldLauncher = [IO.File]::ReadAllBytes($launcherPath) }
    $stageDirectory = Join-Path $root ('.install-stage-' + [Guid]::NewGuid().ToString('N'))
    Expand-VerifiedPackage $PackagePath $expectedHash $stageDirectory
    $candidateVersion = Read-CandidateVersion $stageDirectory
    if (-not [string]::IsNullOrWhiteSpace($requestedVersion) -and $candidateVersion -cne $requestedVersion.TrimStart('v')) {
        throw "Manifest version $candidateVersion does not match requested release $requestedVersion."
    }
    New-PlainDirectory $versionsDirectory
    $versionDirectory = Join-Path $versionsDirectory $candidateVersion
    if (-not (Test-UnderRoot $stageDirectory $root) -or -not (Test-UnderRoot $versionDirectory $versionsDirectory)) {
        throw 'Refusing a version move outside the installation directories.'
    }
    Assert-PlainPath $versionDirectory $true
    if ([IO.Directory]::Exists($versionDirectory)) {
        Assert-PlainTree $versionDirectory
        $receipt = Join-Path $versionDirectory '.package-sha256'
        if (-not [IO.File]::Exists($receipt) -or [IO.File]::ReadAllText($receipt).Trim() -ne $expectedHash -or
            (Read-CandidateVersion $versionDirectory) -cne $candidateVersion) {
            throw "Version $candidateVersion already exists with different or incomplete contents. Installed versions are never overwritten."
        }
    }
    else {
        [IO.File]::WriteAllText((Join-Path $stageDirectory '.package-sha256'), $expectedHash, $utf8)
        Assert-PlainTree $stageDirectory
        [IO.Directory]::Move($stageDirectory, $versionDirectory)
        $stageDirectory = $null
    }
    $executable = Join-Path $versionDirectory 'DisplayWeave.exe'
    $running = @(Get-DisplayWeaveProcesses $root $migrationDirectory)
    $alreadyCurrent = $false
    if ($null -ne $oldPointer) {
        try {
            $previous = $utf8.GetString($oldPointer) | ConvertFrom-Json
            $alreadyCurrent = $previous.version -ceq $candidateVersion -and $previous.executable -ceq ('versions/' + $candidateVersion + '/DisplayWeave.exe') -and $previous.dataDirectory -ceq 'data'
        }
        catch { $alreadyCurrent = $false }
    }
    if (-not $alreadyCurrent) { Stop-OwnedDisplayWeave $executable $running }
    New-PlainDirectory $dataDirectory
    if ($null -ne $migrationDirectory) {
        foreach ($name in @('layout.xml', 'rules.xml')) {
            $source = Join-Path $migrationDirectory $name
            $destination = Join-Path $dataDirectory $name
            Assert-PlainPath $destination $false
            if ([IO.File]::Exists($source) -and -not [IO.File]::Exists($destination)) {
                Assert-PlainPath $source $false
                [IO.File]::Copy($source, $destination, $false)
            }
        }
    }
    Write-AtomicBytes $launcherPath ($utf8.GetBytes($launcherSource))
    $launcherChanged = $true
    $pointerText = [ordered]@{ version = $candidateVersion; executable = ('versions/' + $candidateVersion + '/DisplayWeave.exe'); dataDirectory = 'data' } | ConvertTo-Json
    Write-AtomicBytes $pointerPath ($utf8.GetBytes($pointerText + [Environment]::NewLine))
    $pointerChanged = $true

    $shortcutPaths = @((Join-Path $root 'DisplayWeave.lnk'))
    $defaultRoot = Get-NormalPath (Join-Path $env:LOCALAPPDATA 'DisplayWeave')
    if ([string]::Equals($root, $defaultRoot, [StringComparison]::OrdinalIgnoreCase)) {
        # A custom/isolated install never changes the user's Start menu.
        $programs = [Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)
        if ([string]::IsNullOrWhiteSpace($programs)) { throw 'Windows did not provide the per-user Start menu directory.' }
        New-PlainDirectory $programs
        $shortcutPaths += Join-Path $programs 'DisplayWeave.lnk'
    }
    foreach ($shortcutPath in $shortcutPaths) {
        Assert-PlainPath $shortcutPath $false
        $oldShortcut = $null
        if ([IO.File]::Exists($shortcutPath)) { $oldShortcut = [IO.File]::ReadAllBytes($shortcutPath) }
        Write-LauncherShortcut $shortcutPath $launcherPath $executable
        $shortcutChanges.Add([pscustomobject]@{ Path = $shortcutPath; Original = $oldShortcut })
    }

    # Preserve performs no registry writes (including on a first installation).
    if ($Startup -ne 'Preserve') {
        $key = [Microsoft.Win32.Registry]::CurrentUser.OpenSubKey($registryPath, $false)
        if ($null -ne $key) {
            try {
                $hadStartup = @($key.GetValueNames()) -contains 'DisplayWeave'
                if ($hadStartup) { $oldStartup = $key.GetValue('DisplayWeave', $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames); $oldStartupKind = $key.GetValueKind('DisplayWeave') }
            }
            finally { $key.Dispose() }
        }
        $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryPath)
        try {
            if ($Startup -eq 'Enable') {
                $key.SetValue('DisplayWeave', $startupCommand, [Microsoft.Win32.RegistryValueKind]::String)
            }
            else { $key.DeleteValue('DisplayWeave', $false) }
            $startupChanged = $true
        }
        finally { $key.Dispose() }
    }

    $unrelatedRunning = @($running | Where-Object { -not $_.Owned -and -not $_.Process.HasExited }).Count -gt 0
    if (-not $NoLaunch -and -not $unrelatedRunning) {
        $powershell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $arguments = '-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File "' + $launcherPath + '"'
        $launch = Start-Process -FilePath $powershell -ArgumentList $arguments -WindowStyle Hidden -PassThru
        try {
            if (-not $launch.WaitForExit(10000)) { throw 'The stable launcher did not finish starting DisplayWeave.' }
            if ($launch.ExitCode -ne 0) { throw 'The stable launcher could not start DisplayWeave.' }
        }
        finally { $launch.Dispose() }
    }
    Write-Output "Installed DisplayWeave $candidateVersion in $versionDirectory"
    Write-Output "Settings: $dataDirectory"
    Write-Output "Launcher: $launcherPath"
    Write-Output "Startup: $Startup"
    if ($unrelatedRunning) { Write-Warning 'Another DisplayWeave installation is running. It was left untouched. Exit it before using the installed launcher.' }
}
catch {
    $failure = $_
    $rollbackFailures = @()
    if ($startupChanged) {
        try {
            $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($registryPath)
            try { if ($hadStartup) { $key.SetValue('DisplayWeave', $oldStartup, $oldStartupKind) } else { $key.DeleteValue('DisplayWeave', $false) } }
            finally { $key.Dispose() }
        }
        catch { $rollbackFailures += 'startup preference: ' + $_.Exception.Message }
    }
    foreach ($shortcutChange in $shortcutChanges) {
        try {
            if ($null -ne $shortcutChange.Original) { Write-AtomicBytes $shortcutChange.Path $shortcutChange.Original }
            else { [IO.File]::Delete($shortcutChange.Path) }
        }
        catch { $rollbackFailures += 'shortcut: ' + $_.Exception.Message }
    }
    if ($pointerChanged) {
        try { if ($null -ne $oldPointer) { Write-AtomicBytes $pointerPath $oldPointer } else { [IO.File]::Delete($pointerPath) } }
        catch { $rollbackFailures += 'version pointer: ' + $_.Exception.Message }
    }
    if ($launcherChanged) {
        try { if ($null -ne $oldLauncher) { Write-AtomicBytes $launcherPath $oldLauncher } else { [IO.File]::Delete($launcherPath) } }
        catch { $rollbackFailures += 'launcher: ' + $_.Exception.Message }
    }
    if ($rollbackFailures.Count -gt 0) { Write-Warning ('Rollback needs attention: ' + ($rollbackFailures -join '; ')) }
    throw $failure
}
finally {
    foreach ($entry in $running) { $entry.Process.Dispose() }
    if ($null -ne $installLock) { $installLock.Dispose() }
    if ($null -ne $stageDirectory) {
        try { Remove-OwnedTemporaryDirectory $stageDirectory $root } catch { Write-Warning ('Temporary extraction cleanup: ' + $_.Exception.Message) }
    }
    if ($null -ne $downloadDirectory) {
        try { Remove-OwnedTemporaryDirectory $downloadDirectory $temporaryParent } catch { Write-Warning ('Temporary download cleanup: ' + $_.Exception.Message) }
    }
}
