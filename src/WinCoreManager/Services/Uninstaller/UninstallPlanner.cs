/*
 * Adaptado de Bulk Crap Uninstaller (source/UninstallTools/Uninstaller/UninstallManager.cs)
 * Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/) — Apache License Version 2.0
 * http://www.apache.org/licenses/LICENSE-2.0
 * Alterações: planejamento separado da execução; fallback para shell do BCU substituído por
 * recusa segura (FormatException → plano não executável); cadeias via cmd.exe são recusadas;
 * argumentos MSI sempre suprimem reinício automático.
 */

using WinCoreManager.Localization;

using System.IO;
using WinCoreManager.Models;

namespace WinCoreManager.Services.Uninstaller;

/// <summary>Monta o plano de desinstalação de um programa (como executar e quais avisos mostrar).</summary>
public interface IUninstallPlanner
{
    UninstallPlan CreatePlan(InstalledProgram program, bool silentPreferred);
}

public sealed class UninstallPlanner : IUninstallPlanner
{
    private static readonly HashSet<string> BlockedExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "cscript", "wscript", "mshta", "powershell", "powershell_ise", "pwsh",
        "rundll32", "regsvr32", "msiexec",
    };

    public UninstallPlan CreatePlan(InstalledProgram program, bool silentPreferred)
    {
        var warnings = new List<string>();

        if (program.IsProtected)
        {
            warnings.Add(LocalizationService.Source("Message.ThePublisherMarkedThisEntryNoRemoveUninstallMay"));
        }

        if (program.IsSystemComponent)
        {
            warnings.Add(LocalizationService.Source("Message.ThisEntryIsMarkedAsASystemComponent"));
        }

        if (program.IsUpdate)
        {
            warnings.Add(LocalizationService.Source("Message.ThisEntryIsASystemUpdateOrHotfix"));
        }

        // Revalidação da política na camada de execução: a decisão não vem da interface.
        var classification = ProgramClassifier.Evaluate(program);
        if (classification.Policy == RemovalPolicy.Blocked)
        {
            return new UninstallPlan
            {
                Program = program,
                Method = UninstallMethod.NotSupported,
                Warnings = warnings,
                NotExecutableReason = LocalizationService.Source("Message.UninstallBlockedByProtectionPolicy")
                    + $"({ProgramClassificationLabels.Category(classification.Category)}): "
                    + string.Join(" ", classification.Reasons),
            };
        }

        if (classification.Policy == RemovalPolicy.RequiresReview)
        {
            warnings.Add(LocalizationService.Source("Message.ReviewRequiredSharedDependencyOrUnrecognizedApp"));
        }

        var plan = program.Kind == ProgramKind.Msi
            ? TryCreateMsiPlan(program, warnings)
            : TryCreateExecutablePlan(program, silentPreferred, warnings);

        if (plan is null)
        {
            return new UninstallPlan
            {
                Program = program,
                Method = UninstallMethod.NotSupported,
                Warnings = warnings,
                NotExecutableReason = DescribeUnsupportedReason(program),
            };
        }

        return plan;
    }

    private static UninstallPlan? TryCreateMsiPlan(InstalledProgram program, List<string> warnings)
    {
        if (program.MsiProductCode == Guid.Empty)
        {
            warnings.Add(LocalizationService.Source("Message.TheRegistryHasNoMSIProductCodeGUID"));
            return null;
        }

        // msiexec.exe é resolvido do diretório do sistema — nunca da string do Registro.
        var msiexec = Path.Combine(Environment.SystemDirectory, "msiexec.exe");
        // REBOOT=ReallySuppress /norestart: nunca reinicia o Windows automaticamente.
        var arguments = $"/x{program.MsiProductCode:B} REBOOT=ReallySuppress /norestart";
        warnings.Add(LocalizationService.Source("Message.UninstallThroughWindowsInstallerMsiexecWithAutomaticRestart"));

        return new UninstallPlan
        {
            Program = program,
            Method = UninstallMethod.Msi,
            ExecutablePath = msiexec,
            Arguments = arguments,
            Warnings = warnings,
        };
    }

    private static UninstallPlan? TryCreateExecutablePlan(InstalledProgram program, bool silentPreferred, List<string> warnings)
    {
        switch (program.Kind)
        {
            case ProgramKind.PowerShell:
            case ProgramKind.SdbInst:
            case ProgramKind.Steam:
            case ProgramKind.StoreApp:
            case ProgramKind.WindowsFeature:
            case ProgramKind.WindowsUpdate:
                // Esta versão não executa scripts nem APIs auxiliares — apenas lista.
                return null;
        }

        var command = silentPreferred && program.HasQuietUninstall
            ? program.QuietUninstallString
            : program.UninstallString;

        if (command.Length == 0)
        {
            return null;
        }

        // Cadeias de comandos iniciadas por interpretadores do Windows são recusadas antes
        // mesmo da divisão — não executamos nada proveniente do Registro via shell.
        if (IsShellChainCommand(command))
        {
            warnings.Add(LocalizationService.Source("Message.ForSafetyCommandChainsThroughCmdOrA"));
            return null;
        }

        if (!UninstallCommandSplitter.TrySeparate(command, out var split))
        {
            // Sem divisão segura não há execução — diferente do BCU, não recorremos ao shell.
            warnings.Add(LocalizationService.Source("Message.CouldNotSafelyInterpretTheUninstallCommand"));
            return null;
        }

        if (BlockedExecutables.Contains(Path.GetFileNameWithoutExtension(split.FileName).TrimEnd(' ', '.')))
        {
            warnings.Add(LocalizationService.Source("Message.ForSafetyCommandChainsThroughCmdOrA"));
            return null;
        }

        var resolved = ResolveExecutable(split.FileName, program.InstallLocation);
        if (resolved is null)
        {
            warnings.Add(LocalizationService.Source("Message.TheUninstallerExecutableWasNotFoundAtThe"));
            return null;
        }

        if (program.Kind == ProgramKind.InstallShield)
        {
            warnings.Add(LocalizationService.Source("Message.InstallShieldUninstallersMayNeedTheOriginalInstallationCache"));
        }

        return new UninstallPlan
        {
            Program = program,
            Method = UninstallMethod.Executable,
            ExecutablePath = resolved,
            Arguments = split.Arguments,
            Warnings = warnings,
        };
    }

    /// <summary>cmd/cscript/wscript/mshta e similares não são iniciados com argumentos do Registro.</summary>
    private static bool IsShellChainCommand(string rawCommand)
    {
        foreach (var interpreter in new[] { "cmd ", "cmd.exe ", "cscript ", "cscript.exe ", "wscript ", "wscript.exe ", "mshta ", "mshta.exe " })
        {
            if (rawCommand.StartsWith(interpreter, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    internal static string? ResolveExecutable(string fileName, string installLocation)
    {
        try
        {
            string candidate;
            if (Path.IsPathRooted(fileName))
            {
                if (!IsDrivePath(fileName))
                {
                    return null;
                }

                candidate = Path.GetFullPath(fileName);
            }
            else
            {
                if (!IsDrivePath(installLocation) || fileName.Contains(':'))
                {
                    return null;
                }

                var installRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(installLocation));
                var prefix = Path.EndsInDirectorySeparator(installRoot) ? installRoot : installRoot + Path.DirectorySeparatorChar;
                candidate = Path.GetFullPath(fileName, installRoot);
                if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || candidate.Length == prefix.Length)
                {
                    return null;
                }
            }

            if (!Path.HasExtension(candidate))
            {
                candidate += ".exe";
            }

            if (!Path.GetExtension(candidate).Equals(".exe", StringComparison.OrdinalIgnoreCase)
                || BlockedExecutables.Contains(Path.GetFileNameWithoutExtension(candidate)))
            {
                return null;
            }

            return IsPlainLocalPath(candidate, directory: false) ? candidate : null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    internal static bool IsPlainLocalPath(string path, bool directory)
    {
        if (!IsDrivePath(path)) return false;
        var root = Path.GetPathRoot(path)!;
        try
        {
            if (new DriveInfo(root).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
                return false;

            var parts = path[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            var current = root;
            for (var index = 0; index < parts.Length; index++)
            {
                current = Path.Combine(current, parts[index]);
                var attributes = File.GetAttributes(current);
                var isDirectory = (attributes & FileAttributes.Directory) != 0;
                if ((attributes & FileAttributes.ReparsePoint) != 0 || isDirectory != (directory || index < parts.Length - 1))
                    return false;
            }

            return parts.Length > 0 || directory;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return false;
        }
    }

    private static bool IsDrivePath(string path) => Path.IsPathFullyQualified(path)
        && Path.GetPathRoot(path) is { Length: 3 } && char.IsAsciiLetter(path[0]) && path[1] == ':'
        && !path.AsSpan(2).Contains(':');

    private static string DescribeUnsupportedReason(InstalledProgram program)
    {
        return program.Kind switch
        {
            ProgramKind.PowerShell => LocalizationService.Source("Message.ThisVersionBlocksUninstallThroughPowerShellScripts"),
            ProgramKind.SdbInst => LocalizationService.Source("Message.ThisVersionBlocksUninstallThroughSdbInst"),
            ProgramKind.Steam => LocalizationService.Source("Message.UninstallThroughTheSteamClientWhichTheRegistry"),
            ProgramKind.StoreApp => LocalizationService.Source("Message.ToUninstallMicrosoftStoreAppsUseWindowsStart"),
            ProgramKind.WindowsFeature => LocalizationService.Source("Message.ManageWindowsFeaturesInTurnWindowsFeaturesOn"),
            ProgramKind.WindowsUpdate => LocalizationService.Source("Message.ManageWindowsUpdatesInWindowsUpdate"),
            _ when !program.HasUninstallString && !program.IsMsi => LocalizationService.Source("Message.ThisEntryHasNoUninstallerInTheRegistry"),
            _ => LocalizationService.Source("Message.CouldNotBuildASafeUninstallCommandFor"),
        };
    }
}
