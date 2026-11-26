/*
 * Adaptado de Bulk Crap Uninstaller (source/KlocTools/Tools/ProcessTools.cs, SeparateArgsFromCommand)
 * Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/) — Apache License Version 2.0
 * http://www.apache.org/licenses/LICENSE-2.0
 * Alterações: extraído para classe dedicada, mensagens em pt-BR, remoção da localização original.
 */

using WinCoreManager.Localization;

using System.IO;
using System.Text;

namespace WinCoreManager.Services.Uninstaller;

/// <summary>Comando de desinstalação dividido em executável e argumentos.</summary>
public readonly record struct UninstallCommand(string FileName, string Arguments);

/// <summary>
/// Divide a string UninstallString do Registro em executável + argumentos usando as mesmas
/// heurísticas do BCUninstaller (aspas, caminhos com pontos, unidades mapeadas etc.).
/// Quando não consegue dividir com segurança, lança FormatException — o chamador deve
/// recusar a execução em vez de recorrer ao shell.
/// </summary>
public static class UninstallCommandSplitter
{
    private static readonly char[] InvalidChars =
        Path.GetInvalidFileNameChars().Concat(new[] { ',', ';' }).ToArray();

    public static bool TrySeparate(string? fullCommand, out UninstallCommand result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(fullCommand))
        {
            return false;
        }

        try
        {
            result = SeparateArgsFromCommand(fullCommand);
            return result.FileName.Length > 0;
        }
        catch (FormatException)
        {
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    public static UninstallCommand SeparateArgsFromCommand(string fullCommand)
    {
        ArgumentNullException.ThrowIfNull(fullCommand);

        fullCommand = fullCommand.Trim();
        if (fullCommand.Length == 0)
        {
            throw new FormatException(LocalizationService.Source("Message.TheUninstallCommandIsEmpty"));
        }

        if (fullCommand[0] == '"')
        {
            var closingQuote = fullCommand.IndexOf('"', 1);
            if (closingQuote < 0)
            {
                throw new FormatException(LocalizationService.Source("Message.UnclosedOpeningQuotationMark") + fullCommand);
            }

            return SeparateCommand(fullCommand, closingQuote + 1);
        }

        var firstDot = fullCommand.IndexOf('.');
        if (firstDot < 0)
        {
            return SeparateNonDottedCommand(fullCommand);
        }

        // Formato "Executavel C:\caminho\arquivo.exe" — o executável vem antes do caminho do argumento.
        {
            var pathRoot = fullCommand.IndexOf(":\\", StringComparison.Ordinal);
            var firstSpace = fullCommand.IndexOf(' ');
            if (firstSpace >= 0 && firstSpace < pathRoot)
            {
                var filenameBreaker = fullCommand.IndexOfAny(InvalidChars, 0, pathRoot - 1);
                if (filenameBreaker < 0)
                {
                    var slashIndex = fullCommand.IndexOf('\\');
                    if (slashIndex >= 0 && slashIndex > pathRoot)
                    {
                        var rootSpace = fullCommand.LastIndexOf(' ', pathRoot);
                        return new UninstallCommand(
                            fullCommand.Substring(0, rootSpace).TrimEnd(),
                            fullCommand.Substring(rootSpace).TrimStart());
                    }
                }
            }
        }

        // Caminho entre aspas: a aspa de abertura deve vir antes do primeiro ponto.
        var pathEnd = fullCommand.IndexOf('"', 0, firstDot);
        if (pathEnd >= 0)
        {
            pathEnd = fullCommand.IndexOf('"', pathEnd + 1);
            if (pathEnd < 0)
            {
                throw new FormatException(LocalizationService.Source("Message.UnclosedOpeningQuotationMark") + fullCommand);
            }

            pathEnd += 1;
        }

        // Sem aspas: localiza o fim do executável depois do último ponto válido,
        // ignorando pontos de argumentos (ex.: "c:\teste.dir coisa\arquivo.exe?0").
        if (pathEnd < 0)
        {
            var endIndex = 0;
            while (true)
            {
                var dot = fullCommand.IndexOf('.', endIndex);
                if (dot < 0)
                {
                    break;
                }

                var filenameBreaker = fullCommand.IndexOfAny(InvalidChars, dot);
                var space = fullCommand.IndexOf(' ', dot);
                if (filenameBreaker < 0)
                {
                    if (space < 0)
                    {
                        break;
                    }

                    filenameBreaker = space;
                }

                var dash = fullCommand.IndexOf('\\', dot);
                if (filenameBreaker < dash || dash < 0)
                {
                    pathEnd = space < 0 ? filenameBreaker : space;
                    break;
                }

                var nextBreaker = fullCommand.IndexOfAny(InvalidChars, filenameBreaker + 1);
                var nextDash = fullCommand.IndexOf('\\', filenameBreaker + 1);

                if (nextBreaker > 0 && (nextDash < 0 || nextBreaker < nextDash))
                {
                    var nextDot = fullCommand.IndexOf('.', filenameBreaker + 1);
                    if (nextDot < 0 || nextBreaker < nextDot)
                    {
                        pathEnd = space < 0 ? filenameBreaker : space;
                        break;
                    }
                }

                endIndex = dash;
            }
        }

        return SeparateCommand(fullCommand, pathEnd);
    }

    private static UninstallCommand SeparateCommand(string fullCommand, int splitIndex)
    {
        string filename;
        var args = string.Empty;

        if (splitIndex < 0 || splitIndex >= fullCommand.Length)
        {
            filename = fullCommand;
        }
        else
        {
            filename = fullCommand.Substring(0, splitIndex).TrimEnd();
            args = fullCommand.Substring(splitIndex).TrimStart();
        }

        filename = filename.Trim('"');
        return new UninstallCommand(filename, args);
    }

    private static UninstallCommand SeparateNonDottedCommand(string fullCommand)
    {
        var pathRoot = fullCommand.IndexOf(":\\", StringComparison.Ordinal);
        var pathRootEnd = pathRoot < 0 ? 0 : pathRoot + 2;

        var breakChars = InvalidChars.Except(new[] { '\\' }).ToArray();

        // Caractere inválido antes da raiz indica que o caminho é um argumento.
        if (pathRootEnd > 0 && fullCommand.IndexOfAny(breakChars, 0, pathRootEnd - 2) >= 0)
        {
            pathRootEnd = 0;
        }

        var breakIndex = fullCommand.IndexOfAny(breakChars, pathRootEnd);

        // Sem caracteres inválidos: nome ou caminho puro.
        if (breakIndex < 0)
        {
            return new UninstallCommand(fullCommand.Trim('"'), string.Empty);
        }

        // O caractere precisa de um espaço antes para contar como início de argumento.
        if (breakIndex > 0 && fullCommand[breakIndex - 1] == ' ')
        {
            return new UninstallCommand(
                fullCommand.Substring(0, breakIndex - 1).TrimEnd(),
                fullCommand.Substring(breakIndex));
        }

        throw new FormatException(LocalizationService.Source("Message.CouldNotSeparateTheExecutableFromItsArguments") + fullCommand);
    }
}
