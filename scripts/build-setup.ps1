# Builds release\DisplayWeave-Setup-<version>.exe from the portable package made by
# package.ps1. The setup embeds that ZIP, its .sha256 and installer\install.ps1;
# run package.ps1 first. The result is unsigned.
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

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }
$setupSource = Join-Path $repository 'installer\setup'
$output = Join-Path (Split-Path -Parent $PackagePath) "DisplayWeave-Setup-$version.exe"
& $compiler /nologo /target:winexe /optimize+ /warn:4 /warnaserror+ /reference:System.Windows.Forms.dll /reference:System.Drawing.dll `
    "/win32manifest:$setupSource\setup.manifest" "/resource:$PackagePath,$packageName" "/resource:$checksumPath,$packageName.sha256" `
    "/resource:$(Join-Path $repository 'installer\install.ps1'),install.ps1" "/out:$output" (Join-Path $setupSource 'Setup.cs')
if ($LASTEXITCODE -ne 0) { throw 'Setup build failed.' }
$hash = (Get-FileHash -LiteralPath $output -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($output + '.sha256', $hash + '  ' + [IO.Path]::GetFileName($output) + [Environment]::NewLine, (New-Object Text.UTF8Encoding($false)))
Write-Output "Setup: $output"
Write-Output "SHA-256: $hash"
