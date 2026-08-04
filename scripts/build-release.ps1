[CmdletBinding()]
param(
    [Parameter()]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string]$Version = '0.3.7',

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
$installerScript = Join-Path $repositoryRoot 'installer\CodexAutoReset.iss'
$desktopAppSource = Join-Path $repositoryRoot `
    'src\CodexAutoReset.Desktop\App.xaml.cs'

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

if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot 'CodexAutoReset.sln'))) {
    throw 'The release script must run from the CodexAutoReset repository.'
}

$installerSource = Get-Content -LiteralPath $installerScript -Raw
$desktopSource = Get-Content -LiteralPath $desktopAppSource -Raw
$installerMutex = [regex]::Match(
    $installerSource,
    '(?m)^AppMutex=(?<name>[^\r\n]+)$')
$desktopMutex = [regex]::Match(
    $desktopSource,
    'UninstallGuardMutexName\s*=\s*@"(?<name>[^"]+)"')
if (-not $installerMutex.Success -or -not $desktopMutex.Success) {
    throw 'The installer and desktop app must both declare the uninstall guard mutex.'
}

if (-not [string]::Equals(
        $installerMutex.Groups['name'].Value,
        $desktopMutex.Groups['name'].Value,
        [System.StringComparison]::Ordinal)) {
    throw 'The installer and desktop app uninstall guard mutex names must match exactly.'
}

Reset-ArtifactDirectory -Path $publishDirectory
Reset-ArtifactDirectory -Path $installerDirectory
Reset-ArtifactDirectory -Path $releaseDirectory

Push-Location $repositoryRoot
try {
    Invoke-DotNet -Arguments @('restore', 'CodexAutoReset.sln', '--locked-mode')
    Invoke-DotNet -Arguments @(
        'build',
        'CodexAutoReset.sln',
        '-c', 'Release',
        '--no-restore',
        "-p:Version=$Version")
    Invoke-DotNet -Arguments @(
        'test',
        'CodexAutoReset.sln',
        '-c', 'Release',
        '--no-build')
    Invoke-DotNet -Arguments @(
        'format',
        'CodexAutoReset.sln',
        '--no-restore',
        '--verify-no-changes')
    Invoke-DotNet -Arguments @(
        'restore',
        'src\CodexAutoReset.Desktop\CodexAutoReset.Desktop.csproj',
        '-r', 'win-x64',
        '--locked-mode')
    Invoke-DotNet -Arguments @(
        'publish',
        'src\CodexAutoReset.Desktop\CodexAutoReset.Desktop.csproj',
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
        $_.Extension -in @('.pdb', '.log', '.jsonl', '.tmp', '.dmp') -or
        $_.Name -like '*.invalid*' -or
        $_.Name -in @(
            'settings.json',
            'state.json',
            'live-state.json',
            'live-safety-block.json',
            'live-safety-block.json.tmp',
            'usage-reset-state.json',
            'compatibility-notification-state.json',
            'compatibility-notification-state.json.tmp',
            'instance.lock')
    }
if ($forbiddenFiles) {
    throw "Forbidden runtime or debug files were published: $($forbiddenFiles.FullName -join ', ')"
}

$mainExecutable = Join-Path $publishDirectory 'CodexAutoReset.exe'
if (-not (Test-Path -LiteralPath $mainExecutable -PathType Leaf)) {
    throw "The published application executable was not found: $mainExecutable"
}

$runtimeConfigPath = Join-Path $publishDirectory 'CodexAutoReset.runtimeconfig.json'
if (-not (Test-Path -LiteralPath $runtimeConfigPath -PathType Leaf)) {
    throw "The published runtime configuration was not found: $runtimeConfigPath"
}

$runtimeConfig = Get-Content -LiteralPath $runtimeConfigPath -Raw |
    ConvertFrom-Json
$binaryFormatterSetting = $runtimeConfig.runtimeOptions.configProperties.
    PSObject.Properties[
        'System.Runtime.Serialization.EnableUnsafeBinaryFormatterSerialization']
if ($null -eq $binaryFormatterSetting -or
    $binaryFormatterSetting.Value -ne $false) {
    throw 'The published application must explicitly disable unsafe BinaryFormatter serialization.'
}

$legacyBrandedFiles = Get-ChildItem -LiteralPath $publishDirectory -Recurse -File |
    Where-Object { $_.Name -like 'CodexResetGuard*' }
if ($legacyBrandedFiles) {
    throw "Legacy-branded files were published: $($legacyBrandedFiles.FullName -join ', ')"
}

$portableArchive = Join-Path $releaseDirectory 'CodexAutoReset-Portable-x64.zip'
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

& $InnoCompilerPath `
    "/DAppVersion=$Version" `
    "/DPublishDir=$publishDirectory" `
    $installerScript
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup failed with exit code $LASTEXITCODE"
}

$compiledInstaller = Join-Path $installerDirectory 'CodexAutoReset-Setup-x64.exe'
if (-not (Test-Path -LiteralPath $compiledInstaller -PathType Leaf)) {
    throw "The installer was not produced: $compiledInstaller"
}

$releaseInstaller = Join-Path $releaseDirectory 'CodexAutoReset-Setup-x64.exe'
Copy-Item -LiteralPath $compiledInstaller -Destination $releaseInstaller

Get-ChildItem -LiteralPath $releaseDirectory -File |
    Sort-Object Name |
    Select-Object Name, Length, LastWriteTime
