$ErrorActionPreference = 'Stop'
$toolsDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) '.tools'
New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
$zigDirectory = Join-Path $toolsDirectory 'zig-windows-x86_64-0.13.0'
if (-not (Test-Path -LiteralPath (Join-Path $zigDirectory 'zig.exe'))) {
    $index = Invoke-RestMethod -Uri 'https://ziglang.org/download/index.json' -TimeoutSec 60
    $release = $index.'0.13.0'.'x86_64-windows'
    if ($release.tarball -ne 'https://ziglang.org/download/0.13.0/zig-windows-x86_64-0.13.0.zip') { throw 'Unexpected compiler download URL.' }
    $archive = Join-Path $toolsDirectory 'zig-0.13.0.zip'
    Invoke-WebRequest -Uri $release.tarball -OutFile $archive -TimeoutSec 180
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $release.shasum) { throw 'Compiler checksum mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $toolsDirectory -Force
    Write-Output 'Downloaded Zig 0.13.0; SHA-256 verified against ziglang.org.'
}
$dependencyDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) 'third_party'
New-Item -ItemType Directory -Path $dependencyDirectory -Force | Out-Null
$minhookDirectory = Join-Path $dependencyDirectory 'minhook'
if (-not (Test-Path -LiteralPath (Join-Path $minhookDirectory 'include\MinHook.h'))) {
    $hookArchive = Join-Path $toolsDirectory 'minhook-v1.3.4.zip'
    Invoke-WebRequest -Uri 'https://codeload.github.com/TsudaKageyu/minhook/zip/refs/tags/v1.3.4' -OutFile $hookArchive -TimeoutSec 90
    Expand-Archive -LiteralPath $hookArchive -DestinationPath $toolsDirectory -Force
    Copy-Item -LiteralPath (Join-Path $toolsDirectory 'minhook-1.3.4') -Destination $minhookDirectory -Recurse
    Write-Output 'Downloaded MinHook v1.3.4 source with its license.'
}
Write-Output "Portable compiler: $zigDirectory\zig.exe"
