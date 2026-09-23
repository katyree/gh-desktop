[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',
    [string] $OutputPath = (Join-Path $PSScriptRoot 'artifacts\win-x64')
)

$ErrorActionPreference = 'Stop'
$projectPath = Join-Path $PSScriptRoot 'WinGit.Native\WinGit.Native.csproj'
$publishPath = [System.IO.Path]::GetFullPath($OutputPath)
$artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot 'artifacts')).TrimEnd('\')
$artifactsPrefix = "$artifactsRoot\"
$isSafePublishPath = $publishPath.StartsWith($artifactsPrefix, [StringComparison]::OrdinalIgnoreCase)
if ($publishPath.Equals($artifactsRoot, [StringComparison]::OrdinalIgnoreCase) -or -not $isSafePublishPath) {
    throw "OutputPath must be a descendant of native\artifacts: $OutputPath"
}

if (Test-Path -LiteralPath $publishPath) {
    $existingPublishPath = Get-Item -LiteralPath $publishPath -Force
    if ($existingPublishPath.Attributes -band [System.IO.FileAttributes]::ReparsePoint) {
        throw "OutputPath cannot be a reparse point: $OutputPath"
    }
}

$msbuildProperties = @(
    '-p:Platform=x64',
    "-p:RuntimeIdentifier=$Runtime"
)

function Invoke-Dotnet {
    param(
        [Parameter(Mandatory)]
        [string[]] $Arguments
    )

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

try {
    $restoreArguments = @('restore', $projectPath, '--runtime', $Runtime) + $msbuildProperties
    Invoke-Dotnet $restoreArguments

    $buildArguments = @('build', $projectPath, '--configuration', $Configuration, '--runtime', $Runtime, '--no-restore') + $msbuildProperties
    Invoke-Dotnet $buildArguments

    if (Test-Path -LiteralPath $publishPath) {
        Remove-Item -LiteralPath $publishPath -Recurse -Force
    }

    $publishArguments = @('publish', $projectPath, '--configuration', $Configuration, '--runtime', $Runtime, '--no-restore', '--no-build', '--self-contained', 'true', '--output', $publishPath) + $msbuildProperties
    Invoke-Dotnet $publishArguments

    $requiredPublishFiles = @(
        'App.xbf',
        'MainWindow.xbf',
        'NativeImageDiffView.xbf',
        'NativeSubmoduleDiffView.xbf',
        'WinGit.Native.pri',
        'Assets\icon-logo.ico',
        'WinGit.Native.exe',
        'apply-native-update.ps1',
        'verify-update-package.ps1',
        'ReleaseNotes.txt',
        'Acknowledgements.txt',
        'LICENSE.txt'
    )

    foreach ($relativePath in $requiredPublishFiles) {
        $publishedFile = Join-Path $publishPath $relativePath
        if (-not (Test-Path -LiteralPath $publishedFile -PathType Leaf)) {
            throw "Publish output is missing required file: $relativePath"
        }
    }
}
catch {
    Write-Error $_
    exit 1
}

exit 0
