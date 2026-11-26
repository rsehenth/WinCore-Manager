/*
 * Adaptado de Bulk Crap Uninstaller (Klocman/Tools/Sift4.cs, Klocman/Tools/StringTools.cs e
 * UninstallTools/Junk/Confidence/ConfidenceGenerators.cs)
 * Copyright (c) 2017 Marcin Szeniak (https://github.com/Klocman/) — Apache License Version 2.0
 * Sift4: algoritmo de Siderite (https://siderite.blogspot.com/2014/11/super-fast-and-accurate-string-distance.html)
 * Alterações: apenas SimplestDistance é portado; nomes e documentação em pt-BR.
 */

namespace WinCoreManager.Services.Uninstaller;

/// <summary>
/// Correspondência entre nomes de itens (pastas, chaves, arquivos) e o nome do programa.
/// Porta fiel das regras do BCU: distância Sift4 com cortes determinísticos.
/// </summary>
public static class ResidueNameMatcher
{
    /// <summary>
    /// Resultado da comparação com o nome do produto.
    /// </summary>
    public enum MatchQuality
    {
        /// <summary>Sem correspondência aceitável.</summary>
        None = -1,
        /// <summary>Igualdade ou diferença de uma posição, sem inferir propriedade.</summary>
        Perfect = 0,
        /// <summary>Um nome contém o outro.</summary>
        Containment = 2,
        /// <summary>Distância menor que um terço do menor nome (correspondência fraca).</summary>
        Weak = 3,
    }

    /// <summary>Distância Sift4 simples (port fiel de Sift4.SimplestDistance).</summary>
    public static int SimplestDistance(string s1, string s2, int maxOffset)
    {
        var l1 = s1?.Length ?? 0;
        var l2 = s2?.Length ?? 0;

        if (l1 == 0)
        {
            return l2;
        }

        if (l2 == 0)
        {
            return l1;
        }

        var c1 = 0;
        var c2 = 0;
        var lcss = 0;
        var localCs = 0;

        while (c1 < l1 && c2 < l2)
        {
            if (s1![c1] == s2![c2])
            {
                localCs++;
            }
            else
            {
                lcss += localCs;
                localCs = 0;
                if (c1 != c2)
                {
                    c1 = c2 = Math.Max(c1, c2);
                }

                if (c1 >= l1 || c2 >= l2) break;
                for (var i = 1; i <= maxOffset && (c1 + i < l1 || c2 + i < l2); i++)
                {
                    if (c1 + i < l1 && s1[c1 + i] == s2[c2])
                    {
                        c1 += i - 1;
                        c2--;
                        break;
                    }

                    if (c2 + i < l2 && s1[c1] == s2[c2 + i])
                    {
                        c1--;
                        c2 += i - 1;
                        break;
                    }
                }
            }

            c1++;
            c2++;
        }

        lcss += localCs;
        return Math.Max(l1, l2) - lcss;
    }

    /// <summary>
    /// Remove versões e sufixos ruidosos do nome de exibição (port de StringTools.StripStringFromVersionNumber).
    /// </summary>
    public static string TrimProductName(string? input)
    {
        if (input is null)
        {
            return string.Empty;
        }

        string previous;
        do
        {
            previous = input;

            input = input.Trim();
            if (input.Length == 0)
            {
                return string.Empty;
            }

            if (input.EndsWith(')'))
            {
                var bracket = input.LastIndexOf('(');
                if (bracket > 4)
                {
                    input = input[..bracket].TrimEnd();
                }
            }

            input = TrimEndAny(input, ["1", "2", "3", "4", "5", "6", "7", "8", "9", "0", ".", ",", "-", "_"]).TrimEnd();

            if (input.EndsWith(" v", StringComparison.InvariantCultureIgnoreCase))
            {
                input = input[..^2];
            }

            input = TrimEndAny(input, ["Application", "Helper", " v", " CE"]).TrimEnd();
        }
        while (previous.Length != input.Length);

        return input;
    }

    private static string TrimEndAny(string source, IReadOnlyList<string> trimmers)
    {
        var result = source.Trim().Trim(',', '.', ' ');
        var rerun = true;
        while (rerun)
        {
            rerun = false;
            foreach (var trimmer in trimmers)
            {
                if (!result.EndsWith(trimmer, StringComparison.InvariantCultureIgnoreCase))
                {
                    continue;
                }

                var cut = result.Length - trimmer.Length;
                if (cut <= 0)
                {
                    return string.Empty;
                }

                result = result[..cut].Trim().Trim(',', '.', ' ');
                rerun = true;
                break;
            }
        }

        return result;
    }

    /// <summary>
    /// Compara um candidato (nome de pasta/chave) com o nome do produto.
    /// Retorna (qualidade, distância bruta). Porta de MatchStringToProductName.
    /// </summary>
    public static (MatchQuality Quality, int Similarity) MatchProduct(string productName, string candidate)
    {
        var product = TrimProductName(productName).ToLowerInvariant();
        var item = (candidate ?? string.Empty).Replace('_', ' ').ToLowerInvariant().Trim();
        var lowestLength = Math.Min(product.Length, item.Length);

        if (IsGenericName(product) || IsGenericName(item))
        {
            return (MatchQuality.None, -1);
        }

        // Nomes curtos só correspondem por igualdade exata.
        if (lowestLength <= 4)
        {
            return lowestLength >= 3 && product == item ? (MatchQuality.Perfect, 0) : (MatchQuality.None, -1);
        }

        var directDistance = SimplestDistance(product, item, 0);
        if (directDistance <= 1)
        {
            return (MatchQuality.Perfect, directDistance);
        }

        // Quando o nome do produto contém o fabricante, tenta sem o fabricante.
        // (o chamador passa o nome já sem fabricante quando aplicável, como no BCU)
        if (ContainsWholeName(item, product) || ContainsWholeName(product, item))
        {
            return (MatchQuality.Containment, 2);
        }

        var distance = SimplestDistance(product, item, 1);
        if (distance < lowestLength / 3)
        {
            return (MatchQuality.Weak, distance);
        }

        return (MatchQuality.None, -1);
    }

    internal static bool IsExactProductName(string productName, string candidate) =>
        TrimProductName(productName).Equals(candidate.Replace('_', ' ').Trim(), StringComparison.OrdinalIgnoreCase);

    private static bool ContainsWholeName(string text, string name)
    {
        for (var start = text.IndexOf(name, StringComparison.Ordinal); start >= 0;
            start = text.IndexOf(name, start + 1, StringComparison.Ordinal))
        {
            var end = start + name.Length;
            if ((start == 0 || !char.IsLetterOrDigit(text[start - 1])) &&
                (end == text.Length || !char.IsLetterOrDigit(text[end])))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsGenericName(string name) =>
        name is "app" or "bin" or "git" or "java" or "lib" or "net" or "win" or "player" or "setup";

    internal static bool MatchesPublisherName(string publisher, string candidate)
    {
        string[] suffixes = [" Incorporated", " Corporation", " Limited", " s.r.o", " GmbH", " Inc", " LLC", " Ltd", " Corp"];
        var normalized = TrimEndAny(publisher, suffixes);
        return normalized.Length > 3 && normalized.Equals(TrimEndAny(candidate, suffixes), StringComparison.OrdinalIgnoreCase);
    }
}
