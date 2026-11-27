[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Package,
    [string] $Baseline,
    [long] $MaximumBytes = 85MB
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-EntryHash($Entry) {
    $stream = $Entry.Open()
    $sha = [Security.Cryptography.SHA256]::Create()
    try { [Convert]::ToBase64String($sha.ComputeHash($stream)) }
    finally { $sha.Dispose(); $stream.Dispose() }
}

$item = Get-Item -LiteralPath $Package
if ($item.Length -gt $MaximumBytes) { throw "Portable ZIP is $($item.Length) bytes, above the $MaximumBytes budget." }

$zip = [IO.Compression.ZipFile]::OpenRead($item.FullName)
try {
    $entries = @{}
    foreach ($entry in $zip.Entries) {
        $name = $entry.FullName.Replace('\', '/')
        if ($entries.ContainsKey($name)) { throw "Duplicate ZIP entry $name." }
        if ($name -match '\.(pdb|trx|binlog|tmp|dmp)$') { throw "Unexpected build artifact $name." }
        $entries.Add($name, $entry)
    }
    $required = @(
        'WinCoreManager.exe', 'WinCoreManager.dll', 'WinCoreManager.runtimeconfig.json', 'PresentationFramework.dll',
        'coreclr.dll', 'hostfxr.dll', 'e_sqlite3.dll', 'WinCoreManager.Update.exe',
        'Recovery/WinCoreManager.Recover.exe', 'WinCoreManager.package.sha256', 'settings.json',
        'en-US/WinCoreManager.resources.dll', 'Licencas/THIRD-PARTY-NOTICES.md', 'Licencas/DiskScope.LICENSE.txt'
    )
    foreach ($name in $required) {
        if (!$entries.ContainsKey("WinCoreManager/$name")) { throw "Required portable file missing: $name." }
    }
    $updater = $entries['WinCoreManager/WinCoreManager.Update.exe']
    $recovery = $entries['WinCoreManager/Recovery/WinCoreManager.Recover.exe']
    if ($updater.Length -gt 20MB -or $recovery.Length -ne $updater.Length -or
        (Get-EntryHash $updater) -cne (Get-EntryHash $recovery)) {
        throw 'Unexpected updater or autonomous recovery bundle.'
    }

    $baselineBytes = if ($Baseline) { (Get-Item -LiteralPath $Baseline).Length } else { 0 }
    if ($baselineBytes -gt 0 -and $item.Length -ge $baselineBytes) { throw 'Candidate must be smaller than the baseline.' }
    [pscustomobject]@{
        ZipBytes = $item.Length
        BaselineBytes = $baselineBytes
        SavingsBytes = if ($baselineBytes -gt 0) { $baselineBytes - $item.Length } else { $null }
        Entries = $zip.Entries.Count
        ExpandedBytes = ($zip.Entries | Measure-Object Length -Sum).Sum
        UpdaterBytes = $updater.Length
        UpdaterCompressedBytes = $updater.CompressedLength
        RecoveryCompressedBytes = $recovery.CompressedLength
    } | ConvertTo-Json -Compress
}
finally { $zip.Dispose() }
