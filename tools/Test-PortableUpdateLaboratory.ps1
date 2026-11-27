param([Parameter(Mandatory)][string]$Portable,[Parameter(Mandatory)][string]$Dotnet)
$ErrorActionPreference = 'Stop'
$portablePath = (Resolve-Path -LiteralPath $Portable).Path
$sdk = (Resolve-Path -LiteralPath $Dotnet).Path
$lab = Join-Path ([IO.Path]::GetTempPath()) "wincore-update-fixture-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $lab
$src = Join-Path $lab 'src'
Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../src') -Destination $src -Recurse
$fixture = (New-Item -ItemType Directory -Path "$lab/fixture").FullName
$helper = Join-Path $lab 'helper'
$manifest = 'WinCoreManager.package.sha256'
$updater = 'WinCoreManager.Update.exe'
$appExe = 'WinCoreManager.exe'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Fixture requires a standard session.' }
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$signer = [Security.Cryptography.ECDsaCng]::new(256)
$utf8 = [Text.UTF8Encoding]::new($false)
$publish = @('-c','Release','-r','win-x64','--self-contained','-p:RuntimeFrameworkVersion=10.0.15',
    '-p:DebugType=None','-p:DebugSymbols=false','-warnaserror','--nologo')

function Check([bool]$Condition,[string]$Message) { if (!$Condition) { throw $Message } }
function Hash([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash }
function Sign([string]$Path) {
    $signature = $signer.SignData([IO.File]::ReadAllBytes($Path), [Security.Cryptography.HashAlgorithmName]::SHA256)
    [IO.File]::WriteAllBytes("$Path.sig", $signature)
}
function Publish([string]$Project,[string]$Output,[string[]]$Options) {
    & $sdk publish $Project -o $Output @publish @Options | Out-Host
    Check ($LASTEXITCODE -eq 0) "Fixture build failed for $Project"
}
function Start-Fixture([string]$Exe,[string]$Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new($Exe, $Arguments)
    $start.WorkingDirectory = Split-Path -Parent $Exe
    $start.UseShellExecute = $false
    $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $lab 'bundle-cache'
    return [Diagnostics.Process]::Start($start)
}
function Wait-Marker([string]$Path) {
    $deadline = [DateTime]::UtcNow.AddSeconds(20)
    while (!(Test-Path -LiteralPath $Path) -and [DateTime]::UtcNow -lt $deadline) { Start-Sleep -Milliseconds 100 }
    Check (Test-Path -LiteralPath $Path) "Missing fixture marker $Path"
}
function Copy-Payload([string]$App,[string]$Destination) {
    $null = New-Item -ItemType Directory -Path $Destination
    Copy-Item -Path "$App/*" -Destination $Destination -Recurse
    $null = New-Item -ItemType Directory -Path "$Destination/en-US"
    Copy-Item -LiteralPath "$portablePath/en-US/WinCoreManager.resources.dll" -Destination "$Destination/en-US"
    Copy-Item -LiteralPath "$portablePath/e_sqlite3.dll","$helper/$updater" -Destination $Destination
    $lines = foreach ($file in (Get-ChildItem -LiteralPath $Destination -File -Recurse | Sort-Object FullName)) {
        $relative = $file.FullName.Substring($Destination.Length + 1).Replace('\','/')
        "$(Hash $file.FullName)  $relative"
    }
    [IO.File]::WriteAllLines("$Destination/$manifest", $lines, $utf8)
    Sign "$Destination/$manifest"
}

try {
    $header = [byte[]]@(0x30,0x59,0x30,0x13,0x06,0x07,0x2A,0x86,0x48,0xCE,0x3D,0x02,0x01,
        0x06,0x08,0x2A,0x86,0x48,0xCE,0x3D,0x03,0x01,0x07,0x03,0x42,0x00,0x04)
    $blob = $signer.Key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    $labKey = [Convert]::ToBase64String([byte[]]($header + $blob[8..71]))
    $trustPath = "$src/WinCoreManager/Services/SelfUpdate/ReleaseTrust.cs"
    $trust = [IO.File]::ReadAllText($trustPath)
    Check ($trust.Contains('PublisherPublicKey = null;')) 'Fixture requires an unconfigured source copy.'
    [IO.File]::WriteAllText($trustPath, $trust.Replace('PublisherPublicKey = null;', ('PublisherPublicKey = "{0}";' -f $labKey)), $utf8)
    $programPath = "$src/WinCoreManager.Update/Program.cs"
    $program = [IO.File]::ReadAllText($programPath)
    Check ($program.Contains('NotifyFailure(root);')) 'Failure notification hook missing.'
    [IO.File]::WriteAllText($programPath, $program.Replace('NotifyFailure(root);', 'File.WriteAllText(Path.Combine(root, "Data", "failure"), ex.ToString());'), $utf8)
    Publish "$src/WinCoreManager.Update/WinCoreManager.Update.csproj" $helper @(
        '-p:PublishSingleFile=true','-p:IncludeNativeLibrariesForSelfExtract=true')

    [IO.File]::WriteAllText("$fixture/WinCoreManager.csproj", @'
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>WinCoreManager</AssemblyName>
  </PropertyGroup>
</Project>
'@, $utf8)
    [IO.File]::WriteAllText("$fixture/Program.cs", @'
using System.Reflection;
var version = Assembly.GetExecutingAssembly().GetName().Version!.ToString(3);
var data = Path.Combine(AppContext.BaseDirectory, "Data");
if (args is ["--verify-wincore-package", var expected])
{
    File.WriteAllText(Path.Combine(data, "verified"), version);
    return version == expected && !File.Exists(Path.Combine(data, "fail-probe")) ? 0 : 4;
}
if (args is ["--fixture-parent"])
{
    File.WriteAllText(Path.Combine(data, "parent-started"), version);
    for (var i = 0; i < 250 && !File.Exists(Path.Combine(data, "release-parent")); i++) Thread.Sleep(100);
    return 0;
}
File.WriteAllText(Path.Combine(data, "restarted"), version);
return 0;
'@, $utf8)
    foreach ($version in '0.2.0','0.2.1') {
        Publish "$fixture/WinCoreManager.csproj" "$lab/app-$version" @("-p:Version=$version",'--artifacts-path',"$lab/build-$version")
    }
    Copy-Payload "$lab/app-0.2.1" "$lab/incoming"
    $archive = "$lab/release.zip"
    $zip = [IO.Compression.ZipFile]::Open($archive, [IO.Compression.ZipArchiveMode]::Create)
    try {
        $incoming = Join-Path $lab 'incoming'
        foreach ($file in Get-ChildItem -LiteralPath $incoming -Recurse -File) {
            $relative = $file.FullName.Substring($incoming.Length + 1).Replace('\','/')
            $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $relative)
        }
    } finally { $zip.Dispose() }
    Sign $archive
    "Fixture ZIP SHA-256 $(Hash $archive)"

    foreach ($scenario in 'successful-restart','failed-probe-rollback') {
        $app = "$lab/$scenario"
        Copy-Payload "$lab/app-0.2.0" $app
        $null = New-Item -ItemType Directory -Path "$app/Recovery","$app/Data"
        Copy-Item -LiteralPath "$helper/$updater" -Destination "$app/Recovery/WinCoreManager.Recover.exe"
        $data = "$app/Data"
        [IO.File]::WriteAllText("$app/settings.json", '{"PortableMode":true,"Language":"pt-BR"}', $utf8)
        [IO.File]::WriteAllText("$data/history.db", 'disposable sentinel', $utf8)
        [IO.File]::WriteAllText("$data/receipt.json", '{"fixture":true}', $utf8)
        $original = Hash "$app/WinCoreManager.dll"
        $userFiles = @("$app/settings.json","$data/history.db","$data/receipt.json")
        $before = @($userFiles | ForEach-Object { Hash $_ })
        if ($scenario -eq 'failed-probe-rollback') { [IO.File]::WriteAllText("$data/fail-probe", '1', $utf8) }

        $job = (New-Item -ItemType Directory -Path "$app/.wincore-update/$([guid]::NewGuid().ToString('N'))").FullName
        Copy-Item -LiteralPath $archive,"$archive.sig","$helper/$updater" -Destination $job
        $parent = Start-Fixture "$app/$appExe" '--fixture-parent'
        try {
            Wait-Marker "$data/parent-started"
            $worker = Start-Fixture "$job/$updater" "0.2.1 $($parent.Id) $($parent.StartTime.ToUniversalTime().Ticks)"
            try {
                Start-Sleep -Milliseconds 350
                Check (!$worker.HasExited) 'Helper exited while parent was still running.'
                Check ((Hash "$app/WinCoreManager.dll") -eq $original) 'Active process was overwritten.'
                [IO.File]::WriteAllText("$data/release-parent",'1',$utf8)
                Check ($worker.WaitForExit(45000)) 'Updater helper timed out.'
                $expectedExit = if ($scenario -eq 'successful-restart') { 0 } else { 3 }
                if ($worker.ExitCode -ne $expectedExit -and (Test-Path "$data/failure")) { Get-Content "$data/failure" }
                Check ($worker.ExitCode -eq $expectedExit) "Fixture exit $($worker.ExitCode) in $scenario"
            } finally { $worker.Dispose() }
        } finally { $parent.Dispose() }

        Check ((Hash "$job/previous/WinCoreManager.dll") -eq $original) 'Backup differs from original.'
        Check ((Get-Content -LiteralPath "$data/verified" -Raw) -eq '0.2.1') 'Post-install probe did not run.'
        if ($scenario -eq 'successful-restart') {
            Wait-Marker "$data/restarted"
            Check ((Get-Content -LiteralPath "$data/restarted" -Raw) -eq '0.2.1') 'New version was not restarted.'
        } else {
            Check (Test-Path -LiteralPath "$job/rolled-back") 'Rollback marker missing.'
            Check ((Hash "$app/WinCoreManager.dll") -eq $original) 'Rollback did not restore original.'
        }
        for ($i=0;$i -lt $userFiles.Count;$i++) {
            Check ((Hash $userFiles[$i]) -eq $before[$i]) 'Fixture user data changed.'
        }
        "PASS $scenario"
    }
    "Isolated process evidence $lab"
} finally { $signer.Dispose() }
