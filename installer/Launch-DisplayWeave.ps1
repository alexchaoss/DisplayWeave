# Stable per-user launcher; the DisplayWeave installer copies it from the package to the installation root.
# Version updates replace current.json; user data stays in data\.
#requires -Version 5.1
param([switch]$Minimized)
$ErrorActionPreference = 'Stop'
try {
    $root = [IO.Path]::GetFullPath($PSScriptRoot)
    $pointerPath = Join-Path $root 'current.json'
    $pointer = [IO.File]::ReadAllText($pointerPath) | ConvertFrom-Json
    if ($pointer.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?(?:\+[0-9A-Za-z.-]+)?$' -or
        $pointer.executable -cne ('versions/' + $pointer.version + '/DisplayWeave.exe') -or $pointer.dataDirectory -cne 'data') {
        throw 'DisplayWeave current.json is invalid. Run the installer again.'
    }
    $exe = Join-Path $root $pointer.executable
    $data = Join-Path $root 'data'
    if (-not [IO.File]::Exists($exe)) { throw 'The selected DisplayWeave version is missing. Run the installer again.' }
    # A version that stopped twice without running a minute or exiting cleanly (the app then writes
    # health\<version>.ok) goes back to the version it replaced. Bookkeeping problems never stop the launch.
    try {
        $previous = [string]$pointer.previousVersion
        $health = Join-Path $root 'health'
        if ($previous -match '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$' -and $previous -cne $pointer.version -and
            -not [IO.File]::Exists((Join-Path $health ($pointer.version + '.ok')))) {
            $starts = Join-Path $health ($pointer.version + '.starts')
            $previousExe = Join-Path $root ('versions/' + $previous + '/DisplayWeave.exe')
            if ([IO.File]::Exists($starts) -and @([IO.File]::ReadAllLines($starts)).Count -ge 2 -and [IO.File]::Exists($previousExe)) {
                $text = [ordered]@{ version = $previous; executable = ('versions/' + $previous + '/DisplayWeave.exe'); dataDirectory = 'data' } | ConvertTo-Json
                $temporary = $pointerPath + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
                [IO.File]::WriteAllText($temporary, $text + "`r`n", (New-Object Text.UTF8Encoding($false)))
                [IO.File]::Replace($temporary, $pointerPath, [NullString]::Value)
                [IO.File]::WriteAllText((Join-Path $health ($pointer.version + '.failed')), "Stopped twice within a minute of starting.`r`n")
                $exe = $previousExe
            }
            else {
                [void][IO.Directory]::CreateDirectory($health)
                [IO.File]::AppendAllText($starts, (Get-Date -Format o) + "`r`n")
            }
        }
    }
    catch { }
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $exe
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($exe)
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.Arguments = '--data-dir "' + $data + '"'
    if ($Minimized) { $info.Arguments += ' --minimized' }
    $process = [Diagnostics.Process]::Start($info)
    if ($null -eq $process) { throw 'Windows did not start DisplayWeave.' }
    $process.Dispose()
}
catch { Write-Error $_; exit 1 }
