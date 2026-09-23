[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $PackageDirectory,
    [Parameter(Mandatory)] [string] $ExpectedSignerSubject
)

$ErrorActionPreference = 'Stop'
try {
    $catalog = Join-Path $PackageDirectory 'UpdateCatalog.cat'
    $signature = Get-AuthenticodeSignature -LiteralPath $catalog
    if ($signature.Status -ne 'Valid' -or
        $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -cne $ExpectedSignerSubject) {
        exit 1
    }

    $validation = Test-FileCatalog -Path $PackageDirectory -CatalogFilePath $catalog -Detailed
    if ($validation.Status -eq 'Valid' -and $validation.HashAlgorithm -eq 'SHA256') {
        exit 0
    }
}
catch {
}
exit 1
