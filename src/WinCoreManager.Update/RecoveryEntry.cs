using System.ComponentModel;
using System.Globalization;
using System.Text;
using WinCoreManager.Services.SelfUpdate;

internal static class RecoveryEntry
{
    private enum Result
    {
        HashesVerified = 0,
        InvalidInvocation = 2,
        ManualRequired = 3,
        Busy = 4,
        AccessDenied = 5,
        InsufficientSpace = 6,
        Recovered = 7,
        SignedPackageVerified = 8,
    }

    internal static int Run(string[] args, Action<string> ensureAppClosed, Action<string, bool> notify)
    {
        var quiet = args is ["--quiet"];
        var result = Result.InvalidInvocation;
        string? name = null;
        try
        {
            if (Environment.IsPrivilegedProcess) return Finish(Result.AccessDenied, quiet, null, notify);
            if (args.Length != 0 && !quiet) return (int)result;

            var entry = new DirectoryInfo(AppContext.BaseDirectory);
            if (entry.Name != PortablePackage.RecoveryDirectory || entry.Parent is not { } installation)
                return Finish(result, quiet, null, notify);

            var root = PortableFileSystem.ResolveRoot(installation.FullName);
            var recovery = Path.Combine(root, PortablePackage.RecoveryDirectory);
            using var paths = new PortableFileSystem();
            paths.PinDirectory(recovery);
            var helper = Path.Combine(recovery, PortablePackage.RecoveryHelper);
            using var ownFile = PortableFileSystem.OpenRead(helper);
            var logName = $"recovery-{Guid.NewGuid():N}.log";
            using var log = paths.CreateNew(Path.Combine(recovery, logName));
            name = logName;
            Record(log, "Started");
            string? error = null;
            try
            {
                result = Recover(root, paths, ensureAppClosed);
            }
            catch (Exception ex) when (IsRecoveryFailure(ex))
            {
                result = Classify(ex);
                error = ex is UpdateException update
                    ? update.State.ToString()
                    : $"{ex.GetType().Name} 0x{ex.HResult:X8}";
            }
            Record(log, result.ToString(), error);
        }
        catch (Exception ex) when (IsRecoveryFailure(ex))
        {
            result = Classify(ex);
            name = null;
        }
        return Finish(result, quiet, name, notify);
    }

    private static Result Recover(string root, PortableFileSystem paths, Action<string> ensureAppClosed)
    {
        using var lease = PortableUpdateLease.TryAcquire(root);
        if (lease is null) return Result.Busy;

        var work = Path.Combine(root, PortablePackage.WorkDirectory);
        PortablePackage.CheckPath(work);
        if (Directory.Exists(work)) paths.PinDirectory(work);
        var pending = PortableUpdateTransaction.FindPending(root);
        if (pending.Count > 1) return Result.ManualRequired;

        var publisherKey = ReadRecoveryKey();
        if (pending.Count == 1)
        {
            if (publisherKey is null) return Result.ManualRequired;
            ensureAppClosed(root);
            var transaction = new PortableUpdateTransaction(root, pending[0]);
            transaction.RollbackAuthenticated(publisherKey);
            return Result.Recovered;
        }

        var signature = PortablePackage.PayloadPath(root, PortablePackage.ManifestSignature);
        var signed = Exists(signature);
        if (signed && publisherKey is null) return Result.ManualRequired;
        var manifest = signed
            ? PortablePackage.ReadSignedManifest(root, publisherKey!).Files
            : PortablePackage.ReadManifest(root);
        PortablePackage.VerifyFiles(root, manifest);
        PortablePackage.VerifyAvailableSignatures(root);
        return signed ? Result.SignedPackageVerified : Result.HashesVerified;
    }

    private static bool Exists(string path)
    {
        try { return File.GetAttributes(path) != 0; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static string? ReadRecoveryKey()
    {
        // This build-only resource is for disposable fixtures. The updater never reads it.
        using var key = typeof(RecoveryEntry).Assembly.GetManifestResourceStream("WinCoreManager.Recovery.TestPublicKey");
        if (key is null) return ReleaseTrust.PublisherPublicKey;
        if (ReleaseTrust.PublisherPublicKey is not null || key.Length is <= 0 or > 256)
            throw new UpdateException(UpdateState.VerificationFailed);
        using var reader = new StreamReader(key, Encoding.UTF8);
        return reader.ReadToEnd().Trim();
    }

    private static void Record(FileStream log, string state, string? error = null)
    {
        var line = string.Create(CultureInfo.InvariantCulture, $"{DateTimeOffset.UtcNow:O} {state} {error}\r\n");
        log.Write(Encoding.UTF8.GetBytes(line));
        log.Flush(flushToDisk: true);
    }

    internal static bool IsRecoveryFailure(Exception ex) => ex is IOException or UnauthorizedAccessException or
        InvalidOperationException or UpdateException or Win32Exception or BadImageFormatException or InvalidDataException
        or System.Security.SecurityException;

    private static Result Classify(Exception ex) => ex switch
    {
        UnauthorizedAccessException or System.Security.SecurityException or Win32Exception { NativeErrorCode: 5 } => Result.AccessDenied,
        UpdateException { State: UpdateState.InsufficientSpace } => Result.InsufficientSpace,
        IOException io when (io.HResult & 0xffff) is 39 or 112 => Result.InsufficientSpace,
        IOException io when (io.HResult & 0xffff) is 32 or 33 => Result.Busy,
        UpdateException { State: UpdateState.Failed } => Result.Busy,
        _ => Result.ManualRequired,
    };

    private static int Finish(Result result, bool quiet, string? log, Action<string, bool> notify)
    {
        var success = result is Result.HashesVerified or Result.Recovered or Result.SignedPackageVerified;
        if (!quiet)
        {
            var (pt, en) = result switch
            {
                Result.HashesVerified => ("Sem atualização pendente. Os hashes locais conferem. A origem do pacote não foi autenticada.",
                    "No update is pending. Local hashes match. The package origin was not authenticated."),
                Result.SignedPackageVerified => ("Sem atualização pendente. Assinatura do manifesto e hashes verificados.",
                    "No update is pending. The manifest signature and hashes were verified."),
                Result.Recovered => ("Pacote anterior restaurado e verificado. Pode abrir o WinCore Manager.",
                    "The previous package was restored and verified. You can open WinCore Manager."),
                Result.Busy => ("Feche o WinCore Manager e aguarde o atualizador ou libere os arquivos em uso. Tente novamente.",
                    "Close WinCore Manager and wait for the updater or release any locked files. Try again."),
                Result.AccessDenied => ("Acesso negado. Use uma conta padrão com acesso de gravação à sua cópia portátil.",
                    "Access denied. Use a standard account with write access to your portable copy."),
                Result.InsufficientSpace => ("Espaço insuficiente. Libere espaço fora da pasta portátil e tente novamente.",
                    "Insufficient disk space. Free space outside the portable folder and try again."),
                Result.InvalidInvocation => ("Abra WinCoreManager.Recover.exe na pasta Recovery da cópia portátil.",
                    "Open WinCoreManager.Recover.exe from the portable copy's Recovery folder."),
                _ => ("O estado não permite recuperação automática segura.",
                    "The installation state cannot be recovered automatically with confidence."),
            };
            if (!success)
            {
                pt += "\nPreserve a pasta original. Consulte RECUPERACAO-RECOVERY.md para usar uma cópia íntegra em outra pasta.";
                en += "\nKeep the original folder. See RECUPERACAO-RECOVERY.md to use a verified copy in a new folder.";
            }
            var path = log is null ? "" : $"\n\nLog: {PortablePackage.RecoveryDirectory}\\{log}";
            notify($"{pt}\n\n{en}{path}", !success);
        }
        return success ? 0 : (int)result;
    }
}
