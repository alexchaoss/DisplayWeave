# Builds the separate projection prototype (UI/worker plus x64 projection
# tools) into one folder laid out like an installed version. It does not
# include legacy RegionHook binaries or the upstream display driver.
# -ProjectionDirectory reuses an existing projection-x64 build (CI builds it first).
param([string]$OutputDirectory, [switch]$RunSelfTests, [string]$ProjectionDirectory)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$source = Join-Path $repository 'src\DisplayWeave.Prototype'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $repository 'dist\prototype' }
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$version = (Get-Content -LiteralPath (Join-Path $source 'version.txt') -Raw).Trim()
if ((Get-Content -LiteralPath (Join-Path $source 'PrototypeApp.cs') -Raw) -notmatch ('Version = "' + [regex]::Escape($version) + '"')) {
    throw 'PrototypeApp.cs Program.Version must match src\DisplayWeave.Prototype\version.txt.'
}
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null

$sources = @((Get-ChildItem -LiteralPath $source -Filter '*.cs').FullName) + (Join-Path $repository 'src\DisplayWeave.Core\Layout.cs')
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    /reference:System.Xml.dll /reference:System.Web.Extensions.dll /reference:System.Core.dll "/win32manifest:$source\app.manifest" `
    "/out:$OutputDirectory\DisplayWeavePrototype.exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'Prototype build failed.' }

# Projection tools come from the existing build; only the two runtime binaries ship.
$projection = Join-Path $OutputDirectory '.projection-build'
if ($ProjectionDirectory) { $built = (Resolve-Path -LiteralPath $ProjectionDirectory).Path }
else {
    & (Join-Path $PSScriptRoot 'build-projection.ps1') -Architecture x64 -OutputDirectory $projection -RunSelfTests:$RunSelfTests | Out-Host
    $built = Join-Path $projection 'projection-x64'
}
$runtime = Join-Path $OutputDirectory 'projection-x64'
New-Item -ItemType Directory -Path $runtime -Force | Out-Null
foreach ($binary in @('RegionPresenter.exe', 'RegionDisplayLease.exe')) { Copy-Item -LiteralPath (Join-Path $built $binary) -Destination $runtime -Force }
if (Test-Path -LiteralPath $projection) { Remove-Item -LiteralPath $projection -Recurse -Force }
Copy-Item -LiteralPath (Join-Path $repository 'tests\test-virtual-display.ps1') -Destination $OutputDirectory -Force
Copy-Item -LiteralPath (Join-Path $source 'version.txt') -Destination $OutputDirectory -Force

if ($RunSelfTests) {
    $report = Join-Path $env:TEMP ('DisplayWeavePrototype-selftest-' + [Guid]::NewGuid().ToString('N') + '.txt')
    $test = Start-Process -FilePath (Join-Path $OutputDirectory 'DisplayWeavePrototype.exe') -ArgumentList @('--self-test', ('"{0}"' -f $report)) -WindowStyle Hidden -Wait -PassThru
    $summary = Get-Content -LiteralPath $report | Select-Object -Last 1
    Get-Content -LiteralPath $report | Where-Object { $_ -like 'FAIL *' } | Write-Output
    Remove-Item -LiteralPath $report -Force
    if ($test.ExitCode -ne 0) { throw "Prototype self-tests failed: $summary" }
    Write-Output "Prototype self-tests: $summary"
}
Write-Output "Built prototype $version in $OutputDirectory"
