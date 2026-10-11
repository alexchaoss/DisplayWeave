# Packages the separate projection prototype. Explicit allowlist: no legacy
# RegionHook binaries, settings, reports or the upstream display driver (the
# installer downloads and verifies that pinned package itself).
param([string]$BuildDirectory)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repository = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($BuildDirectory)) { $BuildDirectory = Join-Path $repository 'dist\prototype' }
$BuildDirectory = [IO.Path]::GetFullPath($BuildDirectory)
$version = (Get-Content -LiteralPath (Join-Path $repository 'src\DisplayWeave.Prototype\version.txt') -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') { throw 'Invalid prototype version.' }
$built = (Get-Content -LiteralPath (Join-Path $BuildDirectory 'version.txt') -Raw).Trim()
if ($built -cne $version) { throw "Build folder holds $built, expected $version. Rebuild the prototype first." }

$files = @(
    @{ Source = (Join-Path $BuildDirectory 'DisplayWeavePrototype.exe'); Destination = 'DisplayWeavePrototype.exe' },
    @{ Source = (Join-Path $BuildDirectory 'projection-x64\RegionPresenter.exe'); Destination = 'projection-x64\RegionPresenter.exe' },
    @{ Source = (Join-Path $BuildDirectory 'projection-x64\RegionDisplayLease.exe'); Destination = 'projection-x64\RegionDisplayLease.exe' },
    @{ Source = (Join-Path $BuildDirectory 'test-virtual-display.ps1'); Destination = 'test-virtual-display.ps1' },
    @{ Source = (Join-Path $BuildDirectory 'version.txt'); Destination = 'version.txt' },
    @{ Source = (Join-Path $repository 'installer\install-prototype.ps1'); Destination = 'install-prototype.ps1' },
    @{ Source = (Join-Path $repository 'docs\prototype-guide.md'); Destination = 'README.md' },
    @{ Source = (Join-Path $repository 'src\DisplayWeave.Prototype\THIRD-PARTY-NOTICES.txt'); Destination = 'THIRD-PARTY-NOTICES.txt' }
)
foreach ($file in $files) { if (-not (Test-Path -LiteralPath $file.Source -PathType Leaf)) { throw "Required package file is missing: $($file.Source)" } }

$releaseDirectory = Join-Path $repository 'release'
New-Item -ItemType Directory -Path $releaseDirectory -Force | Out-Null
$staging = Join-Path $releaseDirectory ('.staging-' + [Guid]::NewGuid().ToString('N'))
try {
    $payload = Join-Path $staging 'payload'
    $hashes = New-Object Collections.Generic.List[string]
    foreach ($file in $files) {
        $destination = Join-Path $payload $file.Destination
        New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
        Copy-Item -LiteralPath $file.Source -Destination $destination
        if ($file.Destination -ne 'install-prototype.ps1') {
            $hashes.Add((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $file.Destination.Replace('\', '/'))
        }
    }
    # The installer verifies every payload file against this list before copying.
    [IO.File]::WriteAllLines((Join-Path $payload 'payload.sha256'), $hashes, (New-Object Text.UTF8Encoding($false)))
    $name = "DisplayWeavePrototype-$version.zip"
    $archive = Join-Path $staging $name
    Compress-Archive -Path (Join-Path $payload '*') -DestinationPath $archive
    $sha = (Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText($archive + '.sha256', "$sha  $name`n", (New-Object Text.UTF8Encoding($false)))
    foreach ($output in @($name, "$name.sha256")) { Move-Item -LiteralPath (Join-Path $staging $output) -Destination (Join-Path $releaseDirectory $output) -Force }
    Write-Output "Prototype package: $(Join-Path $releaseDirectory $name)"
    Write-Output "SHA-256: $sha"
}
finally {
    if ((Split-Path -Leaf $staging) -like '.staging-*' -and (Test-Path -LiteralPath $staging)) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
