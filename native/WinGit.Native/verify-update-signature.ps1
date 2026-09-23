[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ExecutablePath,
    [Parameter(Mandatory)] [string] $ExpectedSignerSubject
)

$ErrorActionPreference = 'Stop'
try {
    $signature = Get-AuthenticodeSignature -LiteralPath $ExecutablePath
    if ($signature.Status -eq 'Valid' -and
        $null -ne $signature.SignerCertificate -and
        $signature.SignerCertificate.Subject -ceq $ExpectedSignerSubject) {
        exit 0
    }
}
catch {
}
exit 1
