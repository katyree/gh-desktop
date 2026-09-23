[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ArchivePath,
    [Parameter(Mandatory)] [string] $ArchiveSha256,
    [Parameter(Mandatory)] [string] $StagedDirectory,
    [Parameter(Mandatory)] [string] $InstallationDirectory,
    [Parameter(Mandatory)] [string] $ExpectedSignerSubject,
    [Parameter(Mandatory)] [int] $ParentProcessId,
    [Parameter(Mandatory)] [string] $ResultPath
)

$ErrorActionPreference = 'Stop'
$fullTarget = [IO.Path]::GetFullPath($InstallationDirectory)
$target = $fullTarget.TrimEnd('\')
$stage = [IO.Path]::GetFullPath($StagedDirectory).TrimEnd('\')
$previous = Join-Path (Split-Path $target -Parent) ('.' + (Split-Path $target -Leaf) + '.previous-' + [guid]::NewGuid().ToString('N'))
$movedPrevious = $false
$movedStage = $false

function Write-InstallResult([string] $Message) {
    $resultDirectory = Split-Path $ResultPath -Parent
    New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
    Set-Content -LiteralPath $ResultPath -Value $Message -Encoding utf8
}

try {
    if ($fullTarget -eq [IO.Path]::GetPathRoot($fullTarget) -or
        (Split-Path $stage -Parent) -cne (Split-Path $target -Parent) -or
        -not $stage.StartsWith((Join-Path (Split-Path $target -Parent) ('.' + (Split-Path $target -Leaf) + '.update-')), [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath (Join-Path $target 'WinGit.Native.exe') -PathType Leaf) -or
        -not (Test-Path -LiteralPath (Join-Path $stage 'WinGit.Native.exe') -PathType Leaf)) {
        throw 'The update paths are invalid.'
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(90)
    while (Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue) {
        if ([DateTime]::UtcNow -ge $deadline) {
            throw 'The running app did not exit.'
        }
        Start-Sleep -Milliseconds 250
    }

    if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -cne $ArchiveSha256) {
        throw 'The downloaded update changed before installation.'
    }
    $evidence = Get-Content -LiteralPath (Join-Path $stage 'SigningStatus.json') -Raw | ConvertFrom-Json
    $executable = Join-Path $stage 'WinGit.Native.exe'
    $signature = Get-AuthenticodeSignature -LiteralPath $executable
    if ($evidence.artifact -cne 'WinGit.Native.exe' -or
        $evidence.releaseGate -cne 'Passed' -or
        $evidence.signatureStatus -cne 'Valid' -or
        $evidence.signerSubject -cne $ExpectedSignerSubject -or
        $evidence.sha256 -cne (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -or
        $signature.Status -ne 'Valid' -or
        $signature.SignerCertificate.Subject -cne $ExpectedSignerSubject) {
        throw 'The staged executable is not signed by the expected signer.'
    }

    $catalog = Join-Path $stage 'UpdateCatalog.cat'
    $catalogSignature = Get-AuthenticodeSignature -LiteralPath $catalog
    $catalogValidation = Test-FileCatalog -Path $stage -CatalogFilePath $catalog -Detailed
    if ($catalogSignature.Status -ne 'Valid' -or
        $null -eq $catalogSignature.SignerCertificate -or
        $catalogSignature.SignerCertificate.Subject -cne $ExpectedSignerSubject -or
        $catalogValidation.Status -ne 'Valid' -or
        $catalogValidation.HashAlgorithm -ne 'SHA256') {
        throw 'The staged package has no valid signed file catalog.'
    }

    Move-Item -LiteralPath $target -Destination $previous
    $movedPrevious = $true
    Move-Item -LiteralPath $stage -Destination $target
    $movedStage = $true
    Write-InstallResult 'Update files installed. WinGit restarted.'
    $newProcess = Start-Process -FilePath (Join-Path $target 'WinGit.Native.exe') -WorkingDirectory $target -PassThru
    Start-Sleep -Seconds 3
    if ($newProcess.HasExited) {
        throw 'The updated app exited during startup.'
    }
}
catch {
    if ($movedStage) {
        Move-Item -LiteralPath $target -Destination $stage -ErrorAction SilentlyContinue
    }
    if ($movedPrevious) {
        Move-Item -LiteralPath $previous -Destination $target -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath (Join-Path $target 'WinGit.Native.exe') -PathType Leaf) {
        Write-InstallResult 'Update installation failed. The previous installation was preserved.'
    }
    else {
        Write-InstallResult "Update installation failed. Restore the previous installation from $previous."
    }
    if (-not (Get-Process -Id $ParentProcessId -ErrorAction SilentlyContinue) -and
        (Test-Path -LiteralPath (Join-Path $target 'WinGit.Native.exe') -PathType Leaf)) {
        try {
            Start-Process -FilePath (Join-Path $target 'WinGit.Native.exe') -WorkingDirectory $target
        }
        catch {
            Write-InstallResult "Update installation failed. Start the previous app from $target."
        }
    }
    exit 1
}
