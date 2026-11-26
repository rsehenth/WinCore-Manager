using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Resources;
using System.Text.Json;
using WinCoreManager.Services.SelfUpdate;

internal static partial class Program
{
    private static int Main(string[] args)
    {
        var executable = Path.GetFileName(Environment.ProcessPath);
        if (string.Equals(executable, PortablePackage.RecoveryHelper, StringComparison.OrdinalIgnoreCase))
            return RecoveryEntry.Run(args, EnsureNoApplicationProcess, Notify);
        if (!string.Equals(executable, PortablePackage.Helper, StringComparison.OrdinalIgnoreCase) ||
            Environment.IsPrivilegedProcess || !GitHubReleases.OfficialIdentityConfigured ||
            ReleaseTrust.PublisherPublicKey is not { } publisherKey ||
            args is not [var requested, var parentId, var parentStart] ||
            !UpdateVersion.TryParse(requested, out var version) || !version.IsStable ||
            !int.TryParse(parentId, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0 ||
            !long.TryParse(parentStart, NumberStyles.None, CultureInfo.InvariantCulture, out var started) || started <= 0)
            return 2;

        var work = new DirectoryInfo(AppContext.BaseDirectory);
        if (!Guid.TryParseExact(work.Name, "N", out _) ||
            work.Parent is not { Name: PortablePackage.WorkDirectory, Parent: { } installation })
            return 2;
        var root = installation.FullName;
        try
        {
            root = PortableFileSystem.ResolveRoot(root);
            var job = Path.Combine(root, PortablePackage.WorkDirectory, work.Name);
            using var paths = new PortableFileSystem();
            paths.PinDirectory(job);
            WaitForParent(root, pid, started);
            var transaction = new PortableUpdateTransaction(root, job);
            using (var lease = PortableUpdateLease.TryAcquire(root) ?? throw new UpdateException(UpdateState.Failed))
            {
                EnsureNoApplicationProcess(root);
                if (PortableUpdateTransaction.FindPending(root).Count != 0)
                    throw new UpdateException(UpdateState.Failed);
                var restartablePrevious = PortablePackage.ReadSignedManifest(root, publisherKey);
                PortablePackage.VerifyFiles(root, restartablePrevious.Files);
                var previous = ReadInstalledVersion(root);
                if (version.CompareTo(previous) <= 0)
                {
                    throw new UpdateException(UpdateState.VerificationFailed);
                }

                PortablePackage.VerifyExecutable(Path.Combine(root, PortablePackage.RecoveryDirectory, PortablePackage.RecoveryHelper));
                var zip = Path.Combine(job, "release.zip");
                var signature = zip + ".sig";
                PortablePackage.CheckPath(zip);
                PortablePackage.CheckPath(signature);
                using (var input = PortableFileSystem.OpenRead(zip))
                using (var proof = PortableFileSystem.OpenRead(signature))
                {
                    ReleaseTrust.Verify(input, proof, publisherKey);
                    // Keep the verified archive open without write/delete sharing through extraction.
                    PortablePackage.Extract(zip, transaction.Staging, CancellationToken.None);
                }

                PortablePackage.VerifyLayout(transaction.Staging, version);
                transaction.Apply(publisherKey: publisherKey);
                transaction.FinishVerifiedUpdate(() => VerifyStartup(root, version), publisherKey);
            }

            Restart(root);
            return 0;
        }
        catch (Exception ex) when (RecoveryEntry.IsRecoveryFailure(ex))
        {
            NotifyFailure(root);
            return 3;
        }
    }

    private static UpdateVersion ReadInstalledVersion(string root)
    {
        var installed = FileVersionInfo.GetVersionInfo(PortablePackage.PayloadPath(root, "WinCoreManager.dll")).ProductVersion;
        return UpdateVersion.TryParse(installed, out var version) ? version : throw new UpdateException(UpdateState.VerificationFailed);
    }

    private static void WaitForParent(string root, int pid, long started)
    {
        Process process;
        try { process = Process.GetProcessById(pid); }
        catch (ArgumentException) { return; }
        using (process)
        {
            if (process.HasExited)
            {
                return;
            }
            if (process.StartTime.ToUniversalTime().Ticks != started || !IsApplicationProcess(process, root) ||
                !process.WaitForExit(90_000))
            {
                throw new UpdateException(UpdateState.Failed);
            }
        }
    }

    private static void EnsureNoApplicationProcess(string root)
    {
        foreach (var process in Process.GetProcessesByName("WinCoreManager"))
        {
            using (process)
            {
                if (!process.HasExited && IsApplicationProcess(process, root))
                {
                    throw new UpdateException(UpdateState.Failed);
                }
            }
        }
    }

    private static bool IsApplicationProcess(Process process, string root)
    {
        var path = process.MainModule?.FileName;
        return path is not null && string.Equals(Path.GetFileName(path), PortablePackage.Executable, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(PortableFileSystem.ResolveRoot(Path.GetDirectoryName(path)!), root, StringComparison.OrdinalIgnoreCase);
    }

    private static void VerifyStartup(string root, UpdateVersion version)
    {
        var start = new ProcessStartInfo(PortablePackage.PayloadPath(root, PortablePackage.Executable))
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        start.ArgumentList.Add("--verify-wincore-package");
        start.ArgumentList.Add(version.ToString());
        using var probe = Process.Start(start) ?? throw new UpdateException(UpdateState.Failed);
        if (!probe.WaitForExit(20_000))
        {
            probe.Kill();
            probe.WaitForExit();
            throw new UpdateException(UpdateState.Failed);
        }
        if (probe.ExitCode != 0)
        {
            throw new UpdateException(UpdateState.Failed);
        }
    }

    private static void Restart(string root)
    {
        var start = new ProcessStartInfo(PortablePackage.PayloadPath(root, PortablePackage.Executable))
        {
            WorkingDirectory = root,
            UseShellExecute = false,
        };
        using var restarted = Process.Start(start) ?? throw new UpdateException(UpdateState.Failed);
    }

    private static void NotifyFailure(string root)
    {
        var english = false;
        try
        {
            english = IsEnglish(Path.Combine(root, "settings.json"), out var portable);
            if (!portable)
            {
                var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
                english = IsEnglish(Path.Combine(local, "WinCoreManager", "settings.json"), out _);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or UpdateException or InvalidOperationException) { }

        var culture = CultureInfo.GetCultureInfo(english ? "en-US" : "pt-BR");
        var resources = new ResourceManager("WinCoreManager.Localization.Strings", typeof(Program).Assembly);
        var message = resources.GetString("Updater.HelperFailure", culture)!;
        message += "\n" + string.Format(culture, resources.GetString("Updater.HelperRecovery", culture)!,
            Path.Combine(PortablePackage.RecoveryDirectory, PortablePackage.RecoveryHelper));
        Notify(message, warning: true);
    }

    private static bool IsEnglish(string settings, out bool portable)
    {
        portable = false;
        PortablePackage.CheckPath(settings);
        if (!File.Exists(settings) || new FileInfo(settings).Length > 64 * 1024)
        {
            return false;
        }

        using var document = JsonDocument.Parse(File.ReadAllText(settings));
        portable = !document.RootElement.TryGetProperty("PortableMode", out var mode) || mode.ValueKind == JsonValueKind.True;
        return document.RootElement.TryGetProperty("Language", out var language) && language.ValueKind == JsonValueKind.String &&
            string.Equals(language.GetString(), "en-US", StringComparison.OrdinalIgnoreCase);
    }

    private static void Notify(string message, bool warning)
        => MessageBoxW(IntPtr.Zero, message, "WinCore Manager", warning ? 0x30u : 0x40u);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);
}
