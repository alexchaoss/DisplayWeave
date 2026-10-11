param(
    [string]$ManagedExecutable,
    [string]$NativeDirectory
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repository = Split-Path -Parent $PSScriptRoot
$appDirectory = Join-Path $repository 'src\DisplayWeave'
$version = (Get-Content -LiteralPath (Join-Path $appDirectory 'version.txt') -Raw).Trim()
if ($version.Length -gt 64 -or $version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') {
    throw 'version.txt must contain a valid release version, such as 0.4.0-preview.'
}
$manifestPath = Join-Path $appDirectory 'release.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ($manifest.version -cne $version) { throw 'release.json and version.txt must have the same version.' }

if ([string]::IsNullOrWhiteSpace($ManagedExecutable)) {
    $ManagedExecutable = Join-Path $repository 'dist\DisplayWeave.exe'
}
if ([string]::IsNullOrWhiteSpace($NativeDirectory)) {
    $NativeDirectory = Join-Path $repository 'dist'
}
$ManagedExecutable = [IO.Path]::GetFullPath($ManagedExecutable)
$NativeDirectory = [IO.Path]::GetFullPath($NativeDirectory)

# An explicit allowlist prevents settings, local reports, and old build output
# from entering an archive. Validate every input before creating a staging area.
$files = @(
    @{ Source = $ManagedExecutable; Destination = 'DisplayWeave.exe' },
    @{ Source = (Join-Path $NativeDirectory 'MinHook-LICENSE.txt'); Destination = 'MinHook-LICENSE.txt' }
)
foreach ($architecture in @('x64', 'x86')) {
    foreach ($binary in @('RegionHook.dll', 'RegionHookHost.exe', 'FullscreenProbe.exe')) {
        $relativePath = "native-v2-$architecture\$binary"
        $files += @{ Source = (Join-Path $NativeDirectory $relativePath); Destination = $relativePath }
    }
}
foreach ($document in @('LICENSE', 'README.md', 'TESTING.md', 'installer\install.ps1', 'src\DisplayWeave\release.json')) {
    $files += @{ Source = (Join-Path $repository $document); Destination = (Split-Path -Leaf $document) }
}
foreach ($file in $files) {
    if (-not (Test-Path -LiteralPath $file.Source -PathType Leaf)) {
        throw "Required package file is missing: $($file.Source). Build the application and both native architectures first."
    }
}

$releaseDirectory = [IO.Path]::GetFullPath((Join-Path $repository 'release'))
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$stagingName = '.staging-' + [Guid]::NewGuid().ToString('N')
$stagingDirectory = Join-Path $releaseDirectory $stagingName
New-Item -ItemType Directory -Path $stagingDirectory | Out-Null

try {
    $payloadDirectory = Join-Path $stagingDirectory 'payload'
    New-Item -ItemType Directory -Path $payloadDirectory | Out-Null
    foreach ($file in $files) {
        $destination = Join-Path $payloadDirectory $file.Destination
        New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($destination)) -Force | Out-Null
        Copy-Item -LiteralPath $file.Source -Destination $destination
    }

    $archiveName = "DisplayWeave-$version.zip"
    $stagedArchive = Join-Path $stagingDirectory $archiveName
    Compress-Archive -Path (Join-Path $payloadDirectory '*') -DestinationPath $stagedArchive
    $sha256 = (Get-FileHash -LiteralPath $stagedArchive -Algorithm SHA256).Hash.ToLowerInvariant()
    $stagedChecksum = $stagedArchive + '.sha256'
    [IO.File]::WriteAllText($stagedChecksum, ($sha256 + '  ' + $archiveName + [Environment]::NewLine), (New-Object Text.UTF8Encoding($false)))

    # Replace only the two named output files after the full archive is ready.
    # Existing extracted release folders are never modified or removed.
    # Antivirus can briefly hold a just-written archive open; retry for up to 15 seconds.
    function Set-Output([string]$Staged, [string]$Final) {
        for ($attempt = 1; ; $attempt++) {
            try {
                if (Test-Path -LiteralPath $Final -PathType Leaf) {
                    [IO.File]::Replace($Staged, $Final, [System.Management.Automation.Language.NullString]::Value)
                } else {
                    [IO.File]::Move($Staged, $Final)
                }
                return
            }
            catch { if ($attempt -ge 30) { throw }; Start-Sleep -Milliseconds 500 }
        }
    }
    $archive = Join-Path $releaseDirectory $archiveName
    Set-Output $stagedArchive $archive
    $checksum = $archive + '.sha256'
    Set-Output $stagedChecksum $checksum
    Write-Output "Portable build: $archive"
    Write-Output "SHA-256: $sha256"
}
finally {
    # Cleanup is restricted to the unique staging directory created by this run.
    $stagingFullPath = [IO.Path]::GetFullPath($stagingDirectory)
    $releasePrefix = $releaseDirectory.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $stagingFullPath.StartsWith($releasePrefix, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($stagingFullPath) -cne $stagingName) {
        throw 'Refusing to remove a staging directory outside the release directory.'
    }
    if (Test-Path -LiteralPath $stagingFullPath -PathType Container) {
        Remove-Item -LiteralPath $stagingFullPath -Recurse -Force
    }
}
