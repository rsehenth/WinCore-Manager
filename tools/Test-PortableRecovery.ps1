[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Portable,
    [Parameter(Mandatory)] [string] $Dotnet,
    [switch] $PrepareOnly
)

$ErrorActionPreference = 'Stop'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run from a non-elevated session.'
}
Add-Type -AssemblyName System.Security, System.IO.Compression.FileSystem
$source = (Resolve-Path -LiteralPath $Portable).Path
$sdk = (Resolve-Path -LiteralPath $Dotnet).Path
$scratch = Join-Path ([IO.Path]::GetTempPath()) "wincore-process-$([guid]::NewGuid().ToString('N'))"
$null = New-Item -ItemType Directory -Path $scratch
$published = Join-Path $scratch 'signed-test-build'
$recoveryExe = 'WinCoreManager.Recover.exe'
$mainExe = 'WinCoreManager.exe'
$updater = 'WinCoreManager.Update.exe'
$manifest = 'WinCoreManager.package.sha256'
$keyFile = Join-Path $scratch 'test-public-key.txt'
$sha = [Security.Cryptography.SHA256]::Create()
$signer = [Security.Cryptography.ECDsaCng]::new(256)

function Assert-Fixture([bool] $Condition, [string] $Description) {
    if (!$Condition) { throw $Description }
}

function Write-Signature([string] $Path) {
    $signature = $signer.SignData([IO.File]::ReadAllBytes($Path), [Security.Cryptography.HashAlgorithmName]::SHA256)
    [IO.File]::WriteAllBytes("$Path.sig", $signature)
}

function Get-FixtureHash([string] $Path) {
    (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash
}

function Write-TestPackage([string] $Path, [bool] $Incoming) {
    New-Item -ItemType Directory -Force -Path $Path | Out-Null
    foreach ($name in @($mainExe, 'WinCoreManager.dll')) {
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination (Join-Path $Path $name)
    }
    Copy-Item -LiteralPath (Join-Path $source $mainExe) -Destination (Join-Path $Path $updater)
    [IO.File]::WriteAllText((Join-Path $Path 'fixture.dll'), "disposable payload $Incoming")
    if ($Incoming) {
        $exe = [IO.File]::Open((Join-Path $Path $mainExe), [IO.FileMode]::Append)
        try { $exe.WriteByte(123) } finally { $exe.Dispose() }
    }
    $lines = foreach ($name in @($mainExe, $updater, 'WinCoreManager.dll', 'fixture.dll')) {
        "$(Get-FixtureHash (Join-Path $Path $name))  $name"
    }
    $catalog = Join-Path $Path $manifest
    [IO.File]::WriteAllLines($catalog, $lines)
    Write-Signature $catalog
}

function New-RecoveryFixture([string] $Name, [string] $State) {
    $root = (New-Item -ItemType Directory -Path (Join-Path $scratch $Name)).FullName
    $jobId = [guid]::NewGuid().ToString('N')
    $work = Join-Path $root ".wincore-update\$jobId"
    $old = Join-Path $work 'previous'
    $new = Join-Path $work 'incoming'
    Write-TestPackage $old $false
    Write-TestPackage $new $true
    $zip = Join-Path $work 'release.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($new, $zip)
    Write-Signature $zip
    $fields = @('WinCoreManager.Recovery.v1', $root.ToUpperInvariant(), $jobId.ToUpperInvariant(),
        (Get-FixtureHash (Join-Path $old $manifest)), (Get-FixtureHash (Join-Path $old "$manifest.sig")),
        (Get-FixtureHash (Join-Path $new $manifest)), (Get-FixtureHash (Join-Path $new "$manifest.sig")))
    $binding = $sha.ComputeHash([Text.Encoding]::UTF8.GetBytes(($fields -join [char]0)))
    $receipt = [Security.Cryptography.ProtectedData]::Protect($binding, $null, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes((Join-Path $work 'authorization.dpapi'), $receipt)
    [IO.File]::WriteAllBytes((Join-Path $work 'pending'), [byte[]]@())
    $recovery = (New-Item -ItemType Directory -Path (Join-Path $root 'Recovery')).FullName
    $exe = Join-Path $recovery $recoveryExe
    Copy-Item -LiteralPath (Join-Path $published $updater) -Destination $exe
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot '../tasks/RECUPERACAO-RECOVERY.md') -Destination $root
    New-Item -ItemType Directory -Path (Join-Path $root 'Data') | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'Data/user-notes.txt'), 'disposable user bytes')
    [IO.File]::WriteAllText((Join-Path $root 'settings.json'), '{"PortableMode":true,"Language":"en-US","Theme":"Light"}')
    $active = if ($State -eq 'before') { $old } else { $new }
    Get-ChildItem -LiteralPath $active -File | Copy-Item -Destination $root
    switch ($State) {
        'during' { Copy-Item -LiteralPath (Join-Path $old $manifest) -Destination $root -Force }
        'absent' { Remove-Item -LiteralPath (Join-Path $root $mainExe) }
        'corrupt' { [IO.File]::WriteAllText((Join-Path $root $mainExe), 'corrupt primary apphost fixture') }
        'managed-unavailable' { [IO.File]::WriteAllText((Join-Path $root 'WinCoreManager.dll'), 'corrupt managed app fixture') }
    }
    return @{ Root = $root; Work = $work; Old = $old; Exe = $exe; Recovery = $recovery }
}

function Start-RecoveryProcess([string] $Exe, [string] $Arguments = '--quiet') {
    $start = [Diagnostics.ProcessStartInfo]::new($Exe, $Arguments)
    $start.UseShellExecute = $false
    $start.WorkingDirectory = Split-Path -Parent $Exe
    $start.EnvironmentVariables['DOTNET_BUNDLE_EXTRACT_BASE_DIR'] = Join-Path $scratch 'bundle-cache'
    return [Diagnostics.Process]::Start($start)
}

function Invoke-RecoveryProcess([string] $Exe, [string] $Arguments = '--quiet') {
    $worker = Start-RecoveryProcess $Exe $Arguments
    try {
        if (!$worker.WaitForExit(30000)) {
            $worker.Kill()
            $worker.WaitForExit()
            throw 'Recovery process timed out.'
        }
        return $worker.ExitCode
    } finally { $worker.Dispose() }
}

function Assert-RolledBack($Fixture) {
    Assert-Fixture (Test-Path -LiteralPath (Join-Path $Fixture.Work 'rolled-back')) 'Missing rolled-back marker.'
    Assert-Fixture (!(Test-Path -LiteralPath (Join-Path $Fixture.Work 'pending'))) 'Unexpected pending marker.'
    foreach ($item in Get-ChildItem -LiteralPath $Fixture.Old -File) {
        $same = (Get-FixtureHash $item.FullName) -eq (Get-FixtureHash (Join-Path $Fixture.Root $item.Name))
        Assert-Fixture $same "Previous payload mismatch in $($item.Name)."
    }
    Assert-Fixture ((Get-Content -LiteralPath (Join-Path $Fixture.Root 'Data/user-notes.txt') -Raw) -eq 'disposable user bytes') 'User data changed.'
    Assert-Fixture ((Get-Content -LiteralPath (Join-Path $Fixture.Root 'settings.json') -Raw) -eq '{"PortableMode":true,"Language":"en-US","Theme":"Light"}') 'Settings changed.'
}

try {
    # P-256 SubjectPublicKeyInfo prefix followed by X and Y from the CNG public blob.
    $header = [byte[]]@(0x30,0x59,0x30,0x13,0x06,0x07,0x2A,0x86,0x48,0xCE,0x3D,0x02,0x01,
        0x06,0x08,0x2A,0x86,0x48,0xCE,0x3D,0x03,0x01,0x07,0x03,0x42,0x00,0x04)
    $publicBlob = $signer.Key.Export([Security.Cryptography.CngKeyBlobFormat]::EccPublicBlob)
    [IO.File]::WriteAllText($keyFile, [Convert]::ToBase64String([byte[]]($header + $publicBlob[8..71])))
    $project = Join-Path $PSScriptRoot '../src/WinCoreManager.Update/WinCoreManager.Update.csproj'
    $buildArgs = @('publish', $project, '-c', 'Release', '-r', 'win-x64', '--self-contained',
        '--artifacts-path', (Join-Path $scratch 'build'), '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:PublishTrimmed=true', '-p:RuntimeFrameworkVersion=10.0.15',
        '-p:DebugType=None', "-p:PortableRecoveryTestKeyFile=$keyFile", '-warnaserror', '--nologo', '-o', $published)
    & $sdk @buildArgs | Out-Host
    Assert-Fixture ($LASTEXITCODE -eq 0) 'The isolated test helper build failed.'
    Write-Output "Evidence directory $scratch"
    if ($PrepareOnly) {
        $prepared = New-RecoveryFixture 'prepared' 'absent'
        Write-Output "Signed disposable installation $($prepared.Root)"
        return
    }

    foreach ($state in @('before', 'during', 'after', 'absent', 'corrupt', 'managed-unavailable')) {
        $fixture = New-RecoveryFixture "state-$state" $state
        Assert-Fixture ((Invoke-RecoveryProcess $fixture.Exe) -eq 0) "Recovery failed for $state."
        Assert-RolledBack $fixture
        Assert-Fixture ((Invoke-RecoveryProcess $fixture.Exe) -eq 0) "Repeat failed for $state."
        $logs = (Get-ChildItem -LiteralPath $fixture.Recovery -Filter 'recovery-*.log' | Get-Content) -join ' '
        Assert-Fixture ($logs -match 'Recovered' -and $logs -match 'SignedPackageVerified') "Logs incomplete for $state."
        Write-Output "PASS state $state and idempotence"
    }

    $aliasFixture = New-RecoveryFixture 'physical-root' 'absent'
    $aliasParent = Join-Path ([IO.Path]::GetTempPath()) "wincore-alias-$([guid]::NewGuid().ToString('N'))"
    try {
        $null = New-Item -ItemType Junction -Path $aliasParent -Target $scratch
        $throughAlias = Join-Path $aliasParent "physical-root/Recovery/$recoveryExe"
        Assert-Fixture ((Invoke-RecoveryProcess $throughAlias) -eq 0) 'Recovery failed through a legitimate ancestral junction.'
        Assert-RolledBack $aliasFixture
    } finally {
        if (Test-Path -LiteralPath $aliasParent) { [IO.Directory]::Delete($aliasParent) }
    }
    Write-Output 'PASS independent process through ancestral junction'

    $multiple = New-RecoveryFixture 'multiple-pending' 'absent'
    $otherWork = Join-Path $multiple.Root ".wincore-update\$([guid]::NewGuid().ToString('N'))"
    New-Item -ItemType Directory -Path $otherWork | Out-Null
    [IO.File]::WriteAllBytes((Join-Path $otherWork 'pending'), [byte[]]@())
    Assert-Fixture ((Invoke-RecoveryProcess $multiple.Exe) -eq 3) 'Recovery selected an ambiguous transaction.'
    Assert-Fixture (!(Test-Path -LiteralPath (Join-Path $multiple.Root $mainExe))) 'Ambiguous transactions mutated the fixture.'
    Write-Output 'PASS multiple pending transactions are preserved'

    $unsigned = New-RecoveryFixture 'release-key-block' 'absent'
    Copy-Item -LiteralPath (Join-Path $source "Recovery/$recoveryExe") -Destination $unsigned.Exe -Force
    Assert-Fixture ((Invoke-RecoveryProcess $unsigned.Exe) -eq 3) 'Release helper accepted a test identity.'
    Assert-Fixture (!(Test-Path -LiteralPath (Join-Path $unsigned.Root $mainExe))) 'Release helper mutated the fixture.'
    Assert-Fixture (Test-Path -LiteralPath (Join-Path $unsigned.Work 'pending')) 'Release helper finalized the fixture.'
    Assert-Fixture ((Invoke-RecoveryProcess $unsigned.Exe '--quiet elsewhere') -eq 2) 'Recovery accepted path input.'
    Copy-Item -LiteralPath (Join-Path $published $updater) -Destination $unsigned.Exe -Force
    Assert-Fixture ((Invoke-RecoveryProcess $unsigned.Exe) -eq 0) 'The same fixture did not recover with the test-only helper.'
    Assert-RolledBack $unsigned
    Write-Output 'PASS release key separation and argument gate'

    $access = New-RecoveryFixture 'access-denied' 'absent'
    $before = Get-Acl -LiteralPath $access.Recovery
    $acl = Get-Acl -LiteralPath $access.Recovery
    $deny = [Security.AccessControl.FileSystemAccessRule]::new($identity.User, 'CreateFiles', 'Deny')
    $acl.AddAccessRule($deny)
    Set-Acl -LiteralPath $access.Recovery -AclObject $acl
    try {
        Assert-Fixture ((Invoke-RecoveryProcess $access.Exe) -eq 5) 'Write access denial was not reported.'
        Assert-Fixture (Test-Path -LiteralPath (Join-Path $access.Work 'pending')) 'Access failure finalized the fixture.'
    } finally { Set-Acl -LiteralPath $access.Recovery -AclObject $before }
    Assert-Fixture ((Invoke-RecoveryProcess $access.Exe) -eq 0) 'Recovery failed after the original ACL was restored.'
    Assert-RolledBack $access
    Write-Output 'PASS permissions and retry'

    $blocked = New-RecoveryFixture 'concurrent-process' 'after'
    $lockFile = Join-Path $blocked.Root '.wincore-portable.lock'
    $lease = [IO.File]::Open($lockFile, 'OpenOrCreate', 'ReadWrite', 'None')
    try { Assert-Fixture ((Invoke-RecoveryProcess $blocked.Exe) -eq 4) 'Second process did not report a busy installation.' }
    finally { $lease.Dispose() }
    Assert-Fixture ((Invoke-RecoveryProcess $blocked.Exe) -eq 0) 'Recovery failed after the installation lock was released.'
    Assert-RolledBack $blocked
    Write-Output 'PASS exclusive installation lock across processes'

    $missing = New-RecoveryFixture 'invalid-helper' 'absent'
    Remove-Item -LiteralPath $missing.Exe
    Assert-Fixture (!(Test-Path -LiteralPath $missing.Exe)) 'Expected missing bootstrap.'
    [IO.File]::WriteAllText($missing.Exe, 'invalid disposable bootstrap')
    $rejected = $false
    try { $rejected = (Invoke-RecoveryProcess $missing.Exe) -ne 0 }
    catch { if ($_.Exception.InnerException -isnot [ComponentModel.Win32Exception]) { throw }; $rejected = $true }
    Assert-Fixture $rejected 'The OS did not reject an invalid bootstrap.'
    Assert-Fixture (Test-Path -LiteralPath (Join-Path $missing.Work 'pending')) 'Invalid bootstrap finalized the fixture.'
    Assert-Fixture (!(Test-Path -LiteralPath (Join-Path $missing.Root $mainExe))) 'Invalid bootstrap changed the installation.'
    Write-Output 'PASS missing and invalid bootstrap preserve the fixture'

    $crash = New-RecoveryFixture 'interrupted-recovery' 'after'
    $expectedMain = Get-FixtureHash (Join-Path $crash.Old $mainExe)
    $locked = [IO.File]::Open((Join-Path $crash.Root 'fixture.dll'), 'Open', 'Read', 'Read')
    try {
        $worker = Start-RecoveryProcess $crash.Exe
        try {
            $limit = [DateTime]::UtcNow.AddSeconds(20)
            $changed = $false
            while (!$worker.HasExited -and [DateTime]::UtcNow -lt $limit) {
                try { $changed = (Get-FixtureHash (Join-Path $crash.Root $mainExe)) -eq $expectedMain }
                catch { $changed = $false }
                if ($changed) { break }
                Start-Sleep -Milliseconds 15
            }
            Assert-Fixture ($changed -and !$worker.HasExited) 'Process did not reach a replacement during rollback.'
            $worker.Kill()
            $worker.WaitForExit()
        } finally {
            if (!$worker.HasExited) { $worker.Kill(); $worker.WaitForExit() }
            $worker.Dispose()
        }
    } finally { $locked.Dispose() }
    Assert-Fixture (Test-Path -LiteralPath (Join-Path $crash.Work 'pending')) 'Interrupted recovery lost its pending marker.'
    Assert-Fixture ((Invoke-RecoveryProcess $crash.Exe) -eq 0) 'Recovery failed after process termination.'
    Assert-RolledBack $crash
    Write-Output 'PASS killed recovery process resumes from a durable replacement'
} finally {
    $signer.Dispose()
    $sha.Dispose()
}
