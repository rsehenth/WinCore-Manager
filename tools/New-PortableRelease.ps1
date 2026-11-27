[CmdletBinding()]
param(
    [string] $Dotnet = 'dotnet',
    [string] $PortableDirectory = (Join-Path $PSScriptRoot '../publish/portable'),
    [string] $ReleaseDirectory = (Join-Path $PSScriptRoot '../artifacts/release')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$app = 'src/WinCoreManager/WinCoreManager.csproj'
$helper = 'src/WinCoreManager.Update/WinCoreManager.Update.csproj'
$runtime = '10.0.15'
$encoding = [Text.UTF8Encoding]::new($false)

function Get-Sha256([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

function Publish-Project([string] $Project, [string] $Destination, [string[]] $ExtraArguments) {
    $options = @(
        '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-warnaserror',
        "-p:RuntimeFrameworkVersion=$runtime", '-p:DebugType=None', '-p:DebugSymbols=false',
        '-p:IncludeSourceRevisionInInformationalVersion=false', '--artifacts-path', "$scratch/build",
        '-o', $Destination
    )
    & $Dotnet publish $Project @options @ExtraArguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Publish failed for $Project ($LASTEXITCODE)." }
}

$portable = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PortableDirectory)
$release = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($ReleaseDirectory)
if (Test-Path -LiteralPath $portable) { throw "Portable directory already exists: $portable" }
$parent = Split-Path -Parent $portable
$null = New-Item -ItemType Directory -Force -Path $parent, $release
$scratch = (New-Item -ItemType Directory -Path (Join-Path $parent ".wincore-release-$([guid]::NewGuid().ToString('N'))")).FullName
$draft = Join-Path $scratch 'WinCoreManager'
Push-Location -LiteralPath $repo
try {
    Publish-Project $app $draft @('-p:WinCoreBuildChannel=Candidate')
    $appHost = Join-Path $draft 'WinCoreManager.exe'
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($appHost).ProductVersion
    if ($version -cnotmatch '^\d+\.\d+\.\d+-rc\.\d+$') { throw "Invalid release candidate version: $version" }
    $zipName = "WinCoreManager-$version-win-x64-portable.zip"
    $zip = Join-Path $release $zipName
    $sums = Join-Path $release 'SHA256SUMS.txt'
    foreach ($file in @($zip, $sums)) {
        if (Test-Path -LiteralPath $file) { throw "Release artifact already exists: $file" }
    }

    $helperDirectory = Join-Path $scratch 'helper'
    Publish-Project $helper $helperDirectory @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
    $updateExe = Join-Path $helperDirectory 'WinCoreManager.Update.exe'
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($updateExe).ProductVersion -cne $version) {
        throw 'Update helper version differs from the application.'
    }
    Copy-Item -LiteralPath $updateExe -Destination $draft
    $null = New-Item -ItemType Directory -Path "$draft/Recovery", "$draft/Data"
    Copy-Item -LiteralPath $updateExe -Destination "$draft/Recovery/WinCoreManager.Recover.exe"
    Copy-Item -LiteralPath 'tasks/RECUPERACAO-RECOVERY.md' -Destination $draft

    $settings = Join-Path $draft 'settings.json'
    [IO.File]::WriteAllText($settings, '{"PortableMode":true}' + [char]10, $encoding)
    $excluded = @('settings.json', 'RECUPERACAO-RECOVERY.md', 'Recovery/WinCoreManager.Recover.exe')
    $paths = [string[]] @(Get-ChildItem -LiteralPath $draft -File -Recurse | ForEach-Object {
        $_.FullName.Substring($draft.Length + 1).Replace('\', '/')
    } | Where-Object { $_ -cnotin $excluded })
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    $rows = foreach ($path in $paths) { "$(Get-Sha256 (Join-Path $draft $path))  $path" }
    [IO.File]::WriteAllLines("$draft/WinCoreManager.package.sha256", [string[]] $rows, $encoding)

    $draftZip = Join-Path $scratch $zipName
    [IO.Compression.ZipFile]::CreateFromDirectory($draft, $draftZip, [IO.Compression.CompressionLevel]::Optimal, $true)
    $draftSums = Join-Path $scratch 'SHA256SUMS.txt'
    [IO.File]::WriteAllLines($draftSums, @(
        "$(Get-Sha256 $draftZip)  $zipName"
        "$(Get-Sha256 $appHost)  WinCoreManager/WinCoreManager.exe"
    ), $encoding)

    if ((Resolve-Path -LiteralPath $draft).Path -cne (Join-Path $scratch 'WinCoreManager') -or
        ((Get-Item -LiteralPath $draft).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'Unexpected portable build path.'
    }
    [IO.Directory]::Move($draft, $portable)
    Move-Item -LiteralPath $draftZip -Destination $zip
    Move-Item -LiteralPath $draftSums -Destination $sums
    Write-Host "Created $portable and $zip"
}
finally {
    Pop-Location
    $work = Get-Item -LiteralPath $scratch
    if ($work.Parent.FullName -ieq (Resolve-Path -LiteralPath $parent).Path -and
        $work.Name -cmatch '^\.wincore-release-[a-f0-9]{32}$' -and
        !($work.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        Remove-Item -LiteralPath $work.FullName -Recurse -Force
    }
}
