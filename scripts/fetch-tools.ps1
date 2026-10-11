$ErrorActionPreference = 'Stop'
$toolsDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) '.tools'
New-Item -ItemType Directory -Path $toolsDirectory -Force | Out-Null
$zigDirectory = Join-Path $toolsDirectory 'zig-windows-x86_64-0.13.0'
if (-not (Test-Path -LiteralPath (Join-Path $zigDirectory 'zig.exe'))) {
    # Pinned here, not read from the download server, so a changed archive is always refused.
    $expected = 'd859994725ef9402381e557c60bb57497215682e355204d754ee3df75ee3c158'
    $archive = Join-Path $toolsDirectory 'zig-0.13.0.zip'
    Invoke-WebRequest -Uri 'https://ziglang.org/download/0.13.0/zig-windows-x86_64-0.13.0.zip' -OutFile $archive -TimeoutSec 180
    if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'Compiler checksum mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $toolsDirectory -Force
    Write-Output 'Downloaded Zig 0.13.0; SHA-256 matches the pinned value.'
}
# MinHook v1.3.4 is committed under third_party/minhook with its license.
Write-Output "Portable compiler: $zigDirectory\zig.exe"
