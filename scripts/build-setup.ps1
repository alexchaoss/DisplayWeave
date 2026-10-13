# Builds release\DisplayWeave-Setup-<version>.exe with Inno Setup from the portable package made by
# package.ps1 (run package.ps1 first). The payload is baked into the setup; the result is unsigned.
param([string]$PackagePath)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repository 'src\DisplayWeave\version.txt') -Raw).Trim()
if ([string]::IsNullOrWhiteSpace($PackagePath)) { $PackagePath = Join-Path $repository "release\DisplayWeave-$version.zip" }
$PackagePath = [IO.Path]::GetFullPath($PackagePath)
$packageName = [IO.Path]::GetFileName($PackagePath)
if ($packageName -cne "DisplayWeave-$version.zip") { throw "The package must be DisplayWeave-$version.zip to match version.txt." }
$checksumPath = $PackagePath + '.sha256'
foreach ($file in @($PackagePath, $checksumPath)) { if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing $file. Run scripts\package.ps1 first." } }
$expected = ((Get-Content -LiteralPath $checksumPath -Raw).Trim() -split '\s+')[0].ToLowerInvariant()
if ((Get-FileHash -LiteralPath $PackagePath -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected) { throw 'The package does not match its .sha256 file.' }

# Find the Inno Setup command-line compiler (local install or the GitHub runner's preinstalled copy).
$iscc = $env:ISCC
if ([string]::IsNullOrWhiteSpace($iscc) -or -not (Test-Path -LiteralPath $iscc)) {
    $iscc = @('C:\Program Files\Inno Setup 7\ISCC.exe', 'C:\Program Files (x86)\Inno Setup 7\ISCC.exe',
        'C:\Program Files\Inno Setup 6\ISCC.exe', 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe') |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($iscc)) { throw 'Inno Setup (ISCC.exe) was not found. Install Inno Setup 6 or 7, or set $env:ISCC.' }

$outputDirectory = Split-Path -Parent $PackagePath
$script = Join-Path $repository 'installer\DisplayWeave.iss'
$payloadDirectory = Join-Path ([IO.Path]::GetTempPath()) ('DisplayWeave-setup-' + [Guid]::NewGuid().ToString('N'))
Add-Type -AssemblyName System.IO.Compression.FileSystem
try {
    [IO.Compression.ZipFile]::ExtractToDirectory($PackagePath, $payloadDirectory)
    & $iscc /Qp "/DAppVersion=$version" "/DPayloadDir=$payloadDirectory" "/DOutputDir=$outputDirectory" $script
    if ($LASTEXITCODE -ne 0) { throw 'Inno Setup compilation failed.' }
}
finally {
    if (Test-Path -LiteralPath $payloadDirectory) { Remove-Item -LiteralPath $payloadDirectory -Recurse -Force }
}
$output = Join-Path $outputDirectory "DisplayWeave-Setup-$version.exe"
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) { throw "Inno Setup did not produce $output." }
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($output + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($output) + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
Write-Output "Setup: $output"
Write-Output "SHA-256: $hash"
