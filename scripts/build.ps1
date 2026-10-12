param([string]$OutputDirectory, [string]$NativeDirectory)
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
# Administrator mode copies only these native helpers and checks each copy against its hash, so build the native
# files first (build-native.ps1). Without them the list is empty and administrator mode has no helpers.
if ([string]::IsNullOrWhiteSpace($NativeDirectory)) { $NativeDirectory = $outputDirectory }
$helperList = Join-Path $outputDirectory 'helpers.sha256'
$helpers = foreach ($architecture in @('x64', 'x86')) {
    foreach ($binary in @('RegionHook.dll', 'RegionHookHost.exe', 'FullscreenProbe.exe')) {
        $helper = Join-Path $NativeDirectory "native-v2-$architecture\$binary"
        if (Test-Path -LiteralPath $helper -PathType Leaf) { '{0}  native-v2-{1}/{2}' -f (Get-FileHash -LiteralPath $helper -Algorithm SHA256).Hash.ToLowerInvariant(), $architecture, $binary }
    }
}
[IO.File]::WriteAllText($helperList, ((@($helpers) | ForEach-Object { $_ + "`n" }) -join ''), (New-Object Text.UTF8Encoding($false)))
# Every source in the app folder and its Tests folder, as for the engine, so a new partial file cannot be left out.
$sources = @(@($sourceDirectory, (Join-Path $sourceDirectory 'Tests')) | ForEach-Object { Get-ChildItem -LiteralPath $_ -Filter '*.cs' } | ForEach-Object { $_.FullName })
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /warn:4 "/reference:$core" /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Xml.dll "/win32manifest:$sourceDirectory\app.manifest" "/win32icon:$sourceDirectory\app.ico" "/resource:$sourceDirectory\version.txt,DisplayWeave.version.txt" "/resource:$helperList,DisplayWeave.helpers.sha256" "/out:$outputDirectory\DisplayWeave.exe" $sources
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }
Write-Output "Built $outputDirectory\DisplayWeave.exe and DisplayWeave.Core.dll"
