[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0',

    [Parameter()]
    [string]$InnoCompilerPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..'))
$artifactsRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $repositoryRoot 'artifacts'))
$publishDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot 'staging\publish\win-x64'))
$installerDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot 'installer'))
$releaseDirectory = [System.IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot 'release'))
$artifactPrefix = $artifactsRoot.TrimEnd('\') + '\'

function Reset-ArtifactDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $resolved = [System.IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith(
            $artifactPrefix,
            [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to reset a directory outside the artifacts root: $resolved"
    }

    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }

    New-Item -ItemType Directory -Path $resolved | Out-Null
}

function Invoke-DotNet {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet failed with exit code $LASTEXITCODE"
    }
}

if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'CodexResetGuard.sln'))) {
    throw 'The release script must run from the CodexResetGuard repository.'
}

Reset-ArtifactDirectory -Path $publishDirectory
Reset-ArtifactDirectory -Path $installerDirectory
Reset-ArtifactDirectory -Path $releaseDirectory

Push-Location $repositoryRoot
try {
    Invoke-DotNet -Arguments @('restore', 'CodexResetGuard.sln', '--locked-mode')
    Invoke-DotNet -Arguments @(
        'build',
        'CodexResetGuard.sln',
        '-c', 'Release',
        '--no-restore',
        "-p:Version=$Version")
    Invoke-DotNet -Arguments @(
        'test',
        'CodexResetGuard.sln',
        '-c', 'Release',
        '--no-build')
    Invoke-DotNet -Arguments @(
        'format',
        'CodexResetGuard.sln',
        '--no-restore',
        '--verify-no-changes')
    Invoke-DotNet -Arguments @(
        'restore',
        'src\CodexResetGuard.Desktop\CodexResetGuard.Desktop.csproj',
        '-r', 'win-x64',
        '--locked-mode')
    Invoke-DotNet -Arguments @(
        'publish',
        'src\CodexResetGuard.Desktop\CodexResetGuard.Desktop.csproj',
        '-c', 'Release',
        '--no-restore',
        '-p:PublishProfile=win-x64',
        "-p:Version=$Version",
        "-p:PublishDir=$publishDirectory")
}
finally {
    Pop-Location
}

$forbiddenFiles = Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
    Where-Object {
        $_.Extension -in @('.pdb', '.log') -or
        $_.Name -in @(
            'settings.json',
            'state.json',
            'live-state.json',
            'live-safety-block.json',
            'live-safety-block.json.tmp',
            'instance.lock')
    }
if ($forbiddenFiles) {
    throw "Forbidden runtime or debug files were published: $($forbiddenFiles.FullName -join ', ')"
}

$portableArchive = Join-Path $releaseDirectory 'CodexResetGuard-Portable-x64.zip'
Compress-Archive -Path (Join-Path $publishDirectory '*') `
    -DestinationPath $portableArchive `
    -CompressionLevel Optimal

if ([string]::IsNullOrWhiteSpace($InnoCompilerPath)) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) {
        $InnoCompilerPath = $command.Source
    }
    else {
        $perUserCompiler = Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
        if (Test-Path -LiteralPath $perUserCompiler -PathType Leaf) {
            $InnoCompilerPath = $perUserCompiler
        }
        else {
            $InnoCompilerPath = 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
        }
    }
}

$InnoCompilerPath = [System.IO.Path]::GetFullPath($InnoCompilerPath)
if (-not (Test-Path -LiteralPath $InnoCompilerPath -PathType Leaf)) {
    throw "Inno Setup compiler was not found: $InnoCompilerPath"
}

$installerScript = Join-Path $repositoryRoot 'installer\CodexResetGuard.iss'
& $InnoCompilerPath `
    "/DAppVersion=$Version" `
    "/DPublishDir=$publishDirectory" `
    $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}

$compiledInstaller = Join-Path $installerDirectory 'CodexResetGuard-Setup-x64.exe'
if (-not (Test-Path -LiteralPath $compiledInstaller -PathType Leaf)) {
    throw "The installer was not produced: $compiledInstaller"
}

$releaseInstaller = Join-Path $releaseDirectory 'CodexResetGuard-Setup-x64.exe'
Copy-Item -LiteralPath $compiledInstaller -Destination $releaseInstaller

$checksumFile = Join-Path $releaseDirectory 'SHA256SUMS.txt'
$releaseFiles = @($releaseInstaller, $portableArchive)
$checksumLines = foreach ($file in $releaseFiles) {
    $hash = Get-FileHash -LiteralPath $file -Algorithm SHA256
    "{0}  {1}" -f $hash.Hash.ToLowerInvariant(), (Split-Path $file -Leaf)
}
[System.IO.File]::WriteAllLines(
    $checksumFile,
    $checksumLines,
    [System.Text.UTF8Encoding]::new($false))

Get-ChildItem -LiteralPath $releaseDirectory -File |
    Sort-Object Name |
    Select-Object Name, Length, LastWriteTime
