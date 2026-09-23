[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $PublishPath,
    [ValidateSet('Audit', 'Release')]
    [string] $Mode = 'Audit',
    [string] $EvidencePath,
    [string] $ExpectedSignerSubject,
    [switch] $Sign,
    [string] $SignToolPath,
    [string] $SigningClientPath
)

$ErrorActionPreference = 'Stop'
$publishDirectory = [System.IO.Path]::GetFullPath($PublishPath)
$executable = Join-Path $publishDirectory 'WinGit.Native.exe'
$statusPath = Join-Path $publishDirectory 'SigningStatus.json'

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw 'Native publish output is missing WinGit.Native.exe'
}

function Write-SigningStatus {
    param([string] $Gate)

    $signature = Get-AuthenticodeSignature -LiteralPath $executable
    $subject = if ($null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
    [ordered]@{
        artifact = 'WinGit.Native.exe'
        sha256 = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
        signatureStatus = [string] $signature.Status
        signerSubject = $subject
        releaseGate = $Gate
    } | ConvertTo-Json | Set-Content -LiteralPath $statusPath -Encoding utf8
    return $signature
}

$null = Write-SigningStatus -Gate 'Blocked'

function Require-ReleaseEvidence {
    if ([string]::IsNullOrWhiteSpace($EvidencePath) -or -not (Test-Path -LiteralPath $EvidencePath -PathType Container)) {
        throw 'Release evidence directory is required'
    }

    foreach ($name in @('clean-machine-install.md', 'native-redistribution-notices.md', 'authentication-permission.md', 'product-identity-review.md')) {
        $file = Join-Path $EvidencePath $name
        if (-not (Test-Path -LiteralPath $file -PathType Leaf) -or (Get-Item -LiteralPath $file).Length -eq 0) {
            throw "Release evidence is missing or empty: $name"
        }
    }
}

if ($Mode -eq 'Release') {
    Require-ReleaseEvidence
    if ([string]::IsNullOrWhiteSpace($ExpectedSignerSubject)) {
        throw 'ExpectedSignerSubject is required for a native release candidate'
    }
    foreach ($name in @('WINGIT_AZURE_SIGNING_ENDPOINT', 'WINGIT_AZURE_SIGNING_ACCOUNT', 'WINGIT_AZURE_SIGNING_PROFILE')) {
        if ([string]::IsNullOrWhiteSpace([Environment]::GetEnvironmentVariable($name))) {
            throw "Native release signing configuration is missing: $name"
        }
    }
    $endpoint = [Environment]::GetEnvironmentVariable('WINGIT_AZURE_SIGNING_ENDPOINT')
    $parsedEndpoint = $null
    if (-not [Uri]::TryCreate($endpoint, [UriKind]::Absolute, [ref] $parsedEndpoint) -or $parsedEndpoint.Scheme -ne 'https') {
        throw 'Native release signing endpoint must use HTTPS'
    }
    if ($parsedEndpoint.UserInfo -or $parsedEndpoint.Query -or $parsedEndpoint.Fragment) {
        throw 'Native release signing endpoint must not contain credentials or query data'
    }
}

function Sign-NativeFile([string] $FilePath) {
    $metadataPath = Join-Path $env:TEMP ([System.IO.Path]::GetRandomFileName())
    try {
        @{
            Endpoint = $endpoint
            CodeSigningAccountName = [Environment]::GetEnvironmentVariable('WINGIT_AZURE_SIGNING_ACCOUNT')
            CertificateProfileName = [Environment]::GetEnvironmentVariable('WINGIT_AZURE_SIGNING_PROFILE')
        } | ConvertTo-Json | Set-Content -LiteralPath $metadataPath -Encoding utf8
        & $SignToolPath sign /v /fd SHA256 /tr http://timestamp.acs.microsoft.com /td SHA256 /dlib $SigningClientPath /dmdf $metadataPath $FilePath
        if ($LASTEXITCODE -ne 0) {
            throw 'Native release signing failed'
        }
    }
    finally {
        Remove-Item -LiteralPath $metadataPath -Force -ErrorAction SilentlyContinue
    }
}

if ($Sign) {
    if ($Mode -ne 'Release') {
        throw 'Sign requires Release mode'
    }
    if ([string]::IsNullOrWhiteSpace($SignToolPath) -or
        [string]::IsNullOrWhiteSpace($SigningClientPath) -or
        -not (Test-Path -LiteralPath $SignToolPath -PathType Leaf) -or
        -not (Test-Path -LiteralPath $SigningClientPath -PathType Leaf)) {
        throw 'SignToolPath and SigningClientPath must name existing files'
    }
    Sign-NativeFile $executable
}

$signature = Get-AuthenticodeSignature -LiteralPath $executable
$subject = if ($null -ne $signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
$releaseReady = $Mode -eq 'Release' -and $signature.Status -eq 'Valid' -and $subject -eq $ExpectedSignerSubject

if ($Mode -eq 'Release' -and -not $releaseReady) {
    throw 'Native release candidate requires a valid signature from ExpectedSignerSubject'
}
if ($releaseReady) {
    $null = Write-SigningStatus -Gate 'Passed'
    $catalog = Join-Path $publishDirectory 'UpdateCatalog.cat'
    $temporaryCatalog = Join-Path $env:TEMP ([System.IO.Path]::GetRandomFileName() + '.cat')
    try {
        if ($Sign) {
            if (Test-Path -LiteralPath $catalog) {
                throw 'Remove the existing update catalog before signing a new release candidate'
            }
            New-FileCatalog -Path $publishDirectory -CatalogFilePath $temporaryCatalog -CatalogVersion 2.0 | Out-Null
            Sign-NativeFile $temporaryCatalog
            Move-Item -LiteralPath $temporaryCatalog -Destination $catalog
        }

        $catalogSignature = Get-AuthenticodeSignature -LiteralPath $catalog
        $catalogValidation = Test-FileCatalog -Path $publishDirectory -CatalogFilePath $catalog -Detailed
        if ($catalogSignature.Status -ne 'Valid' -or
            $null -eq $catalogSignature.SignerCertificate -or
            $catalogSignature.SignerCertificate.Subject -cne $ExpectedSignerSubject -or
            $catalogValidation.Status -ne 'Valid' -or
            $catalogValidation.HashAlgorithm -ne 'SHA256') {
            throw 'Native release candidate requires a valid signed catalog for the entire publish output'
        }
    }
    catch {
        $null = Write-SigningStatus -Gate 'Blocked'
        throw
    }
    finally {
        Remove-Item -LiteralPath $temporaryCatalog -Force -ErrorAction SilentlyContinue
    }
}
