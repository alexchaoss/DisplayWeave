#requires -Version 5.1
# Only hidden copies of our rendering probe are launched; never a real launcher.
param([string]$BuildDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'))
$ErrorActionPreference = 'Stop'
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$output = Join-Path (Split-Path -Parent $PSScriptRoot) ('test-results\compatibility-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $output -Force | Out-Null
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class RegionCompatibilityFixture {
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint pid);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
}
'@
function Assert-NoMapping([int]$ProcessId) {
    $mapping = $null
    try { $mapping = [IO.MemoryMappedFiles.MemoryMappedFile]::OpenExisting("Local\DisplayWeave_$ProcessId") }
    catch [IO.FileNotFoundException] { return }
    finally { if ($null -ne $mapping) { $mapping.Dispose() } }
    throw 'The exclusion created or reused a control mapping.'
}
$passed = 0
foreach ($arch in @('x64','x86')) {
    $directory = Join-Path $output $arch
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    foreach ($name in @('Battle.net.exe','Battle.net Launcher.exe','Battle.net Helper.exe')) {
        $fixturePath = Join-Path $directory $name
        Copy-Item -LiteralPath (Join-Path $build "native-v2-$arch\FullscreenProbe.exe") -Destination $fixturePath
        $report = $fixturePath + '.json'
        $fixture = $null; $helper = $null; $window = [IntPtr]::Zero
        try {
            $fixture = Start-Process -FilePath $fixturePath -ArgumentList ('--report "' + $report + '"') -WindowStyle Hidden -PassThru
            $deadline = [DateTime]::UtcNow.AddSeconds(12)
            while (-not [IO.File]::Exists($report + '.ready.json')) {
                if ($fixture.HasExited -or [DateTime]::UtcNow -gt $deadline) { throw 'The hidden test fixture did not initialize.' }
                Start-Sleep -Milliseconds 50
            }
            $ready = [IO.File]::ReadAllText($report + '.ready.json') | ConvertFrom-Json
            $window = [IntPtr]([long]$ready.hwnd)
            [uint32]$owner = 0
            [void][RegionCompatibilityFixture]::GetWindowThreadProcessId($window, [ref]$owner)
            if ($ready.pid -ne $fixture.Id -or $owner -ne $fixture.Id -or $fixture.MainModule.FileName -ine $fixturePath) { throw 'Fixture identity mismatch.' }
            if ([RegionCompatibilityFixture]::IsWindowVisible($window)) { throw 'Fixture must remain hidden to avoid automatic handling by an older installed controller.' }
            Assert-NoMapping $fixture.Id
            foreach ($mode in @('', ' --auto')) {
                $info = New-Object Diagnostics.ProcessStartInfo
                $info.FileName = Join-Path $build "native-v2-$arch\RegionHookHost.exe"
                $info.Arguments = "$($fixture.Id) $($ready.hwnd) 240 180 960 540$mode"
                $info.UseShellExecute = $false; $info.CreateNoWindow = $true
                $info.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
                $info.RedirectStandardOutput = $true; $info.RedirectStandardError = $true
                $helper = [Diagnostics.Process]::Start($info)
                $stdout = $helper.StandardOutput.ReadToEndAsync(); $stderr = $helper.StandardError.ReadToEndAsync()
                if (-not $helper.WaitForExit(10000)) { throw 'Exclusion helper timed out.' }
                $text = $stdout.Result + $stderr.Result
                if ($helper.ExitCode -eq 0 -or $text -notmatch 'Battle.net launcher is excluded' -or $text -notmatch 'Windows error 50') { throw ('Launcher exclusion failed: ' + $text) }
                $helper.Dispose(); $helper = $null
                if ($fixture.HasExited) { throw 'Excluded fixture exited unexpectedly.' }
                $fixture.Refresh()
                if (@($fixture.Modules | Where-Object ModuleName -IEQ 'RegionHook.dll').Count -ne 0) { throw 'Excluded fixture has a DisplayWeave DLL loaded.' }
                Assert-NoMapping $fixture.Id
                $passed++
            }
        }
        finally {
            if ($null -ne $helper) { if (-not $helper.HasExited) { $helper.Kill(); $helper.WaitForExit() }; $helper.Dispose() }
            if ($null -ne $fixture) {
                if (-not $fixture.HasExited) {
                    [uint32]$owner = 0
                    [void][RegionCompatibilityFixture]::GetWindowThreadProcessId($window, [ref]$owner)
                    if ($owner -eq $fixture.Id -and $fixture.MainModule.FileName -ieq $fixturePath) {
                        [void][RegionCompatibilityFixture]::PostMessage($window, 0x8004, [IntPtr]::Zero, [IntPtr]::Zero)
                    }
                    if (-not $fixture.WaitForExit(3000)) { $fixture.Kill(); $fixture.WaitForExit() }
                }
                $fixture.Dispose()
            }
        }
    }
}
$summary = "PASS $passed native launcher exclusions: x64/x86, automatic/manual, no DisplayWeave DLL or mapping; real launchers untouched."
$summary | Set-Content -LiteralPath (Join-Path $output 'results.txt') -Encoding UTF8
Write-Output $summary
Write-Output "Reports: $output"
