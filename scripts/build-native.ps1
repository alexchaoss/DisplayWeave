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

# Give the native binaries standard Windows metadata (version info, and an
# asInvoker manifest on the executables) instead of none, like any shipped app.
$version = (Get-Content -LiteralPath (Join-Path $projectDirectory 'src\DisplayWeave\version.txt') -Raw).Trim()
$numeric = @(($version -split '[-+]')[0] -split '\.') + @('0', '0', '0', '0')
$fileVersion = ($numeric[0..3]) -join ','
$resourceDir = Join-Path $projectDirectory '.tools\native-resources'
New-Item -ItemType Directory -Path $resourceDir -Force | Out-Null
Set-Content -LiteralPath (Join-Path $resourceDir 'helper.manifest') -Encoding ascii -Value @"
<?xml version="1.0" encoding="UTF-8" standalone="yes"?>
<assembly xmlns="urn:schemas-microsoft-com:asm.v1" manifestVersion="1.0">
  <assemblyIdentity type="win32" name="DisplayWeave.NativeHelper" version="$(($numeric[0..3]) -join '.')" processorArchitecture="*"/>
  <trustInfo xmlns="urn:schemas-microsoft-com:asm.v3"><security><requestedPrivileges>
    <requestedExecutionLevel level="asInvoker" uiAccess="false"/>
  </requestedPrivileges></security></trustInfo>
</assembly>
"@
function New-VersionResource([string]$Description, [string]$OriginalName, [bool]$Executable) {
    $path = Join-Path $resourceDir ($OriginalName + '.rc')
    $manifestLine = if ($Executable) { '1 24 "helper.manifest"' } else { '' }
    Set-Content -LiteralPath $path -Encoding ascii -Value @"
1 VERSIONINFO
FILEVERSION $fileVersion
PRODUCTVERSION $fileVersion
FILEOS 0x40004L
FILETYPE $(if ($Executable) { '0x1L' } else { '0x2L' })
BEGIN
  BLOCK "StringFileInfo"
  BEGIN
    BLOCK "040904B0"
    BEGIN
      VALUE "CompanyName", "Alexandre St-Pierre"
      VALUE "FileDescription", "$Description"
      VALUE "FileVersion", "$version"
      VALUE "InternalName", "$OriginalName"
      VALUE "LegalCopyright", "Copyright (C) Alexandre St-Pierre"
      VALUE "OriginalFilename", "$OriginalName"
      VALUE "ProductName", "DisplayWeave"
      VALUE "ProductVersion", "$version"
    END
  END
  BLOCK "VarFileInfo"
  BEGIN
    VALUE "Translation", 0x409, 1200
  END
END
$manifestLine
"@
    return $path
}
$hookResource = New-VersionResource 'DisplayWeave region hook' 'RegionHook.dll' $false
$hostResource = New-VersionResource 'DisplayWeave region host' 'RegionHookHost.exe' $true
$probeResource = New-VersionResource 'DisplayWeave display probe' 'FullscreenProbe.exe' $true

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
    & $zig c++ -target $target -std=c++17 -O2 -s -shared (Join-Path $projectDirectory 'native\legacy\RegionHook.cpp') @objectFiles $hookResource -I (Join-Path $minhook 'include') -o (Join-Path $destination 'RegionHook.dll') -luser32 -lgdi32
    if ($LASTEXITCODE -ne 0) { throw "Fullscreen DLL compilation failed ($arch)." }
    }
    # GUI subsystem (no console) while keeping wmain and the stdin/stdout control pipe: a tiny
    # console helper that injects a DLL is a stronger antivirus heuristic than a windowed one.
    & $zig c++ -target $target -std=c++17 -O2 -s -municode '-Wl,--subsystem,windows' (Join-Path $projectDirectory 'native\legacy\HookHost.cpp') $hostResource -o (Join-Path $destination 'RegionHookHost.exe') -luser32 -lshell32 -ladvapi32
    if ($LASTEXITCODE -ne 0) { throw "Fullscreen host compilation failed ($arch)." }
    if (-not $HostOnly) {
        & $zig c++ -target $target -std=c++17 -O2 -s -municode -mwindows '-Wl,--subsystem,windows' (Join-Path $projectDirectory 'native\legacy\FullscreenProbe.cpp') $probeResource -o (Join-Path $destination 'FullscreenProbe.exe') -ld3d11 -ldxgi -lshell32 -luser32 -lgdi32
        if ($LASTEXITCODE -ne 0) { throw "Fullscreen probe compilation failed ($arch)." }
    }
    Write-Output $(if ($HostOnly) { "Built native $arch fullscreen host." } else { "Built native $arch fullscreen engine and DirectX probe." })
}
Copy-Item -LiteralPath (Join-Path $projectDirectory 'third_party\minhook\LICENSE.txt') -Destination (Join-Path $OutputDirectory 'MinHook-LICENSE.txt') -Force
