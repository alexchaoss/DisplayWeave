param([ValidateSet('x64', 'x86', 'all')][string]$Architecture = 'all', [switch]$HostOnly, [string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectDirectory = (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectDirectory 'dist' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$zig = Join-Path $projectDirectory '.tools\zig-windows-x86_64-0.13.0\zig.exe'
if (-not (Test-Path -LiteralPath $zig)) { throw 'Run fetch-tools.ps1 to download the portable compiler first.' }
# Links use -s: debug info would embed the local build folder path in published binaries.
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $projectDirectory '.tools\zig-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $projectDirectory '.tools\zig-local-cache'
$architectures = if ($Architecture -eq 'all') { @('x64', 'x86') } else { @($Architecture) }
foreach ($arch in $architectures) {
    $target = if ($arch -eq 'x64') { 'x86_64-windows-gnu' } else { 'x86-windows-gnu' }
    $decoder = if ($arch -eq 'x64') { 'hde64' } else { 'hde32' }
    $destination = Join-Path $OutputDirectory "native-v2-$arch"
    $objects = Join-Path $projectDirectory ".tools\objects-$arch"
    New-Item -ItemType Directory -Path $destination,$objects -Force | Out-Null
    $minhook = Join-Path $projectDirectory 'third_party\minhook'
    $objectFiles = @()
    if (-not $HostOnly) {
    foreach ($source in @('buffer','hook','trampoline',"hde\$decoder")) {
        $objectFile = Join-Path $objects ((Split-Path $source -Leaf) + '.o')
        & $zig cc -target $target -O2 -c (Join-Path $minhook "src\$source.c") -I (Join-Path $minhook 'include') -o $objectFile
        if ($LASTEXITCODE -ne 0) { throw "MinHook compilation failed: $source ($arch)" }
        $objectFiles += $objectFile
    }
    & $zig c++ -target $target -std=c++17 -O2 -s -shared (Join-Path $projectDirectory 'native\legacy\RegionHook.cpp') @objectFiles -I (Join-Path $minhook 'include') -o (Join-Path $destination 'RegionHook.dll') -luser32 -lgdi32
    if ($LASTEXITCODE -ne 0) { throw "Fullscreen DLL compilation failed ($arch)." }
    }
    & $zig c++ -target $target -std=c++17 -O2 -s -municode (Join-Path $projectDirectory 'native\legacy\HookHost.cpp') -o (Join-Path $destination 'RegionHookHost.exe') -luser32 -lshell32 -ladvapi32
    if ($LASTEXITCODE -ne 0) { throw "Fullscreen host compilation failed ($arch)." }
    if (-not $HostOnly) {
        & $zig c++ -target $target -std=c++17 -O2 -s -municode -mwindows (Join-Path $projectDirectory 'native\legacy\FullscreenProbe.cpp') -o (Join-Path $destination 'FullscreenProbe.exe') -ld3d11 -ldxgi -lshell32 -luser32 -lgdi32
        if ($LASTEXITCODE -ne 0) { throw "Fullscreen probe compilation failed ($arch)." }
    }
    Write-Output $(if ($HostOnly) { "Built native $arch fullscreen host." } else { "Built native $arch fullscreen engine and DirectX probe." })
}
Copy-Item -LiteralPath (Join-Path $projectDirectory 'third_party\minhook\LICENSE.txt') -Destination (Join-Path $OutputDirectory 'MinHook-LICENSE.txt') -Force
