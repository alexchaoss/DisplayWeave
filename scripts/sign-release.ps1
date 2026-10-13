# Signs a DisplayWeave release with a release key: the maintainer's key in this Windows account, or with
# -KeyVault the key in Azure Key Vault that CI may use once the maintainer approves a release build.
# Neither private key can be exported. Installed copies refuse an update without it.
#   -NewKey                         create the key in this Windows account (it cannot be exported) and pin it
#   -PinKey <base64>                pin another public key, such as a backup key made on another computer
#   -Version <v>                    check the CI-built draft release v<v>, sign it and publish it
#   -Version <v> -Directory <dir>   sign a local build in <dir> (scripts\release.ps1 -Publish does this)
#   -KeyVault <vault>/<key>         with -Directory: sign with that Key Vault key through the signed-in az CLI (CI)
param([string]$Version, [string]$Directory, [switch]$NewKey, [string]$PinKey, [string]$KeyVault)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$github = 'alexchaoss/DisplayWeave'
$keyName = 'DisplayWeave release signing'
# Where the public keys are pinned, as a C# string array and a PowerShell array.
$pins = @(
    @{ File = 'src\DisplayWeave\Updater.cs'; Pattern = '(?<=ReleaseKeys = \{)[^}]*(?=\};)'; Quote = '"'; Pad = ' ' }
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

function ConvertFrom-Base64Url([string]$Text) {
    $Text = $Text.Replace('-', '+').Replace('_', '/')
    return [Convert]::FromBase64String($Text + ('=' * ((4 - $Text.Length % 4) % 4)))
}

# Signs each file's .sha256 after checking that it names that file and matches its bytes, and checks
# each signature with the public key the way installed copies do before writing it.
function Write-ReleaseSignatures([string]$Folder) {
    if ($KeyVault) {
        $vault, $vaultKey = $KeyVault -split '/', 2
        $jwk = az keyvault key show --vault-name $vault --name $vaultKey --query key --output json | ConvertFrom-Json
        if ($LASTEXITCODE -ne 0) { throw "Could not read the Key Vault key $KeyVault." }
        # ECCPUBLICBLOB: BCRYPT_ECDSA_PUBLIC_P256_MAGIC and the 32-byte coordinate length, then x and y.
        $blob = [byte[]](0x45, 0x43, 0x53, 0x31, 0x20, 0, 0, 0) + (ConvertFrom-Base64Url $jwk.x) + (ConvertFrom-Base64Url $jwk.y)
        $request = [IO.Path]::GetTempFileName()
        # Key Vault's ES256 signs a SHA-256 digest and returns r then s, the format ECDsaCng verifies.
        $sign = {
            param([byte[]]$Data)
            $digest = [Convert]::ToBase64String([Security.Cryptography.SHA256]::Create().ComputeHash($Data)).TrimEnd('=').Replace('+', '-').Replace('/', '_')
            [IO.File]::WriteAllText($request, (@{ alg = 'ES256'; value = $digest } | ConvertTo-Json -Compress))
            $result = az rest --method post --url "https://$vault.vault.azure.net/keys/$vaultKey/sign?api-version=7.4" --resource https://vault.azure.net --body "@$request" --output json | ConvertFrom-Json
            if ($LASTEXITCODE -ne 0) { throw "Key Vault did not sign with $KeyVault." }
            return ConvertFrom-Base64Url $result.value
        }
    } else {
        if (-not [Security.Cryptography.CngKey]::Exists($keyName)) { throw "This Windows account has no '$keyName' key. Run .\scripts\sign-release.ps1 -NewKey first." }
        $key = [Security.Cryptography.CngKey]::Open($keyName)
        $blob = $key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
        $ecdsa = New-Object Security.Cryptography.ECDsaCng($key)
        $ecdsa.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
        $sign = { param([byte[]]$Data) return $ecdsa.SignData($Data) }
    }
    $public = [Convert]::ToBase64String($blob)
    foreach ($pin in $pins) {
        if ((Get-PinnedKeys $pin) -notcontains $public) { throw "This release key is not pinned in $($pin.File), so its signatures would be refused." }
    }
    $verifier = New-Object Security.Cryptography.ECDsaCng([Security.Cryptography.CngKey]::Import($blob, [Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob))
    $verifier.HashAlgorithm = [Security.Cryptography.CngAlgorithm]::Sha256
    $signatures = @()
    try {
        foreach ($name in @("DisplayWeave-Setup-$Version.exe", "DisplayWeave-$Version.zip")) {
            $file = Join-Path $Folder $name
            $hash = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
            if ([IO.File]::ReadAllText("$file.sha256").Trim() -cne "$hash  $name") { throw "$name.sha256 does not describe $name; nothing was signed." }
            $data = [IO.File]::ReadAllBytes("$file.sha256")
            [byte[]]$signature = & $sign $data
            if (-not $verifier.VerifyData($data, $signature)) { throw "The signature of $name.sha256 does not verify with the release key." }
            [IO.File]::WriteAllText("$file.sha256.sig", [Convert]::ToBase64String($signature))
            $signatures += "$file.sha256.sig"
        }
    }
    finally {
        $verifier.Dispose()
        if ($KeyVault) { Remove-Item -LiteralPath $request -ErrorAction SilentlyContinue } else { $ecdsa.Dispose(); $key.Dispose() }
    }
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
    # A version without a suffix such as -preview is a full release and becomes the latest one.
    $final = if ($Version.Contains('-')) { @() } else { @('--prerelease=false', '--latest') }
    gh release edit "v$Version" --repo $github --draft=false @final
    if ($LASTEXITCODE -ne 0) { throw 'Publishing failed; the signed release is still a draft.' }
    Write-Output "Signed and published v$Version."
}
finally { Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue }
