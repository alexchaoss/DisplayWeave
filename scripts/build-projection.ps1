param(
    [ValidateSet('x64', 'x86', 'all')][string]$Architecture = 'all',
    [string]$OutputDirectory,
    [switch]$RunSelfTests
)
$ErrorActionPreference = 'Stop'
$projectDirectory = (Split-Path -Parent $PSScriptRoot)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectDirectory 'dist' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$zig = Join-Path $projectDirectory '.tools\zig-windows-x86_64-0.13.0\zig.exe'
if (-not (Test-Path -LiteralPath $zig)) { throw 'Run fetch-tools.ps1 to download the portable compiler first.' }
# Links use -s: debug info would embed the local build folder path in published binaries.
$env:ZIG_GLOBAL_CACHE_DIR = Join-Path $projectDirectory '.tools\zig-cache'
$env:ZIG_LOCAL_CACHE_DIR = Join-Path $projectDirectory '.tools\zig-projection-cache'
$architectures = if ($Architecture -eq 'all') { @('x64', 'x86') } else { @($Architecture) }
foreach ($arch in $architectures) {
    $target = if ($arch -eq 'x64') { 'x86_64-windows-gnu' } else { 'x86-windows-gnu' }
    $destination = Join-Path $OutputDirectory "projection-$arch"
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $executable = Join-Path $destination 'RegionPresenter.exe'
    & $zig c++ -target $target -std=c++17 -O2 -s -Wall -Wextra -municode (Join-Path $projectDirectory 'native\projection\RegionPresenter.cpp') (Join-Path $projectDirectory 'native\projection\ProjectionInput.cpp') -o $executable -ld3d11 -ldxgi -ld3dcompiler_47 -luser32 -lgdi32 -lole32 -ladvapi32
    if ($LASTEXITCODE -ne 0) { throw "Projection presenter compilation failed ($arch)." }
    $probe = Join-Path $destination 'ProjectionProbe.exe'
    & $zig c++ -target $target -std=c++17 -O2 -s -municode (Join-Path $projectDirectory 'native\projection\ProjectionProbe.cpp') -o $probe -ld3dcompiler_47 -ld3d11 -ldxgi -lshell32 -luser32 -lgdi32
    if ($LASTEXITCODE -ne 0) { throw "Projection probe compilation failed ($arch)." }
    $lease = Join-Path $destination 'RegionDisplayLease.exe'
    & $zig c++ -target $target -std=c++17 -O2 -s -municode (Join-Path $projectDirectory 'native\projection\VirtualDisplayLease.cpp') -o $lease -luser32 -lcfgmgr32 -ladvapi32
    if ($LASTEXITCODE -ne 0) { throw "Virtual display lease helper compilation failed ($arch)." }
    $planTests = Join-Path $destination 'ProjectionPlanTests.exe'
    & $zig c++ -target $target -std=c++17 -O2 -s -Wall -Wextra -Werror (Join-Path $projectDirectory 'native\projection\ProjectionPlanTests.cpp') -o $planTests
    if ($LASTEXITCODE -ne 0) { throw "Projection plan tests compilation failed ($arch)." }
    $inputTests = Join-Path $destination 'ProjectionInputTests.exe'
    & $zig c++ -target $target -std=c++17 -O2 -s -Wall -Wextra -Werror (Join-Path $projectDirectory 'native\projection\ProjectionInputTests.cpp') (Join-Path $projectDirectory 'native\projection\ProjectionInput.cpp') -o $inputTests -luser32 -ladvapi32 -lole32
    if ($LASTEXITCODE -ne 0) { throw "Projection input tests compilation failed ($arch)." }
    if ($RunSelfTests) {
        & $executable --self-test
        if ($LASTEXITCODE -ne 0) { throw "Projection policy self-tests failed ($arch)." }
        & $lease --self-test
        if ($LASTEXITCODE -ne 0) { throw "Virtual display lease self-tests failed ($arch)." }
        & $planTests
        if ($LASTEXITCODE -ne 0) { throw "Projection plan self-tests failed ($arch)." }
        & $inputTests
        if ($LASTEXITCODE -ne 0) { throw "Projection input self-tests failed ($arch)." }
    }
    Write-Output "Built $executable"
}
