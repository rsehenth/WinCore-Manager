[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Candidate)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = (Resolve-Path -LiteralPath $Candidate).Path
$provenance = Get-Content -LiteralPath (Join-Path $root 'PROVENANCE.json') -Encoding UTF8 -Raw | ConvertFrom-Json
if ($provenance.schema -cne 'WinCoreManager.PortableCandidate/v2') { throw 'Unsupported provenance format.' }
if ($provenance.runId -cnotmatch '^[0-9]{8}-[0-9]{6}-[a-f0-9]{8}$' -or $provenance.version -cne '0.1.0' -or
    $provenance.rid -cne 'win-x64' -or $provenance.configuration -cne 'Release' -or
    $provenance.selfContained -isnot [bool] -or !$provenance.selfContained -or
    $provenance.sdk -cnotmatch '^10\.0\.\d{3}$' -or $provenance.runtime -cnotmatch '^10\.0\.\d+$') {
    throw 'Invalid candidate identity or build configuration.'
}
$evidence = "evidencias/$($provenance.runId)"
$started = [DateTimeOffset]::Parse([string]$provenance.startedUtc, [Globalization.CultureInfo]::InvariantCulture)
$completed = [DateTimeOffset]::Parse([string]$provenance.completedUtc, [Globalization.CultureInfo]::InvariantCulture)
if ($started -ge $completed -or $started.ToUniversalTime().ToString('yyyyMMdd-HHmmss') -ne $provenance.runId.Substring(0, 15)) {
    # The run ID is created just before the timestamp and can cross one UTC second.
    $runTimestamp = [DateTime]::ParseExact($provenance.runId.Substring(0, 15), 'yyyyMMdd-HHmmss',
        [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::AssumeUniversal)
    if ($started -ge $completed -or [Math]::Abs(($started.UtcDateTime - $runTimestamp.ToUniversalTime()).TotalSeconds) -gt 5) {
        throw 'Invalid build timestamps.'
    }
}

function Get-VerifiedPath([string] $Base, [string] $Relative, [string] $ExpectedHash) {
    if ($Relative -cnotmatch '^[a-zA-Z0-9_. /-]+$' -or $Relative -match '(^|/)\.{1,2}(/|$)' -or
        $Relative.StartsWith('/') -or $Relative.EndsWith('/') -or $Relative.Contains('//') -or
        $ExpectedHash -cnotmatch '^[a-f0-9]{64}$' -or $ExpectedHash -match '^0+$') { throw "Invalid evidence path or digest: $Relative" }
    $path = $Base
    foreach ($component in $Relative.Split('/')) {
        $path = Join-Path $path $component
        $item = Get-Item -LiteralPath $path
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Reparse point in evidence: $Relative" }
    }
    if ($item.PSIsContainer -or
        (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() -cne $ExpectedHash) {
        throw "Hash mismatch: $Relative"
    }
    return $path
}

if ($provenance.externalManifest -cne 'SHA256SUMS.txt' -or
    $provenance.internalManifest -cne 'portable/WinCoreManager.package.sha256' -or
    $provenance.sourceManifest -cne "$evidence/source.sha256" -or $provenance.sourceArchive -cne "$evidence/source-snapshot.zip" -or
    $provenance.testAssembly -cne "$evidence/test-binaries/WinCoreManager.Tests.dll" -or
    $provenance.recoveryLog -cne "$evidence/recovery-fixtures.log") { throw 'Unexpected provenance artifact paths.' }

function Read-Manifest([string] $Path) {
    $entries = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        if ($line -cnotmatch '^([a-f0-9]{64})  (.+)$' -or $entries.ContainsKey($Matches[2])) {
            throw "Invalid manifest row in $Path"
        }
        $entries.Add($Matches[2], $Matches[1])
    }
    return ,$entries
}

$outerPath = Get-VerifiedPath $root $provenance.externalManifest $provenance.externalManifestSha256
$innerPath = Get-VerifiedPath $root $provenance.internalManifest $provenance.internalManifestSha256
$sourcePath = Get-VerifiedPath $root $provenance.sourceManifest $provenance.sourceSnapshotSha256
$archivePath = Get-VerifiedPath $root $provenance.sourceArchive $provenance.sourceArchiveSha256
$outer = Read-Manifest $outerPath
$inner = Read-Manifest $innerPath
$sources = Read-Manifest $sourcePath
if ($outer.Count -ne 273 -or $inner.Count -ne 269 -or $sources.Count -lt 246 -or
    $outer.Count -ne $provenance.externalFiles -or $inner.Count -ne $provenance.internalFiles -or
    $sources.Count -ne $provenance.sourceFiles) { throw 'Unexpected manifest counts.' }

$actual = @(Get-ChildItem -LiteralPath (Join-Path $root 'portable') -Recurse -File -Force)
if ($actual.Count -ne $outer.Count) { throw 'The portable tree contains an unexpected number of files.' }
foreach ($relative in $outer.Keys) {
    if (!$relative.StartsWith('portable/')) { throw "Unexpected portable manifest entry: $relative" }
    $null = Get-VerifiedPath $root $relative $outer[$relative]
}
foreach ($relative in $inner.Keys) {
    if ($outer["portable/$relative"] -cne $inner[$relative]) { throw "Manifest disagreement: $relative" }
}
$testDll = Get-VerifiedPath $root $provenance.testAssembly $provenance.testAssemblySha256
if ([Reflection.AssemblyName]::GetAssemblyName($testDll).Name -cne 'WinCoreManager.Tests') { throw 'Unexpected test assembly.' }
$appFiles = @('WinCoreManager.dll', 'en-US/WinCoreManager.resources.dll')
if (@($provenance.testedAssemblySha256.PSObject.Properties).Count -ne $appFiles.Count) { throw 'Missing tested application hashes.' }
foreach ($name in $appFiles) {
    $digest = $provenance.testedAssemblySha256.PSObject.Properties[$name].Value
    if ($inner[$name] -cne $digest) { throw "Tested binary differs from the package: $name" }
    $binary = Get-VerifiedPath $root "$evidence/test-binaries/$name" $digest
    $assembly = [Reflection.AssemblyName]::GetAssemblyName($binary)
    if ($name -eq 'WinCoreManager.dll') {
        if ($assembly.Name -cne 'WinCoreManager' -or (Get-Item -LiteralPath $binary).VersionInfo.ProductVersion -cne $provenance.version) {
            throw 'Application assembly version mismatch.'
        }
    } elseif ($assembly.Name -cne 'WinCoreManager.resources' -or $assembly.CultureName -cne 'en-US') { throw 'Unexpected satellite assembly.' }
}

$runtime = (Get-Content -LiteralPath (Join-Path $root 'portable/WinCoreManager.runtimeconfig.json') -Raw | ConvertFrom-Json).runtimeOptions
$deps = Get-Content -LiteralPath (Join-Path $root 'portable/WinCoreManager.deps.json') -Raw | ConvertFrom-Json
if ($runtime.tfm -cne 'net10.0' -or @($runtime.includedFrameworks).Count -ne 2 -or
    @($runtime.includedFrameworks | Where-Object { $_.version -ceq $provenance.runtime -and $_.name -cin
        @('Microsoft.NETCore.App', 'Microsoft.WindowsDesktop.App') } | Select-Object -ExpandProperty name -Unique).Count -ne 2 -or
    $deps.runtimeTarget.name -cne ".NETCoreApp,Version=v10.0/$($provenance.rid)" -or
    !$deps.libraries.PSObject.Properties["WinCoreManager/$($provenance.version)"]) { throw 'Runtime, RID or application version mismatch.' }

$recoveryLog = Get-VerifiedPath $root $provenance.recoveryLog $provenance.recoveryLogSha256
if (@(Select-String -LiteralPath $recoveryLog -Pattern '^PASS ').Count -ne 13) { throw 'Recovery evidence is incomplete.' }

# Every required battery is tied to its canonical TRX name and independently evaluated.
$required = [ordered]@{
    'targeted-12k' = 10; 'cross-volume' = 3; 'full-release' = 820; 'audit-selection-181' = 181
    'targeted-12h' = 40; 'adversarial' = 246; 'smoke-wpf' = 8; 'combined-theme-language' = 90
}
$exact = @('cross-volume', 'audit-selection-181', 'targeted-12h', 'smoke-wpf', 'combined-theme-language')
if ($provenance.tests -isnot [array] -or $provenance.tests.Count -ne $required.Count) { throw 'Required test evidence is missing.' }
$seenBatches = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$seenRuns = [Collections.Generic.HashSet[guid]]::new()
$sourceAssembly = $null
foreach ($test in $provenance.tests) {
    if ($test.name -cnotin @($required.Keys) -or !$seenBatches.Add($test.name) -or
        $test.trx -cne "$evidence/$($test.name).trx" -or !$seenRuns.Add([guid]$test.id) -or [guid]$test.id -eq [guid]::Empty -or
        $test.passed -lt $required[$test.name] -or ($test.name -in $exact -and $test.passed -ne $required[$test.name])) {
        throw "Unexpected test evidence: $($test.name)"
    }
    $trx = Get-VerifiedPath $root $test.trx $test.sha256
    [xml]$trxData = Get-Content -LiteralPath $trx -Raw
    $run = $trxData.TestRun
    $counters = $run.ResultSummary.Counters
    $results = @($run.Results.UnitTestResult)
    $definitions = @($run.TestDefinitions.UnitTest)
    if ($run.id -cne $test.id -or $run.ResultSummary.outcome -cne 'Completed' -or
        [int]$counters.passed -ne $test.passed -or [int]$counters.executed -ne $test.passed -or
        [int]$counters.failed -ne 0 -or [int]$counters.notExecuted -ne 0 -or [int]$counters.total -ne $test.passed -or
        $results.Count -ne $test.passed -or $definitions.Count -ne $test.passed -or
        @($results | Where-Object outcome -CNE 'Passed').Count -ne 0) {
        throw "Unexpected test outcomes: $($test.name)"
    }
    $defined = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($definition in $definitions) {
        $codeBase = $definition.TestMethod.codeBase
        if (!$sourceAssembly) { $sourceAssembly = $codeBase }
        $key = "$($definition.id)/$($definition.Execution.id)"
        if ([IO.Path]::GetFileName($codeBase) -cne 'WinCoreManager.Tests.dll' -or $sourceAssembly -cne $codeBase -or
            $definition.storage -ine $codeBase -or $defined.ContainsKey($key)) {
            throw "Unexpected test assembly or definition: $($test.name)"
        }
        $defined.Add($key, $definition.name)
    }
    foreach ($result in $results) {
        $key = "$($result.testId)/$($result.executionId)"
        if (!$defined.ContainsKey($key) -or $defined[$key] -cne $result.testName) { throw "Unmatched result in $($test.name)" }
        $null = $defined.Remove($key)
    }
    if ($test.name -eq 'cross-volume') {
        $crossTests = @('ArquivoEmOutroVolumeUsaQuarentenaNaOrigemERestauraBytesAposReabrir',
            'PastaEmOutroVolumePermaneceNaOrigem', 'PastaComProvaPodeUsarVolumeDeOrigemERestaurarFluxosEPermissoes')
        if (@(Compare-Object $crossTests @($definitions | ForEach-Object { $_.TestMethod.name }) -CaseSensitive).Count -ne 0 -or
            @($definitions | Where-Object { $_.TestMethod.className -cne 'WinCoreManager.Tests.ResidueQuarantineTests' }).Count -ne 0) {
            throw 'The three required cross-volume assertions were not exercised.'
        }
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
try {
    $actualSources = @($archive.Entries | Where-Object Name)
    if ($actualSources.Count -ne $sources.Count) { throw 'The archive source count differs from the manifest.' }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $sdkEntry = $archive.GetEntry('global.json')
    if (!$sdkEntry -or $sdkEntry.Length -gt 4096) { throw 'The source snapshot lacks its SDK pin.' }
    $sdkReader = [IO.StreamReader]::new($sdkEntry.Open())
    try { $sdkPin = ($sdkReader.ReadToEnd() | ConvertFrom-Json).sdk }
    finally { $sdkReader.Dispose() }
    if ($sdkPin.version -cne $provenance.sdk -or $sdkPin.rollForward -cne 'disable') { throw 'SDK pin differs from provenance.' }
    foreach ($entry in $actualSources) {
        $name = $entry.FullName.Replace('\', '/')
        if (!$seen.Add($name) -or !$sources.ContainsKey($name)) { throw "Unexpected archived source: $name" }
        $stream = $entry.Open()
        $sha = [Security.Cryptography.SHA256]::Create()
        try {
            $hash = [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '').ToLowerInvariant()
            if ($hash -cne $sources[$name]) { throw "Archived source differs: $name" }
        } finally { $sha.Dispose(); $stream.Dispose() }
    }
} finally { $archive.Dispose() }

"Verified $($outer.Count) portable files, $($inner.Count) payload files, $($sources.Count) archived source files and $($provenance.tests.Count) test runs."
