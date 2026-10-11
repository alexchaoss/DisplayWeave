param([string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
$projectDirectory = (Split-Path -Parent $PSScriptRoot)
$sourceDirectory = Join-Path $projectDirectory 'src\DisplayWeave'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw 'The Windows .NET Framework C# compiler was not found.' }
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) { $OutputDirectory = Join-Path $projectDirectory 'dist' }
$outputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
$sources = @('Program.cs', 'AppOptions.cs', 'ElevationRestart.cs', 'StartupRegistration.cs', 'Tests\LifecycleTests.cs', 'MainForm.cs', 'MainForm.Interface.cs', 'RulerCanvas.cs', 'Layout.cs', 'RulerLayout.cs', 'Tests\LayoutTests.cs', 'WindowManager.cs', 'Updater.cs', 'ApplicationRules.cs', 'FullscreenController.cs', 'Tests\RuleTests.cs', 'AutomaticRegionPolicy.cs', 'Tests\AutomaticRegionTests.cs', 'Tests\AutomaticControllerIntegration.cs') | ForEach-Object { Join-Path $sourceDirectory $_ }
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warn:4 /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll "/win32manifest:$sourceDirectory\app.manifest" "/out:$outputDirectory\DisplayWeave.exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built $outputDirectory\DisplayWeave.exe"
