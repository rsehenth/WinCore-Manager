[CmdletBinding()]
param([Parameter(Mandatory)] [string] $Candidate)

$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Candidate).Path
$workspace = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\')
$scratch = [IO.Path]::GetFullPath((Join-Path $workspace ("WinCore-Provenance-" + [guid]::NewGuid().ToString('N'))))
$null = New-Item -ItemType Directory -Path $scratch
$verify = Join-Path $PSScriptRoot 'Test-PortableCandidate.ps1'
$manifest = Join-Path $scratch 'PROVENANCE.json'
$utf8 = [Text.UTF8Encoding]::new($false)
$mutations = [ordered]@{
    'missing-schema' = { $_.PSObject.Properties.Remove('schema') }
    'empty-tests' = { $_.tests = @() }
    'omitted-battery' = { $_.tests = @($_.tests | Where-Object name -NE 'cross-volume') }
    'duplicate-battery' = { $_.tests[0] = $_.tests[1] }
    'foreign-trx' = { $_.tests[0].trx = $_.tests[1].trx; $_.tests[0].sha256 = $_.tests[1].sha256 }
    'zero-trx-hash' = { $_.tests[0].sha256 = '0' * 64 }
    'changed-result' = { $_.tests[0].passed = 1 }
    'missing-main-assembly' = { $_.testedAssemblySha256.PSObject.Properties.Remove('WinCoreManager.dll') }
    'missing-language-satellite' = { $_.testedAssemblySha256.PSObject.Properties.Remove('en-US/WinCoreManager.resources.dll') }
    'zero-test-assembly-hash' = { $_.testAssemblySha256 = '0' * 64 }
    'missing-test-assembly-path' = { $_.PSObject.Properties.Remove('testAssembly') }
    'wrong-version' = { $_.version = '0.9.1' }
    'wrong-rid' = { $_.rid = 'win-arm64' }
    'wrong-sdk' = { $_.sdk = '10.0.999' }
    'wrong-runtime' = { $_.runtime = '10.0.99' }
    'zero-source-digest' = { $_.sourceSnapshotSha256 = '0' * 64 }
    'missing-recovery-proof' = { $_.PSObject.Properties.Remove('recoveryLogSha256') }
}
try {
    foreach ($name in @('portable', 'evidencias', 'PROVENANCE.json', 'SHA256SUMS.txt')) {
        Copy-Item -LiteralPath (Join-Path $source $name) -Destination $scratch -Recurse
    }
    & $verify -Candidate $scratch
    $original = [IO.File]::ReadAllText($manifest)
    foreach ($entry in $mutations.GetEnumerator()) {
        $modified = $original | ConvertFrom-Json
        $modified | ForEach-Object $entry.Value
        [IO.File]::WriteAllText($manifest, ($modified | ConvertTo-Json -Depth 12), $utf8)
        $rejected = $false
        try { & $verify -Candidate $scratch }
        catch { $rejected = $true; "PASS $($entry.Key) - $($_.Exception.Message)" }
        if (!$rejected) { throw "Verifier accepted mutation $($entry.Key)." }
    }
} finally {
    if ((Split-Path -Parent $scratch) -ceq $workspace -and (Split-Path -Leaf $scratch) -cmatch '^WinCore-Provenance-[a-f0-9]{32}$') {
        Remove-Item -LiteralPath $scratch -Recurse -Force
    }
}
