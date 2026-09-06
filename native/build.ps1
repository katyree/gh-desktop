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
    Invoke-Dotnet @('restore', $projectPath, '--runtime', $Runtime, '-p:Platform=x64')
    Invoke-Dotnet @('build', $projectPath, '--configuration', $Configuration, '--runtime', $Runtime, '--no-restore', '-p:Platform=x64')
    Invoke-Dotnet @('publish', $projectPath, '--configuration', $Configuration, '--runtime', $Runtime, '--no-restore', '--self-contained', 'true', '-p:Platform=x64', '--output', $OutputPath)
}
catch {
    Write-Error $_
    exit 1
}

exit 0
