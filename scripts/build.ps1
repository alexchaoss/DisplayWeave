param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectDirectory = (Split-Path -Parent $PSScriptRoot)
$sourceDirectory = Join-Path $projectDirectory 'src\DisplayWeave'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectDirectory 'dist' }
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
# The engine library has no UI reference (/noconfig drops csc's default WinForms reference),
# so code that needs a UI type cannot compile into it.
$coreSources = @(Get-ChildItem -LiteralPath (Join-Path $projectDirectory 'src\DisplayWeave.Core') -Filter '*.cs' | ForEach-Object { $_.FullName })
$core = Join-Path $outputDirectory 'DisplayWeave.Core.dll'
& $compiler /noconfig /nologo /target:library /platform:x64 /optimize+ /warn:4 /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Xml.dll "/out:$core" $coreSources
if ($LASTEXITCODE -ne 0) { throw 'Core build failed.' }
# Every source in the app folder and its Tests folder, as for the engine, so a new partial file cannot be left out.
$sources = @(@($sourceDirectory, (Join-Path $sourceDirectory 'Tests')) | ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter '*.cs' } | ForEach-Object { $_.FullName })
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warn:4 "/reference:$core" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll "/win32manifest:$sourceDirectory\app.manifest" "/win32icon:$sourceDirectory\app.ico" "/resource:$sourceDirectory\version.txt,DisplayWeave.version.txt" "/out:$outputDirectory\DisplayWeave.exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built $outputDirectory\DisplayWeave.exe and DisplayWeave.Core.dll"
