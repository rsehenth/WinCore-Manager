using System.Diagnostics;
using System.IO;
using System.Management;
using System.Runtime.InteropServices;
using System.Security;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using WinCoreManager.Models;

namespace WinCoreManager.Services;

public enum EssentialState { NotVerified, Installed, NotInstalled }
public sealed record EssentialDetection(EssentialState State, string? Version = null);

internal sealed record EssentialProgram(string Name, string? Version, string? Publisher);
internal sealed record DotnetRuntime(string Framework, string Version, string Architecture);
internal sealed record VcRuntime(string Family, string Architecture, string? Version);
internal sealed record EssentialInventory(IReadOnlyList<EssentialProgram> Programs, bool ProgramsComplete, bool Is64Bit,
    IReadOnlyList<DotnetRuntime> Runtimes, bool DotnetComplete, IReadOnlyList<VcRuntime> Vc,
    EssentialState Framework35, IReadOnlyList<string> IncompleteVcArchitectures,
    IReadOnlyList<string>? PythonStoreVersions = null);

public interface IEssentialInstallationService
{
    Task<IReadOnlyDictionary<string, EssentialDetection>> DetectAsync(CancellationToken token);
}

internal sealed partial class EssentialInstallationService : IEssentialInstallationService
{
    private static readonly Dictionary<string, string> ProgramNames = new(StringComparer.Ordinal)
    {
        ["nvidia"] = @"^NVIDIA Graphics Driver(?: [\d.]+)?$",
        ["klite"] = @"^K-Lite Codec Pack (?:[\d.]+ )?Mega$",
        ["java"] = @"^Java (?:\d+ Update \d+(?: \(64-bit\))?|\d+(?:\.\d+){0,3}(?: \(64-bit\))?)$",
        ["winrar"] = @"^WinRAR(?: [\d.]+)?(?: \((?:32|64)-bit\))?$",
        ["windirstat"] = @"^WinDirStat(?: [\d.]+)?(?: \((?:x86|x64|ARM64)\))?$",
        ["nodejs"] = @"^Node\.js$",
        ["git"] = @"^Git(?: version [\d.]+)?$",
        ["vscode"] = @"^Microsoft Visual Studio Code(?: \(User\))?$",
    };
    private static readonly string[] VcLegacyYears = ["2005", "2008", "2010", "2012", "2013"];

    public Task<IReadOnlyDictionary<string, EssentialDetection>> DetectAsync(CancellationToken token) =>
        Task.Run(async () => Evaluate(await InventoryAsync(token).ConfigureAwait(false)), token);

    internal static IReadOnlyDictionary<string, EssentialDetection> Evaluate(EssentialInventory inventory)
    {
        var result = EssentialAppCatalog.Apps.ToDictionary(app => app.Id, _ => new EssentialDetection(EssentialState.NotVerified));
        foreach (var (id, pattern) in ProgramNames)
        {
            var installed = inventory.Programs.Where(program => Regex.IsMatch(program.Name, pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToArray();
            result[id] = installed.Length > 0
                ? new(EssentialState.Installed, JoinVersions(installed.Select(program => program.Version)))
                : new(inventory.ProgramsComplete && id != "nvidia" ? EssentialState.NotInstalled : EssentialState.NotVerified);
        }

        // Launchers, Store variants and manual/portable installs require their own reliable identity.
        // ARP can confirm these products, but an absent ARP entry is not enough to decide they are absent.
        foreach (var id in new[] { "stirling", "artcraft", "recordly", "nextgenup", "tracedraw", "arroxy",
            "misterdownload", "kiro", "orca", "opencode", "freebuff", "commandcode", "qoder" })
        {
            var app = EssentialAppCatalog.Apps.Single(a => a.Id == id);
            var installed = inventory.Programs.Where(program => program.Name.Equals(app.ProductName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(program.Publisher)).ToArray();
            if (installed.Length > 0)
                result[id] = new(EssentialState.Installed, JoinVersions(installed.Select(program => program.Version)));
        }

        var python = inventory.Programs.Where(p => Regex.IsMatch(p.Name, @"^Python \d+\.\d+(?:\.\d+)? \((?:32|64)-bit\)$",
            RegexOptions.CultureInvariant) && p.Publisher == "Python Software Foundation").ToArray();
        var pythonStore = inventory.PythonStoreVersions ?? [];
        if (python.Length > 0 || pythonStore.Count > 0)
            result["python"] = new(EssentialState.Installed, JoinVersions(python.Select(p => p.Version).Concat(pythonStore)));

        foreach (var major in new[] { 8, 10, 11 })
        {
            var runtimes = inventory.Runtimes.Where(runtime => runtime.Framework == "Microsoft.WindowsDesktop.App" &&
                runtime.Version.StartsWith(major + ".", StringComparison.Ordinal)).ToArray();
            result[$"desktop{major}"] = runtimes.Length > 0
                ? new(EssentialState.Installed, JoinVersions(runtimes.Select(runtime => $"{runtime.Version} ({runtime.Architecture})")))
                : new(inventory.DotnetComplete ? EssentialState.NotInstalled : EssentialState.NotVerified);
        }

        result["framework35"] = new(inventory.Framework35, inventory.Framework35 == EssentialState.Installed ? "3.5" : null);
        var architectures = inventory.Is64Bit ? new[] { "x86", "x64" } : ["x86"];
        var modern = inventory.Vc.Where(runtime => runtime.Family == "v14" && architectures.Contains(runtime.Architecture)).ToArray();
        var modernComplete = architectures.All(arch => modern.Any(runtime => runtime.Architecture == arch));
        result["vc-redist"] = modern.Length > 0
            ? new(EssentialState.Installed, JoinVersions(modern.Select(runtime =>
                runtime.Version is null ? runtime.Architecture : $"{runtime.Version} ({runtime.Architecture})")))
            : new(inventory.IncompleteVcArchitectures.Count == 0 ? EssentialState.NotInstalled : EssentialState.NotVerified);

        var legacy = inventory.Programs.Select(p => LegacyVcName().Match(p.Name))
            .Where(match => match.Success).Select(match => (match.Groups[1].Value, match.Groups[2].Value.ToLowerInvariant()))
            .ToHashSet();
        var legacyComplete = VcLegacyYears.All(year => architectures.All(arch => legacy.Contains((year, arch))));
        var ambiguousLegacy = inventory.Programs.Any(p =>
            VcLegacyYears.Any(year => p.Name.StartsWith($"Microsoft Visual C++ {year} Redistributable", StringComparison.OrdinalIgnoreCase))
            && !LegacyVcName().IsMatch(p.Name));
        var missingConfirmed = (!legacyComplete && inventory.ProgramsComplete && !ambiguousLegacy) ||
            architectures.Any(arch => !modern.Any(runtime => runtime.Architecture == arch)
                && !inventory.IncompleteVcArchitectures.Contains(arch));
        result["vc-aio"] = new(modernComplete && legacyComplete ? EssentialState.Installed
            : missingConfirmed ? EssentialState.NotInstalled : EssentialState.NotVerified);
        return result;
    }

    private static string? JoinVersions(IEnumerable<string?> versions)
    {
        var distinct = versions.Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase).Order().Take(6).ToArray();
        return distinct.Length == 0 ? null : string.Join(", ", distinct);
    }

    private static async Task<EssentialInventory> InventoryAsync(CancellationToken token)
    {
        var is64 = Environment.Is64BitOperatingSystem;
        var views = is64 ? new[] { RegistryView.Registry64, RegistryView.Registry32 } : [RegistryView.Default];
        var programs = new List<EssentialProgram>();
        var arpComplete = true;
        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        foreach (var view in views)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
                if (uninstall is null) continue;
                foreach (var name in uninstall.GetSubKeyNames())
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using var entry = uninstall.OpenSubKey(name);
                        if (entry?.GetValue("DisplayName") is not string display) continue;
                        if (entry.GetValue("WindowsInstaller") is 1 && Guid.TryParseExact(name, "B", out _))
                        {
                            var state = MsiQueryProductStateW(name);
                            if (state != 5) // INSTALLSTATE_DEFAULT: installed for the current installation context.
                            {
                                if (state != 1) arpComplete = false;
                                continue;
                            }
                        }
                        programs.Add(new(display.Trim(), entry.GetValue("DisplayVersion") as string, entry.GetValue("Publisher") as string));
                    }
                    catch (Exception ex) when (ProbeFailure(ex)) { arpComplete = false; }
                }
            }
            catch (Exception ex) when (ProbeFailure(ex)) { arpComplete = false; }
        }

        var native = views[0];
        var vc = new List<VcRuntime>();
        var vcIncomplete = new List<string>();
        foreach (var arch in is64 ? new[] { "x86", "x64" } : ["x86"])
        {
            var failed = false;
            var preferredViews = arch == "x86" ? views.Reverse() : views;
            foreach (var view in preferredViews)
            {
                try
                {
                    using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                    using var key = root.OpenSubKey($@"SOFTWARE\Microsoft\VisualStudio\14.0\VC\Runtimes\{arch}");
                    if (key?.GetValue("Installed") is not 1) continue;
                    vc.Add(new("v14", arch, (key.GetValue("Version") as string)?.TrimStart('v')));
                    break;
                }
                catch (Exception ex) when (ProbeFailure(ex)) { failed = true; }
            }
            if (failed && !vc.Any(runtime => runtime.Architecture == arch)) vcIncomplete.Add(arch);
        }

        var (runtimes, dotnetComplete) = await ReadRuntimesAsync(native, token).ConfigureAwait(false);
        return new(programs, arpComplete, is64, runtimes, dotnetComplete, vc, ReadFramework35(), vcIncomplete, ReadPythonStore());
    }

    private static async Task<(List<DotnetRuntime> Runtimes, bool Complete)> ReadRuntimesAsync(RegistryView view, CancellationToken token)
    {
        var nativeArch = System.Runtime.InteropServices.RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();
        var architectures = nativeArch switch { "arm64" => new[] { "arm64", "x64", "x86" }, "x64" => ["x64", "x86"], _ => new[] { nativeArch } };
        var runtimes = new List<DotnetRuntime>();
        var complete = true;
        foreach (var arch in architectures)
        {
            token.ThrowIfCancellationRequested();
            var programFiles = Environment.GetFolderPath(arch == "x86" && Environment.Is64BitOperatingSystem
                ? Environment.SpecialFolder.ProgramFilesX86 : Environment.SpecialFolder.ProgramFiles);
            var standard = Path.Combine(programFiles, "dotnet");
            if (nativeArch == "arm64" && arch == "x64") standard = Path.Combine(standard, "x64");
            try
            {
                using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var setup = root.OpenSubKey($@"SOFTWARE\dotnet\Setup\InstalledVersions\{arch}");
                if (setup?.GetValue("InstallLocation") is string registered && !string.IsNullOrWhiteSpace(registered)
                    && !Path.GetFullPath(registered).TrimEnd('\\').Equals(standard, StringComparison.OrdinalIgnoreCase))
                {
                    // Read the registered runtime directory. Never execute a host from a registry-supplied path.
                    if (!Startup.StartupPaths.IsSafeDirectory(registered)) complete = false;
                    else
                    {
                        foreach (var framework in new[] { "Microsoft.WindowsDesktop.App", "Microsoft.NETCore.App" })
                        foreach (var folder in ReadVersionFolders(registered, framework))
                            runtimes.Add(new(framework, Path.GetFileName(folder), arch));
                    }
                }
            }
            catch (Exception ex) when (ProbeFailure(ex)) { complete = false; }

            try
            {
                if (string.IsNullOrWhiteSpace(programFiles)) { complete = false; continue; }
                var host = Path.Combine(standard, "dotnet.exe");
                try { _ = File.GetAttributes(host); }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
                if (!Startup.StartupPaths.IsSafeFile(host)) { complete = false; continue; }
                using var process = new Process { StartInfo = new(host, "--list-runtimes")
                {
                    UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true,
                    WorkingDirectory = standard,
                } };
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(5));
                process.StartInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
                if (!process.Start()) { complete = false; continue; }
                try
                {
                    var output = await process.StandardOutput.ReadToEndAsync(deadline.Token).ConfigureAwait(false);
                    await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false);
                    if (process.ExitCode != 0) { complete = false; continue; }
                    foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        var match = RuntimeLine().Match(line);
                        if (!match.Success) { complete = false; continue; }
                        runtimes.Add(new(match.Groups[1].Value, match.Groups[2].Value, arch));
                    }
                }
                finally
                {
                    if (!process.HasExited) process.Kill();
                }
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { complete = false; }
            catch (Exception ex) when (ProbeFailure(ex)) { complete = false; }
        }
        return (runtimes, complete);
    }

    private static IEnumerable<string> ReadVersionFolders(string root, string framework)
    {
        var folder = Path.Combine(root, "shared", framework);
        try
        {
            return Directory.GetDirectories(folder).Where(version =>
                RuntimeVersion().IsMatch(Path.GetFileName(version)) &&
                Startup.StartupPaths.IsSafeFile(Path.Combine(version, framework + ".deps.json"))).ToArray();
        }
        catch (DirectoryNotFoundException) { return []; }
    }

    private static IReadOnlyList<string> ReadPythonStore()
    {
        // Read current-user AppModel registrations. The Python install manager is not a runtime.
        var versions = new List<string>();
        try
        {
            using var hive = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            using var registrations = hive.OpenSubKey(@"Software\Classes\ActivatableClasses\Package");
            if (registrations is null) return versions;
            foreach (var name in registrations.GetSubKeyNames())
            {
                var match = PythonStoreName().Match(name);
                if (match.Success) versions.Add($"{match.Groups[1].Value} ({match.Groups[2].Value})");
            }
        }
        catch (Exception ex) when (ProbeFailure(ex)) { }
        return versions;
    }

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MsiQueryProductStateW(string productCode);

    [GeneratedRegex(@"^PythonSoftwareFoundation\.Python\.3\.\d+_(\d+\.\d+\.\d+\.\d+)_(x86|x64|arm64)__qbz5n2kfra8p0$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PythonStoreName();

    private static EssentialState ReadFramework35()
    {
        // NDP is a positive fallback on Windows installations without an available WMI provider.
        try
        {
            using var query = new ManagementObjectSearcher("SELECT InstallState FROM Win32_OptionalFeature WHERE Name='NetFx3'")
                { Options = { Timeout = TimeSpan.FromSeconds(5), ReturnImmediately = true } };
            using var results = query.Get();
            foreach (ManagementObject feature in results)
            {
                using (feature)
                {
                    var state = Convert.ToInt32(feature["InstallState"]);
                    if (state == 1) return EssentialState.Installed;
                    if (state is 2 or 3) return EssentialState.NotInstalled;
                }
            }
        }
        catch (ManagementException) { }
        catch (Exception ex) when (ProbeFailure(ex)) { }
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            using var key = root.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v3.5");
            if (key?.GetValue("Install") is 1) return EssentialState.Installed;
        }
        catch (Exception ex) when (ProbeFailure(ex)) { }
        return EssentialState.NotVerified;
    }

    private static bool ProbeFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        SecurityException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or
        System.Runtime.InteropServices.COMException;

    [GeneratedRegex(@"^Microsoft Visual C\+\+ (2005|2008|2010|2012|2013) Redistributable(?: Package)?\s*[-?(]?\s*(x86|x64)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LegacyVcName();

    [GeneratedRegex(@"^(\S+) (\d+\.\d+\.\d+\S*) \[.+\]$", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeLine();

    [GeneratedRegex(@"^\d+\.\d+\.\d+(?:[-+][a-zA-Z0-9.-]+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex RuntimeVersion();
}
