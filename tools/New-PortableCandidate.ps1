[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Dotnet,
    [Parameter(Mandatory)] [string] $Destination,
    [Parameter(Mandatory)] [string] $AuditFilterFile,
    [string] $SdkVersion = '10.0.404',
    [string] $RuntimeVersion = '10.0.15'
)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$sdk = (Resolve-Path -LiteralPath $Dotnet).Path
$destinationRoot = [IO.Path]::GetFullPath($Destination)
$stagingRoot = [IO.Path]::GetFullPath((Join-Path $source 'staging'))
if (!$destinationRoot.StartsWith($stagingRoot + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The isolated candidate must be under the project staging directory.'
}
$portable = Join-Path $destinationRoot 'portable'
if (Test-Path -LiteralPath $portable) { throw 'The destination already contains a portable candidate.' }
$runId = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 8)
$evidence = Join-Path (Join-Path $destinationRoot 'evidencias') $runId
$null = New-Item -ItemType Directory -Path $evidence -Force
$scratch = Join-Path ([IO.Path]::GetTempPath()) "WinCoreCandidate-$runId"
$snapshot = Join-Path $scratch 'source'
$rebuild = Join-Path $scratch 'rebuild'
$draft = Join-Path $destinationRoot "portable.pending-$runId"
$null = New-Item -ItemType Directory -Path $snapshot, $rebuild, $draft
$utf8 = [Text.UTF8Encoding]::new($false)
$manifestName = 'WinCoreManager.package.sha256'
$startedUtc = [DateTime]::UtcNow
$commands = [Collections.Generic.List[object]]::new()
$testBatches = [Collections.Generic.List[object]]::new()

function Set-Lines([string] $Path, [string[]] $Lines) {
    [IO.File]::WriteAllText($Path, (($Lines -join "`n") + "`n"), $utf8)
}

function Get-TreeRows([string] $Root, [string[]] $Excluded = @(), [string] $Prefix = '') {
    $paths = [string[]]@(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | ForEach-Object {
        $_.FullName.Substring($Root.Length + 1).Replace('\', '/')
    } | Where-Object { $_ -notmatch '(^|/)(bin|obj|\.artifacts|TestResults)(/|$)' -and $_ -notin $Excluded })
    [Array]::Sort($paths, [StringComparer]::Ordinal)
    foreach ($relative in $paths) {
        $hash = (Get-FileHash -LiteralPath (Join-Path $Root $relative) -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $Prefix$relative"
    }
}

function Copy-SourceFiles([string] $From, [string] $To, [string[]] $RelativePaths) {
    foreach ($relative in $RelativePaths) {
        $inputPath = Join-Path $From $relative
        if ((Get-Item -LiteralPath $inputPath).Attributes -band [IO.FileAttributes]::ReparsePoint) {
            throw "A source input is a reparse point: $relative"
        }
        $targetPath = Join-Path $To $relative
        $parent = Split-Path -Parent $targetPath
        $null = New-Item -ItemType Directory -Path $parent -Force
        Copy-Item -LiteralPath $inputPath -Destination $targetPath
    }
}

function Invoke-Sdk([string] $Name, [string[]] $Arguments) {
    $log = Join-Path $evidence "$Name.log"
    $commands.Add([ordered]@{
        name = $Name; workingDirectory = (Get-Location).Path
        arguments = $Arguments; log = "evidencias/$runId/$Name.log"
    })
    # Windows PowerShell wraps native stderr as ErrorRecord even when the tool owns the exit status.
    $ErrorActionPreference = 'Continue'
    & $sdk @Arguments 2>&1 | ForEach-Object { "$_" } | Tee-Object -FilePath $log | Out-Host
    $exitCode = $LASTEXITCODE
    $ErrorActionPreference = 'Stop'
    if ($exitCode -ne 0) { throw "$Name failed ($exitCode). See $log." }
}

function Invoke-Batch([string] $Name, [string] $Filter = '', [string] $Settings = '', [int] $Expected = 0) {
    $arguments = @('test', $testAssembly, '--results-directory', $evidence, '--logger', "trx;LogFileName=$Name.trx")
    if ($Filter) { $arguments += @('--filter', $Filter) }
    if ($Settings) { $arguments += @('--settings', $Settings) }
    Invoke-Sdk $Name $arguments
    $resultPath = Join-Path $evidence "$Name.trx"
    [xml]$result = Get-Content -LiteralPath $resultPath -Raw
    $counters = $result.TestRun.ResultSummary.Counters
    $passed = [int]$counters.passed
    if ($passed -lt 1 -or [int]$counters.failed -ne 0 -or $passed -ne [int]$counters.total -or
        ($Expected -gt 0 -and $passed -ne $Expected)) {
        if ($Name -eq 'cross-volume') {
            throw 'Cross-volume integration requires TEMP and the quarantine scratch on distinct local NTFS volumes.'
        }
        throw "Batch $Name has incomplete coverage. Passed $passed, total $($counters.total), expected $Expected."
    }
    $testBatches.Add([ordered]@{
        name = $Name; id = [string]$result.TestRun.id; passed = $passed
        trx = "evidencias/$runId/$Name.trx"
        sha256 = (Get-FileHash -LiteralPath $resultPath).Hash.ToLowerInvariant()
    })
}

function Verify-Executable([string] $Name, [string] $File, [string[]] $Arguments) {
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $File
    $start.Arguments = ($Arguments -join ' ')
    $start.WorkingDirectory = Split-Path -Parent $File
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(30000)) { throw "$Name exceeded its 30-second verification deadline." }
        "$Name exit=$($process.ExitCode)" | Tee-Object -FilePath (Join-Path $evidence "$Name.log") | Out-Host
        if ($process.ExitCode -ne 0) { throw "$Name returned $($process.ExitCode)." }
    } finally { $process.Dispose() }
}

$previousTopology = $env:WINCORE_QUARANTINE_SECOND_VOLUME
$previousOrder = $env:WINCORE_TEST_COLLECTION_ORDER
try {
    $projectInputs = @('WinCoreManager.sln', 'LICENSE', 'THIRD-PARTY-NOTICES.md', 'LICENSE-BCUninstaller.txt', 'NOTICE-BCUninstaller.md',
        'tasks/RECUPERACAO-RECOVERY.md')
    foreach ($folder in @('src', 'tests', 'tools', 'distribution')) {
        $projectInputs += @(Get-ChildItem -LiteralPath (Join-Path $source $folder) -Recurse -File -Force | ForEach-Object {
            $_.FullName.Substring($source.Length + 1).Replace('\', '/')
        } | Where-Object { $_ -notmatch '(^|/)(bin|obj|\.artifacts|TestResults)(/|$)' })
    }
    $original = @{}
    foreach ($relative in $projectInputs) { $original[$relative] = (Get-FileHash -LiteralPath (Join-Path $source $relative)).Hash }
    Copy-SourceFiles $source $snapshot $projectInputs
    foreach ($relative in $projectInputs) {
        if ((Get-FileHash -LiteralPath (Join-Path $snapshot $relative)).Hash -ne $original[$relative] -or
            (Get-FileHash -LiteralPath (Join-Path $source $relative)).Hash -ne $original[$relative]) {
            throw "The source changed during snapshot creation: $relative"
        }
    }
    [IO.File]::WriteAllText((Join-Path $snapshot 'global.json'),
        ('{"sdk":{"version":"' + $SdkVersion + '","rollForward":"disable"}}'), $utf8)

    $sourceRows = @(Get-TreeRows $snapshot)
    $sourceManifest = Join-Path $evidence 'source.sha256'
    Set-Lines $sourceManifest $sourceRows
    $sourceHash = (Get-FileHash -LiteralPath $sourceManifest).Hash.ToLowerInvariant()
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sourceZip = Join-Path $evidence 'source-snapshot.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($snapshot, $sourceZip)
    [IO.Compression.ZipFile]::ExtractToDirectory($sourceZip, $rebuild)
    if (Compare-Object $sourceRows @(Get-TreeRows $rebuild) -CaseSensitive) {
        throw 'The extracted source archive differs from the snapshotted inputs.'
    }

    Push-Location -LiteralPath $snapshot
    try {
        $actualSdk = (& $sdk --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $actualSdk -ne $SdkVersion) { throw "Expected SDK $SdkVersion, got $actualSdk." }
        $runtimes = @(& $sdk --list-runtimes)
        if ($LASTEXITCODE -ne 0 -or !$runtimes.Where({ $_.StartsWith("Microsoft.WindowsDesktop.App $RuntimeVersion ") })) {
            throw "The test host is missing WindowsDesktop runtime $RuntimeVersion."
        }
        Set-Lines (Join-Path $evidence 'runtimes.txt') $runtimes
        $build = @('-c', 'Release', '-warnaserror', '--nologo', '-p:Deterministic=true')
        Invoke-Sdk 'build-release' (@('build', 'WinCoreManager.sln', '--artifacts-path', '.artifacts/build') + $build)
        $publish = $build + @('-r', 'win-x64', '--self-contained', "-p:RuntimeFrameworkVersion=$RuntimeVersion",
            '-p:DebugType=None', '-p:DebugSymbols=false')
        $appProject = 'src/WinCoreManager/WinCoreManager.csproj'
        $helperProject = 'src/WinCoreManager.Update/WinCoreManager.Update.csproj'
        $helperArguments = @('-p:PublishSingleFile=true', '-p:IncludeNativeLibrariesForSelfExtract=true')
        Invoke-Sdk 'publish-app' (@('publish', $appProject, '--artifacts-path', '.artifacts/publish-app', '-o', $draft) + $publish)
        $helperOutput = Join-Path $scratch 'helper'
        Invoke-Sdk 'publish-helper' (@('publish', $helperProject, '--artifacts-path', '.artifacts/publish-helper', '-o', $helperOutput) + $publish + $helperArguments)
        Push-Location -LiteralPath $rebuild
        try {
            $rebuildOutput = Join-Path $scratch 'app-rebuild'
            Invoke-Sdk 'publish-app-rebuild' (@('publish', $appProject, '--artifacts-path', '.artifacts/publish-app', '-o', $rebuildOutput) + $publish)
            $differences = @(Compare-Object @(Get-TreeRows $draft) @(Get-TreeRows $rebuildOutput) -CaseSensitive)
            $comparison = @("different rows=$($differences.Count)") +
                @($differences | ForEach-Object { "$($_.SideIndicator) $($_.InputObject)" })
            Set-Lines (Join-Path $evidence 'rebuild-comparison.txt') $comparison
        } finally { Pop-Location }

        $testDirectory = Join-Path $snapshot '.artifacts/build/bin/WinCoreManager.Tests/release'
        $testAssembly = Join-Path $testDirectory 'WinCoreManager.Tests.dll'
        $testedAppFiles = [ordered]@{}
        foreach ($relative in @('WinCoreManager.dll', 'en-US/WinCoreManager.resources.dll')) {
            Copy-Item -LiteralPath (Join-Path $draft $relative) -Destination (Join-Path $testDirectory $relative) -Force
            $testedAppFiles[$relative] = (Get-FileHash -LiteralPath (Join-Path $testDirectory $relative)).Hash.ToLowerInvariant()
        }
        Copy-Item -LiteralPath (Join-Path $draft 'WinCoreManager.dll') -Destination (Join-Path $testDirectory 'WinGetFixture/WinCoreManager.dll') -Force
        $testBinaryEvidence = Join-Path $evidence 'test-binaries'
        Copy-SourceFiles $testDirectory $testBinaryEvidence @('WinCoreManager.Tests.dll', 'WinCoreManager.dll',
            'en-US/WinCoreManager.resources.dll')
        $testAssemblyHash = (Get-FileHash -LiteralPath $testAssembly).Hash.ToLowerInvariant()
        $env:WINCORE_QUARANTINE_SECOND_VOLUME = Join-Path $evidence 'quarantine-scratch'
        $null = New-Item -ItemType Directory -Path $env:WINCORE_QUARANTINE_SECOND_VOLUME
        $forwardSettings = Join-Path $evidence 'serial.runsettings'
        Set-Lines $forwardSettings @('<RunSettings><RunConfiguration><MaxCpuCount>1</MaxCpuCount></RunConfiguration>',
            '<xUnit><ParallelizeTestCollections>false</ParallelizeTestCollections></xUnit></RunSettings>')
        $env:WINCORE_TEST_COLLECTION_ORDER = 'forward'
        Invoke-Batch 'targeted-12k' 'FullyQualifiedName~PackageCoordination12KTests' -Settings $forwardSettings
        Invoke-Batch 'cross-volume' 'Topology=CrossVolume' -Expected 3
        Invoke-Batch 'full-release' '' $forwardSettings
        $auditFilter = (Get-Content -LiteralPath $AuditFilterFile -Raw -Encoding UTF8).Trim()
        Invoke-Batch 'audit-selection-181' $auditFilter $forwardSettings -Expected 181
        Invoke-Batch 'targeted-12h' 'FullyQualifiedName~Audit12H'
        Invoke-Batch 'adversarial' ('FullyQualifiedName~WinGetOperationTests|FullyQualifiedName~WinGetCancellationTests|' +
            'FullyQualifiedName~WinGetServiceTests|FullyQualifiedName~ProcessRunnerTests|FullyQualifiedName~UninstallPolicyEnforcementTests|' +
            'FullyQualifiedName~UninstallPlannerTests|FullyQualifiedName~ResidueHardeningTests|FullyQualifiedName~PortableRecoveryTests|' +
            'FullyQualifiedName~ReleaseTrustTests|FullyQualifiedName~ResidueQuarantineTests')
        Invoke-Batch 'smoke-wpf' 'FullyQualifiedName~Etapa12e_navigates_all_twelve_pages|FullyQualifiedName~Etapa12g_navigates_and_closes_safely'
        Invoke-Batch 'combined-theme-language' 'FullyQualifiedName~ThemeTests|FullyQualifiedName~LocalizationTests|FullyQualifiedName~Etapa12eTests'
        if ((Get-FileHash -LiteralPath $testAssembly).Hash.ToLowerInvariant() -cne $testAssemblyHash) {
            throw 'The test assembly changed during acceptance.'
        }

        foreach ($relative in $testedAppFiles.Keys) {
            if ((Get-FileHash -LiteralPath (Join-Path $draft $relative)).Hash.ToLowerInvariant() -cne $testedAppFiles[$relative] -or
                (Get-FileHash -LiteralPath (Join-Path $testDirectory $relative)).Hash.ToLowerInvariant() -cne $testedAppFiles[$relative]) {
                throw "The tested assembly identity changed: $relative"
            }
        }
        $helper = 'WinCoreManager.Update.exe'
        Copy-Item -LiteralPath (Join-Path $helperOutput $helper) -Destination $draft
        $recoveryDir = Join-Path $draft 'Recovery'
        $null = New-Item -ItemType Directory -Path $recoveryDir, (Join-Path $draft 'Data')
        Copy-Item -LiteralPath (Join-Path $helperOutput $helper) -Destination (Join-Path $recoveryDir 'WinCoreManager.Recover.exe')
        Copy-Item -LiteralPath (Join-Path $snapshot 'tasks/RECUPERACAO-RECOVERY.md') -Destination $draft
        Set-Lines (Join-Path $draft 'settings.json') @('{"PortableMode":true}')
        $payload = @(Get-TreeRows $draft @('settings.json', 'RECUPERACAO-RECOVERY.md', 'Recovery/WinCoreManager.Recover.exe'))
        Set-Lines (Join-Path $draft $manifestName) $payload
        $external = @(Get-TreeRows $draft @() 'portable/')

        $verify = Join-Path $scratch 'verify'
        Copy-Item -LiteralPath $draft -Destination $verify -Recurse
        Verify-Executable 'verify-app' (Join-Path $verify 'WinCoreManager.exe') @('--verify-wincore-package', '0.1.0')
        Verify-Executable 'verify-recovery' (Join-Path $verify 'Recovery/WinCoreManager.Recover.exe') @('--quiet')
        $recoveryLog = Join-Path $evidence 'recovery-fixtures.log'
        & (Join-Path $snapshot 'tools/Test-PortableRecovery.ps1') -Portable $verify -Dotnet $sdk 6>&1 |
            Tee-Object -FilePath $recoveryLog | Out-Host
        if (@(Select-String -LiteralPath $recoveryLog -Pattern '^PASS ').Count -ne 13) {
            throw 'The independent Recovery suite did not confirm all 13 groups.'
        }
        if ((Compare-Object $external @(Get-TreeRows $draft @() 'portable/') -CaseSensitive) -or
            (Compare-Object $sourceRows @(Get-TreeRows $snapshot) -CaseSensitive)) {
            throw 'The package or frozen source changed after verification.'
        }
        $externalManifest = Join-Path $destinationRoot 'SHA256SUMS.txt'
        Set-Lines $externalManifest $external
        $provenance = [ordered]@{
            schema = 'WinCoreManager.PortableCandidate/v2'; runId = $runId; version = '0.1.0'
            startedUtc = $startedUtc.ToString('o'); completedUtc = [DateTime]::UtcNow.ToString('o')
            sourceSnapshotSha256 = $sourceHash; sourceFiles = $sourceRows.Count
            sourceManifest = "evidencias/$runId/source.sha256"; sourceArchive = "evidencias/$runId/source-snapshot.zip"
            sourceArchiveSha256 = (Get-FileHash -LiteralPath $sourceZip).Hash.ToLowerInvariant()
            sdk = $actualSdk; runtime = $RuntimeVersion; rid = 'win-x64'; configuration = 'Release'
            selfContained = $true; tests = $testBatches; testedAssemblySha256 = $testedAppFiles
            testAssembly = "evidencias/$runId/test-binaries/WinCoreManager.Tests.dll"
            testAssemblySha256 = $testAssemblyHash
            recoveryLog = "evidencias/$runId/recovery-fixtures.log"
            recoveryLogSha256 = (Get-FileHash -LiteralPath $recoveryLog).Hash.ToLowerInvariant()
            internalManifest = "portable/$manifestName"; internalFiles = $payload.Count
            internalManifestSha256 = (Get-FileHash -LiteralPath (Join-Path $draft $manifestName)).Hash.ToLowerInvariant()
            externalManifest = 'SHA256SUMS.txt'; externalFiles = $external.Count
            externalManifestSha256 = (Get-FileHash -LiteralPath $externalManifest).Hash.ToLowerInvariant()
            crossRootAppRebuildDifferentHashRows = $differences.Count
            commands = $commands
        }
        [IO.File]::WriteAllText((Join-Path $destinationRoot 'PROVENANCE.json'), ($provenance | ConvertTo-Json -Depth 12) + "`n", $utf8)
        Move-Item -LiteralPath $draft -Destination $portable
        "Candidate $portable | Source SHA-256 $sourceHash | $($external.Count) files" |
            Tee-Object -FilePath (Join-Path $evidence 'generation.log') | Out-Host
    } finally { Pop-Location }
} finally {
    $env:WINCORE_QUARANTINE_SECOND_VOLUME = $previousTopology
    $env:WINCORE_TEST_COLLECTION_ORDER = $previousOrder
}
