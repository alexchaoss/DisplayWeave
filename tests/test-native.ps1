param([ValidateSet('x64', 'x86', 'all')][string]$Architecture = 'all', [switch]$ShowApp, [switch]$UiOnly, [switch]$Automatic, [string]$AutomaticExecutableFilter, [switch]$ManagedController, [switch]$ReopenUi,
    [string]$BuildDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) 'dist'), [string]$HookBuildDirectory,
    [switch]$FilteredMessages, [switch]$ExpectStartupTimeout)
$ErrorActionPreference = 'Stop'
$BuildDirectory = (Resolve-Path -LiteralPath $BuildDirectory).Path
if (!$HookBuildDirectory) { $HookBuildDirectory = $BuildDirectory }
$HookBuildDirectory = (Resolve-Path -LiteralPath $HookBuildDirectory).Path
if ($FilteredMessages -and ($ManagedController -or $ShowApp -or $UiOnly)) { throw 'Filtered-message checks require the isolated native fixture mode.' }
if ($ExpectStartupTimeout -and !$FilteredMessages) { throw 'Expected timeout is only valid for the filtered-message regression fixture.' }
$architectures = if ($Architecture -eq 'all') { @('x64', 'x86') } else { @($Architecture) }
# ManagedController uses its own isolated message loop and fixture scope. Do not
# open the ordinary UI alongside it, even when the caller supplied -ShowApp.
if ($ShowApp -and -not $ManagedController -and ($ReopenUi -or -not (Get-Process -Name DisplayWeave -ErrorAction SilentlyContinue))) {
    if ($AutomaticExecutableFilter) {
        $filterPath = (Resolve-Path -LiteralPath $AutomaticExecutableFilter).Path
        Start-Process -FilePath (Join-Path $BuildDirectory 'DisplayWeave.exe') -ArgumentList ('--auto-test-executable "' + $filterPath + '"')
    } else {
        Start-Process -FilePath (Join-Path $BuildDirectory 'DisplayWeave.exe')
    }
}
if ($UiOnly -and -not $ManagedController) {
    Write-Output 'Opened the interface for manual inspection; no native test was run.'
    return
}
$modeName = if ($Automatic -or $ManagedController) { 'AUTO' } else { 'FORCE' }
$testPrefix = if ($ManagedController) { 'test-results\managed-auto-' } elseif ($Automatic) { 'test-results\native-auto-' } else { 'test-results\native-' }
$testDirectory = Join-Path (Split-Path -Parent $PSScriptRoot) ($testPrefix + (Get-Date -Format 'yyyyMMdd-HHmmss-fff'))
New-Item -ItemType Directory -Path $testDirectory -Force | Out-Null
foreach ($arch in $architectures) {
    $binaryDirectory = Join-Path $BuildDirectory "native-v2-$arch"
    $report = Join-Path $testDirectory "$arch.json"
    $probe = $null
    $hookHost = $null
    $managed = $null
    try {
        if ($ManagedController) {
            $managedStart = New-Object System.Diagnostics.ProcessStartInfo
            $managedStart.FileName = Join-Path $BuildDirectory 'DisplayWeave.exe'
            $fixturePath = Join-Path $binaryDirectory 'FullscreenProbe.exe'
            $managedStart.Arguments = '--verify-auto-controller "' + $fixturePath + '" "' + $report + '"'
            $managedStart.UseShellExecute = $false
            $managedStart.CreateNoWindow = $true
            $managedStart.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
            $managed = [System.Diagnostics.Process]::Start($managedStart)
            if (-not $managed.WaitForExit(45000)) { throw 'The isolated automatic-controller test timed out.' }
            if (-not (Test-Path -LiteralPath $report)) { throw "The managed controller did not save its fixture report; see $report.controller.txt" }
            $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
            Write-Output ($result | ConvertTo-Json -Depth 5 -Compress)
            if ($managed.ExitCode -ne 0 -or -not $result.success -or $result.mode -ne 'AUTO') {
                throw "The $arch managed automatic-controller verification failed; see $report and $report.controller.txt"
            }
            Write-Output "PASS $arch automatic discovery, region selection, and restoration through FullscreenController. Report: $report"
            continue
        }
        $probeStart = New-Object System.Diagnostics.ProcessStartInfo
        $probeStart.FileName = Join-Path $binaryDirectory 'FullscreenProbe.exe'
        $verifyArgument = if ($Automatic) { '--verify-auto' } else { '--verify-region' }
        $probeStart.Arguments = $verifyArgument + ' 240 180 960 540 --delay-ms 5000 --report "' + $report + '"'
        if ($FilteredMessages) { $probeStart.Arguments += ' --sent-messages-only' }
        $probeStart.UseShellExecute = $false
        $probeStart.CreateNoWindow = $true
        # This is the explicitly authorized visible test window; only the helper
        # and console stay hidden. Hidden targets are intentionally not managed.
        $probeStart.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Normal
        $probe = [System.Diagnostics.Process]::Start($probeStart)
        $deadline = [DateTime]::UtcNow.AddSeconds(12)
        while (-not (Test-Path -LiteralPath ($report + '.ready.json'))) {
            if ($probe.HasExited) { throw "The $arch test application exited before initialization (exit $($probe.ExitCode))." }
            if ([DateTime]::UtcNow -gt $deadline) { throw 'The DirectX test application did not initialize.' }
            Start-Sleep -Milliseconds 100
        }
        $ready = Get-Content -LiteralPath ($report + '.ready.json') -Raw | ConvertFrom-Json
        if ($ready.pid -ne $probe.Id) { throw 'The test report does not belong to the launched process.' }
        $hostStart = New-Object System.Diagnostics.ProcessStartInfo
        $hostStart.FileName = Join-Path $HookBuildDirectory "native-v2-$arch\RegionHookHost.exe"
        $hostStart.Arguments = "$($ready.pid) $($ready.hwnd) 240 180 960 540"
        if ($Automatic) { $hostStart.Arguments += ' --auto' }
        $hostStart.UseShellExecute = $false
        $hostStart.CreateNoWindow = $true
        $hostStart.WindowStyle = [System.Diagnostics.ProcessWindowStyle]::Hidden
        $hostStart.RedirectStandardInput = $true
        $hostStart.RedirectStandardOutput = $true
        $hostStart.RedirectStandardError = $true
        $hookHost = [System.Diagnostics.Process]::Start($hostStart)
        $output = $hookHost.StandardOutput.ReadToEndAsync()
        $errors = $hookHost.StandardError.ReadToEndAsync()
        if ($ExpectStartupTimeout) {
            if (-not $hookHost.WaitForExit(14500)) { throw 'Baseline helper exceeded its bounded startup wait.' }
            $log = $output.Result + $errors.Result
            [IO.File]::WriteAllText((Join-Path $testDirectory "$arch-host.txt"), $log)
            if ($hookHost.ExitCode -eq 0 -or $log -notmatch 'Windows error 1460\)' -or $log -match '(?m)^READY ') {
                throw ('The old helper did not reproduce the filtered-message timeout: ' + $log)
            }
            if ($probe.HasExited) { throw 'Baseline fixture exited unexpectedly.' }
            Write-Output "PASS $arch old helper reproduced startup timeout1460 on the filtered-message fixture. Report: $testDirectory"
            continue
        }
        $deadline = [DateTime]::UtcNow.AddSeconds(30)
        $offSent = $false
        while (-not $probe.HasExited) {
            if (-not $offSent -and (Test-Path -LiteralPath ($report + '.await-release.json'))) {
                if ($hookHost.HasExited) { throw 'The hook helper exited before restoration testing.' }
                $hookHost.StandardInput.WriteLine('OFF')
                $hookHost.StandardInput.Flush()
                $offSent = $true
            }
            if ([DateTime]::UtcNow -gt $deadline) { throw 'The DirectX test timed out.' }
            Start-Sleep -Milliseconds 100
        }
        if (-not $hookHost.HasExited) {
            $hookHost.StandardInput.WriteLine('QUIT')
            $hookHost.StandardInput.Flush()
            if (-not $hookHost.WaitForExit(3000)) { throw 'The hook helper did not stop.' }
        }
        [System.IO.File]::WriteAllText((Join-Path $testDirectory "$arch-host.txt"), $output.Result + $errors.Result)
        if (Test-Path -LiteralPath $report) {
            $result = Get-Content -LiteralPath $report -Raw | ConvertFrom-Json
            Write-Output ($result | ConvertTo-Json -Depth 5 -Compress)
        } else { throw "No DirectX report was saved. Host output: $($output.Result)" }
        if ($probe.ExitCode -ne 0 -or -not $result.success -or $result.mode -ne $modeName) { throw "The $arch $modeName verification failed; see $report" }
        Write-Output "PASS $arch $modeName fullscreen interception and restoration. Report: $report"
    } finally {
        if ($managed) {
            if (-not $managed.HasExited) {
                # This is only the test process launched above. Its native helper
                # restores the fixture if the controller heartbeat stops.
                $null = $managed.CloseMainWindow()
                if (-not $managed.WaitForExit(2500)) { $managed.Kill() }
            }
            $managed.Dispose()
        }
        if ($hookHost -and -not $hookHost.HasExited) {
            try { $hookHost.StandardInput.WriteLine('QUIT'); $hookHost.StandardInput.Flush() } catch { }
            if (-not $hookHost.WaitForExit(2500)) { $hookHost.Kill() }
        }
        if ($probe -and -not $probe.HasExited) {
            $null = $probe.CloseMainWindow()
            if (-not $probe.WaitForExit(2000)) { $probe.Kill() }
        }
    }
}
