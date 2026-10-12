# One command for a DisplayWeave release: build, run the non-desktop checks, package,
# build and test the setup. -Publish then signs the checksums with this computer's release key
# (sign-release.ps1) and creates the GitHub prerelease v<version> with the ZIP, the setup, their
# checksums and signatures, from committed and pushed source only. It is the fallback when CI
# cannot build: normally CI makes a draft and sign-release.ps1 -Version signs and publishes it.
param([switch]$Publish, [string]$NotesFile)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $repository 'src\DisplayWeave\version.txt') -Raw).Trim()
if ($Publish) {
    if (git -C $repository status --porcelain) { throw 'Commit or stash local changes first; a release must match committed source.' }
    if (-not (git -C $repository branch -r --contains HEAD)) { throw 'Push this commit first; the release tag must point at a commit on GitHub.' }
    if ($NotesFile -and -not (Test-Path -LiteralPath $NotesFile -PathType Leaf)) { throw "Release notes not found: $NotesFile" }
}

# Native files go next to the release output, not dist: running apps can keep dist DLLs loaded.
$native = Join-Path $repository "release\build-$version"
& (Join-Path $PSScriptRoot 'build.ps1')
& (Join-Path $PSScriptRoot 'build-native.ps1') -Architecture all -OutputDirectory $native

$report = Join-Path $env:TEMP ('DisplayWeave-release-' + [Guid]::NewGuid().ToString('N') + '.txt')
$test = Start-Process -FilePath (Join-Path $repository 'dist\DisplayWeave.exe') -ArgumentList @('--self-test', ('"{0}"' -f $report)) -WindowStyle Hidden -Wait -PassThru
if ($test.ExitCode -ne 0) { throw "Managed self-tests failed; see $report and its .rules/.automatic/.lifecycle reports." }
foreach ($architecture in @('x64', 'x86')) {
    foreach ($mode in @('--self-test-policy', '--self-test-ipc')) {
        & (Join-Path $native "native-v2-$architecture\RegionHookHost.exe") $mode | Out-Null
        if ($LASTEXITCODE -ne 0) { throw "$architecture $mode failed." }
    }
}
Write-Output 'Managed and native checks passed.'

& (Join-Path $PSScriptRoot 'package.ps1') -NativeDirectory $native
foreach ($suite in @('installer-tests.ps1', 'setup-tests.ps1')) {
    if ($suite -eq 'setup-tests.ps1') { & (Join-Path $PSScriptRoot 'build-setup.ps1') }
    $output = & (Join-Path $repository "tests\$suite") 2>$null
    if ($LASTEXITCODE -ne 0) { $output | Write-Output; throw "$suite failed." }
    Write-Output ("{0}: {1}" -f $suite, ($output | Where-Object { $_ -match 'passed;' } | Select-Object -Last 1))
}

$assets = @("DisplayWeave-$version.zip", "DisplayWeave-$version.zip.sha256", "DisplayWeave-Setup-$version.exe", "DisplayWeave-Setup-$version.exe.sha256") |
    ForEach-Object { Join-Path $repository "release\$_" }
if (-not $Publish) { Write-Output 'Release files ready (not published):'; $assets | Write-Output; return }
$assets += & (Join-Path $PSScriptRoot 'sign-release.ps1') -Version $version -Directory (Join-Path $repository 'release')

$notes = if ($NotesFile) { @('--notes-file', $NotesFile) } else { @('--generate-notes') }
gh release create "v$version" @assets --repo alexchaoss/DisplayWeave --prerelease --title "DisplayWeave $version" --target (git -C $repository rev-parse HEAD) @notes
if ($LASTEXITCODE -ne 0) { throw 'GitHub release creation failed.' }
