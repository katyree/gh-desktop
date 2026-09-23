[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',
    [string] $OutputPath = (Join-Path $PSScriptRoot 'artifacts\win-x64'),
    [string] $CodexRuntimePackageRoot,
    [string] $GitRuntimePackageRoot
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

foreach ($runtimeRoot in @(
        @{ Name = 'CodexRuntimePackageRoot'; Path = $CodexRuntimePackageRoot },
        @{ Name = 'GitRuntimePackageRoot'; Path = $GitRuntimePackageRoot }
    )) {
    if (-not [string]::IsNullOrWhiteSpace($runtimeRoot.Path)) {
        if (-not (Test-Path -LiteralPath $runtimeRoot.Path -PathType Container)) {
            throw "$($runtimeRoot.Name) does not exist: $($runtimeRoot.Path)"
        }

        $msbuildProperties += "-p:$($runtimeRoot.Name)=$($runtimeRoot.Path)"
    }
}

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
        'ReleaseNotes.txt',
        'Acknowledgements.txt',
        'LICENSE.txt',
        'codex\codex-LICENSE.txt',
        'git\LICENSE.txt',
        'git\dugite-LICENSE',
        'codex\package.json',
        'codex\vendor\x86_64-pc-windows-msvc\bin\codex.exe',
        'codex\vendor\x86_64-pc-windows-msvc\bin\codex-code-mode-host.exe',
        'codex\vendor\x86_64-pc-windows-msvc\codex-path\rg.exe',
        'codex\vendor\x86_64-pc-windows-msvc\codex-resources\codex-command-runner.exe',
        'codex\vendor\x86_64-pc-windows-msvc\codex-resources\codex-windows-sandbox-setup.exe',
        'git\cmd\git.exe',
        'git\mingw64\bin\git.exe',
        'git\mingw64\libexec\git-core\git-lfs.exe',
        'git\mingw64\libexec\git-core\git-credential-wincred.exe',
        'git\usr\bin\sh.exe'
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
