using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using WinCoreManager.Models;

namespace WinCoreManager.Services.Uninstaller;

internal static class OfficialAppx
{
    private const string LoadAppx = """
        $ErrorActionPreference = 'Stop'
        $ProgressPreference = 'SilentlyContinue'
        [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
        Import-Module "$PSHOME\Modules\Appx\Appx.psd1" -ErrorAction Stop
        function Get-CurrentPackages { @(Get-AppxPackage -PackageTypeFilter Main -ErrorAction Stop) }
        function Get-Dependencies($packages) {
            $identities = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach ($package in $packages) {
                foreach ($dependency in $package.Dependencies) {
                    foreach ($identity in @($dependency.PackageFamilyName, $dependency.PackageFullName, [string]$dependency)) {
                        if ($identity) { [void]$identities.Add($identity) }
                    }
                }
            }
            return ,$identities
        }
        function CanRemove($p, $dependencies) {
            $null -ne $p.PSObject.Properties['NonRemovable'] -and $p.NonRemovable -eq $false -and
            $p.IsFramework -eq $false -and $p.IsResourcePackage -eq $false -and
            $p.SignatureKind -in @('Store', 'System') -and $p.Status -eq 'Ok' -and
            -not $dependencies.Contains($p.PackageFamilyName) -and -not $dependencies.Contains($p.PackageFullName)
        }
        """;

    internal static async Task<IReadOnlyList<OptionalPackage>?> ReadAsync(CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(18));
        var run = await ProcessRunner.RunAsync(CreateStartInfo(LoadAppx + """

            try {
                $all = @(Get-CurrentPackages)
                $dependencies = Get-Dependencies $all
                $rows = @(foreach ($p in $all) {
                    [pscustomobject]@{
                        Family = [string]$p.PackageFamilyName
                        FullName = [string]$p.PackageFullName
                        Publisher = [string]$p.Publisher
                        Version = [string]$p.Version
                        Eligible = [bool](CanRemove $p $dependencies)
                    }
                })
                ConvertTo-Json -InputObject $rows -Depth 2 -Compress
            } catch { exit 21 }
            """), cancellationToken: deadline.Token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!run.HasExitCode || run.ExitCode != 0 || run.StartFailed || run.Cancelled || run.Interrupted)
            return null;
        try
        {
            var rows = JsonSerializer.Deserialize<OptionalPackage[]>(string.Join("", run.Output));
            return rows is not null && rows.All(row => row is { Family.Length: > 0, FullName.Length: > 0 }) ? rows : null;
        }
        catch (JsonException) { return null; }
    }

    internal static ProcessStartInfo Removal(InstalledProgram program)
    {
        if (!OptionalWindowsApps.IsApprovedTarget(program))
            throw new ArgumentException(nameof(program));
        var family = program.OptionalPackageFamily!;
        var full = program.OptionalPackageFullName!;
        return CreateStartInfo(LoadAppx + $$"""

            $family = '{{family}}'
            $full = '{{full}}'
            try {
                $all = @(Get-CurrentPackages)
                $matching = @($all | Where-Object { $_.PackageFamilyName -ieq $family })
                $dependencies = Get-Dependencies $all
                if ($matching.Count -ne 1 -or $matching[0].PackageFullName -ine $full -or
                    -not (CanRemove $matching[0] $dependencies)) { exit 10 }
            } catch { exit 10 }
            $started = $true
            try { Remove-AppxPackage -Package $full -ErrorAction Stop -Confirm:$false } catch { $started = $false }
            try {
                $present = @(Get-CurrentPackages | Where-Object { $_.PackageFamilyName -ieq $family })
                if ($present.Count -eq 0 -and $started) { exit 0 }
                if ($present.Count -eq 1 -and $present[0].PackageFullName -ieq $full) { exit 20 }
                exit 21
            } catch { exit 21 }
            """);
    }

    private static ProcessStartInfo CreateStartInfo(string script)
    {
        var path = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0");
        var start = new ProcessStartInfo(Path.Combine(path, "powershell.exe"))
        {
            WorkingDirectory = path,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        start.Environment["PSModulePath"] = Path.Combine(path, "Modules");
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand",
            Convert.ToBase64String(Encoding.Unicode.GetBytes(script)) })
            start.ArgumentList.Add(argument);
        return start;
    }
}
