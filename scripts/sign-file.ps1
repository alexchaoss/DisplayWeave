# CI only: signs one file with Azure Artifact Signing, through the module and settings the workflow's
# artifact-signing-action uses. build-setup.ps1 -Sign gives it to ISCC as a Sign Tool, so the setup and the
# uninstaller Inno builds into it are signed while the setup is compiled. Needs the ArtifactSigning module
# (installed by the workflow) and the workflow's Azure CLI sign-in.
param([Parameter(Mandatory = $true)][string]$Path)
$ErrorActionPreference = 'Stop'
# The module picks its signer by file extension, and ISCC hands over the uninstaller under a temporary name.
$copy = Join-Path ([IO.Path]::GetTempPath()) ('DisplayWeave-sign-' + [Guid]::NewGuid().ToString('N') + '.exe')
Copy-Item -LiteralPath $Path -Destination $copy
try {
    $params = @{
        Endpoint = 'https://eus.codesigning.azure.net/'; CodeSigningAccountName = 'displayweave'; CertificateProfileName = 'displayweave-public'
        Files = $copy; FileDigest = 'SHA256'; TimestampRfc3161 = 'http://timestamp.acs.microsoft.com'; TimestampDigest = 'SHA256'
        # Only the Azure CLI sign-in, as the action's defaults allow with no other credential configured.
        ExcludeEnvironmentCredential = $true; ExcludeWorkloadIdentityCredential = $true; ExcludeManagedIdentityCredential = $true
        ExcludeSharedTokenCacheCredential = $true; ExcludeVisualStudioCredential = $true; ExcludeVisualStudioCodeCredential = $true
        ExcludeAzureCliCredential = $false; ExcludeAzurePowerShellCredential = $true; ExcludeAzureDeveloperCliCredential = $true
        ExcludeInteractiveBrowserCredential = $true
    }
    Invoke-ArtifactSigning @params
    if ((Get-AuthenticodeSignature -LiteralPath $copy).Status -ne 'Valid') { throw "Signing did not give $Path a valid signature." }
    Copy-Item -LiteralPath $copy -Destination $Path -Force
}
finally { Remove-Item -LiteralPath $copy -ErrorAction SilentlyContinue }
