#requires -Version 5.1
# Installs the DisplayWeave projection prototype from the extracted package
# folder this script is in. Separate from the legacy DisplayWeave install: its
# settings, startup entry and version folders are never touched.
[CmdletBinding()]
param(
    # Folder already holding the pinned MttVDD.inf/MttVDD.dll/mttvdd.cat.
    # When omitted, the pinned upstream release asset is downloaded and verified.
    [string]$DriverPackageDirectory,
    [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'DisplayWeavePrototype'),
    [switch]$DesktopShortcut,
    [switch]$NoLaunch,
    # Isolated installer checks only: do not create Start menu/desktop shortcuts.
    [switch]$NoShortcut
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Exact upstream release 25.5.2, asset Signed-Driver-v24.12.24-x64.zip (MIT).
$driverUrl = 'https://github.com/VirtualDrivers/Virtual-Display-Driver/releases/download/25.5.2/Signed-Driver-v24.12.24-x64.zip'
$driverArchiveHash = 'F93E7CE3D640C83C419B1B72D2C83B2D8E34D83E8B7BA11F3328A40D2DA82FA4'
$driverFiles = @{
    'MttVDD.inf' = '550D211FE481E74DFE3F9D724ED78BE48B3A9113405965D683D9373E8D672F5D'
    'MttVDD.dll' = 'C9CA837F57A98FBD43BC416A7F535A95843626E7759EAF85CF0CD7CE334DBB05'
    'mttvdd.cat' = '08A0093FC9B2E32B287A6F8A77CA4DE0A31830D29FC33D2B13A918DC859468F6'
}

$package = $PSScriptRoot
$version = (Get-Content -LiteralPath (Join-Path $package 'version.txt') -Raw).Trim()
if ($version.Length -gt 64 -or $version -notmatch '^\d+\.\d+\.\d+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') { throw 'The package has an invalid version.txt.' }
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
$versions = Join-Path $InstallRoot 'versions'
$target = Join-Path $versions $version

function Get-Payload {
    $entries = @()
    foreach ($line in Get-Content -LiteralPath (Join-Path $package 'payload.sha256')) {
        if ($line -notmatch '^([0-9a-f]{64})  ([A-Za-z0-9._/-]+)$' -or $Matches[2].Contains('..')) { throw "Invalid payload manifest line: $line" }
        $entries += [pscustomobject]@{ Hash = $Matches[1].ToUpperInvariant(); Path = $Matches[2].Replace('/', '\') }
    }
    if ($entries.Count -eq 0) { throw 'The payload manifest is empty.' }
    return $entries
}
function Test-Payload([string]$Directory, $Entries) {
    foreach ($entry in $Entries) {
        $file = Join-Path $Directory $entry.Path
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $entry.Hash) { return $false }
    }
    return $true
}
function Test-Driver([string]$Directory) {
    foreach ($name in $driverFiles.Keys) {
        $file = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ne $driverFiles[$name]) { return $false }
    }
    foreach ($name in @('MttVDD.dll', 'mttvdd.cat')) {
        $signature = Get-AuthenticodeSignature -LiteralPath (Join-Path $Directory $name)
        if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '^CN=SignPath Foundation,') { return $false }
    }
    return $true
}

$payload = Get-Payload
if (-not (Test-Payload $package $payload)) { throw 'The extracted package does not match payload.sha256. Download and extract it again.' }
$running = @(Get-Process -Name DisplayWeavePrototype -ErrorAction SilentlyContinue | Where-Object {
    try { $_.Path -and [IO.Path]::GetFullPath($_.Path).StartsWith($target + '\', [StringComparison]::OrdinalIgnoreCase) } catch { $false } })
if ($running.Count) { throw "DisplayWeave Prototype $version is running. Close it (after any session ends) and run the installer again." }

New-Item -ItemType Directory -Path $versions -Force | Out-Null
if (Test-Path -LiteralPath $target) {
    if (-not (Test-Payload $target $payload)) { throw "$target exists but differs from this package. Remove that folder manually if it is not in use, then retry." }
    Write-Output "Version $version is already installed; verified its files."
} else {
    $staging = Join-Path $versions ('.staging-' + [Guid]::NewGuid().ToString('N'))
    try {
        foreach ($entry in $payload) {
            $destination = Join-Path $staging $entry.Path
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $package $entry.Path) -Destination $destination
        }
        if (-not (Test-Payload $staging $payload)) { throw 'Copied files failed verification.' }
        Rename-Item -LiteralPath $staging -NewName $version
    } finally {
        if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    }
}

$driver = Join-Path $target 'driver'
if (Test-Driver $driver) {
    Write-Output 'Display driver files already present and verified.'
} else {
    $work = Join-Path ([IO.Path]::GetTempPath()) ('DisplayWeavePrototype-driver-' + [Guid]::NewGuid().ToString('N'))
    try {
        if ($DriverPackageDirectory) {
            $sourceDirectory = (Resolve-Path -LiteralPath $DriverPackageDirectory).Path
        } else {
            New-Item -ItemType Directory -Path $work | Out-Null
            $archive = Join-Path $work 'driver.zip'
            [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
            Write-Output "Downloading the pinned upstream display driver from $driverUrl"
            Invoke-WebRequest -Uri $driverUrl -OutFile $archive -UseBasicParsing
            if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $driverArchiveHash) { throw 'The downloaded driver archive does not match the pinned SHA-256.' }
            $sourceDirectory = Join-Path $work 'extracted'
            Expand-Archive -LiteralPath $archive -DestinationPath $sourceDirectory
        }
        if (-not (Test-Driver $sourceDirectory)) { throw 'The driver files do not match the pinned hashes and SignPath Foundation signatures.' }
        New-Item -ItemType Directory -Path $driver -Force | Out-Null
        foreach ($name in $driverFiles.Keys) { Copy-Item -LiteralPath (Join-Path $sourceDirectory $name) -Destination (Join-Path $driver $name) -Force }
        if (-not (Test-Driver $driver)) { throw 'Installed driver files failed verification.' }
        Write-Output 'Display driver files verified and stored (staged in Windows only during an approved session).'
    } finally {
        if (Test-Path -LiteralPath $work) { Remove-Item -LiteralPath $work -Recurse -Force }
    }
}

$executable = Join-Path $target 'DisplayWeavePrototype.exe'
$pointer = Join-Path $InstallRoot 'current.json'
$temporary = $pointer + '.' + [Guid]::NewGuid().ToString('N') + '.tmp'
[IO.File]::WriteAllText($temporary, (ConvertTo-Json @{ version = $version; path = $target }), (New-Object Text.UTF8Encoding($false)))
Move-Item -LiteralPath $temporary -Destination $pointer -Force

$shell = New-Object -ComObject WScript.Shell
$locations = @(if (-not $NoShortcut) { [Environment]::GetFolderPath('Programs') })
if ($DesktopShortcut -and -not $NoShortcut) { $locations += [Environment]::GetFolderPath('Desktop') }
foreach ($location in $locations) {
    $shortcut = $shell.CreateShortcut((Join-Path $location 'DisplayWeave Prototype.lnk'))
    $shortcut.TargetPath = $executable
    $shortcut.WorkingDirectory = $target
    $shortcut.Description = 'Experimental virtual-display projection prototype'
    $shortcut.Save()
}
Write-Output "Installed DisplayWeave Prototype $version to $target (Start menu shortcut created; no startup entry)."
if (-not $NoLaunch) { Start-Process -FilePath $executable -WorkingDirectory $target }
