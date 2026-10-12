# Signs a DisplayWeave release with the maintainer's release key. Run it on your own computer, never in
# CI: the key must never reach GitHub. Installed copies and install.ps1 refuse a release without it.
#   -NewKey                         create the key in this Windows account (it cannot be exported) and pin it
#   -PinKey <base64>                pin another public key, such as a backup key made on another computer
#   -Version <v>                    check the CI-built draft release v<v>, sign it and publish it
#   -Version <v> -Directory <dir>   sign a local build in <dir> (scripts\release.ps1 -Publish does this)
param([string]$Version, [string]$Directory, [switch]$NewKey, [string]$PinKey)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$github = 'alexchaoss/DisplayWeave'
$keyName = 'DisplayWeave release signing'
# Where the public keys are pinned, as a C# string array and a PowerShell array.
$pins = @(
    @{ File = 'src\DisplayWeave\Updater.cs'; Pattern = '(?<=ReleaseKeys = \{)[^}]*(?=\};)'; Quote = '"'; Pad = ' ' },
    @{ File = 'installer\install.ps1'; Pattern = '(?<=\$releaseKeys = @\()[^)]*(?=\))'; Quote = "'"; Pad = '' }
)

function Get-PinnedKeys([hashtable]$Pin) {
    $text = [IO.File]::ReadAllText((Join-Path $repository $Pin.File))
    $match = [regex]::Match($text, $Pin.Pattern)
    if (-not $match.Success) { throw "Could not find the release key list in $($Pin.File)." }
    return @([regex]::Matches($match.Value, '[A-Za-z0-9+/=]{20,}') | ForEach-Object { $_.Value })
}

function Add-PinnedKey([string]$Key) {
    try { $imported = [Security.Cryptography.CngKey]::Import([Convert]::FromBase64String($Key), [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob) }
    catch { throw 'That is not a DisplayWeave release public key (base64 ECCPUBLICBLOB).' }
    $isP256 = $imported.Algorithm -eq [Security.Cryptography.CngAlgorithm]::ECDsaP256
    $imported.Dispose()
    if (-not $isP256) { throw 'Release keys must be ECDSA P-256.' }
    foreach ($pin in $pins) {
        $keys = @(Get-PinnedKeys $pin)
        if ($keys -contains $Key) { Write-Output "Already pinned in $($pin.File)."; continue }
        $keys += $Key
        $path = Join-Path $repository $pin.File
        $text = [IO.File]::ReadAllText($path)
        $match = [regex]::Match($text, $pin.Pattern)
        $list = $pin.Pad + (($keys | ForEach-Object { $pin.Quote + $_ + $pin.Quote }) -join ', ') + $pin.Pad
        [IO.File]::WriteAllText($path, $text.Substring(0, $match.Index) + $list + $text.Substring($match.Index + $match.Length), (New-Object Text.UTF8Encoding($false)))
        Write-Output "Pinned in $($pin.File)."
    }
}

# Signs each file's .sha256 after checking that it names that file and matches its bytes.
function Write-ReleaseSignatures([string]$Folder) {
    if (-not [Security.Cryptography.CngKey]::Exists($keyName)) { throw "This Windows account has no '$keyName' key. Run .\scripts\sign-release.ps1 -NewKey first." }
    $key = [Security.Cryptography.CngKey]::Open($keyName)
    $public = [Convert]::ToBase64String($key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob))
    foreach ($pin in $pins) {
        if ((Get-PinnedKeys $pin) -notcontains $public) { throw "This computer's release key is not pinned in $($pin.File), so its signatures would be refused." }
    }
    $ecdsa = New-Object Security.Cryptography.ECDsaCng($key)
    $ecdsa.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
    $signatures = @()
    foreach ($name in @("DisplayWeave-Setup-$Version.exe", "DisplayWeave-$Version.zip")) {
        $file = Join-Path $Folder $name
        $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
        if ([IO.File]::ReadAllText("$file.sha256").Trim() -cne "$hash  $name") { throw "$name.sha256 does not describe $name; nothing was signed." }
        [IO.File]::WriteAllText("$file.sha256.sig", [Convert]::ToBase64String($ecdsa.SignData([IO.File]::ReadAllBytes("$file.sha256"))))
        $signatures += "$file.sha256.sig"
    }
    $ecdsa.Dispose()
    $key.Dispose()
    return $signatures
}

if ($NewKey) {
    if ([Security.Cryptography.CngKey]::Exists($keyName)) { throw "This Windows account already has a '$keyName' key." }
    $parameters = New-Object Security.Cryptography.CngKeyCreationParameters
    $parameters.ExportPolicy = [Security.Cryptography.CngExportPolicies]::None
    $created = [Security.Cryptography.CngKey]::Create([Security.Cryptography.CngAlgorithm]::ECDsaP256, $keyName, $parameters)
    $PinKey = [Convert]::ToBase64String($created.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob))
    $created.Dispose()
    Write-Output "Created '$keyName' in this Windows account. Public key: $PinKey"
}
if ($PinKey) { Add-PinnedKey $PinKey; return }

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$') { throw 'Give -Version, such as 0.5.10-preview.' }
if ($Directory) { Write-ReleaseSignatures ([IO.Path]::GetFullPath($Directory)); return }

$folder = Join-Path $env:TEMP ('DisplayWeaveSign-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $folder | Out-Null
try {
    $names = @("DisplayWeave-Setup-$Version.exe", "DisplayWeave-$Version.zip")
    $patterns = foreach ($name in $names) { '--pattern'; $name; '--pattern'; "$name.sha256" }
    gh release download "v$Version" --repo $github --dir $folder $patterns
    if ($LASTEXITCODE -ne 0) { throw "Could not download release v$Version. CI creates it as a draft after a version bump on main." }
    foreach ($name in $names) {
        # Proves the file came from this repository's workflow on main, not from someone with upload rights.
        gh attestation verify (Join-Path $folder $name) --repo $github --source-ref refs/heads/main
        if ($LASTEXITCODE -ne 0) { throw "$name has no build attestation from this repository's main branch; nothing was signed." }
    }
    $signatures = Write-ReleaseSignatures $folder
    gh release upload "v$Version" $signatures --repo $github
    if ($LASTEXITCODE -ne 0) { throw 'Uploading the signatures failed; the release stays a draft.' }
    gh release edit "v$Version" --repo $github --draft=false
    if ($LASTEXITCODE -ne 0) { throw 'Publishing failed; the signed release is still a draft.' }
    Write-Output "Signed and published v$Version."
}
finally { Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue }
