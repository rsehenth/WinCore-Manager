using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;

namespace WinCoreManager.Services.SelfUpdate;

internal sealed partial record UpdateVersion(int Major, int Minor, int Patch, string Prerelease) : IComparable<UpdateVersion>
{
    public bool IsStable => Prerelease.Length == 0;

    public static bool TryParse(string? tag, out UpdateVersion version)
    {
        version = null!;
        if (tag is null || tag.Length > 128)
        {
            return false;
        }

        var match = VersionPattern().Match(tag);
        if (!match.Success ||
            !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var major) ||
            !int.TryParse(match.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var minor) ||
            !int.TryParse(match.Groups[3].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var patch))
        {
            return false;
        }

        var prerelease = match.Groups[4].Value;
        if (prerelease.Split('.').Any(part => part.Length > 1 && part[0] == '0' && part.All(char.IsAsciiDigit)))
        {
            return false;
        }

        version = new UpdateVersion(major, minor, patch, prerelease);
        return true;
    }

    public static UpdateVersion FromAssembly(Assembly assembly)
    {
        var metadata = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        return TryParse(metadata, out var version)
            ? version
            : throw new InvalidOperationException("Invalid application version metadata.");
    }

    public int CompareTo(UpdateVersion? other)
    {
        if (other is null)
        {
            return 1;
        }

        var numeric = (Major, Minor, Patch).CompareTo((other.Major, other.Minor, other.Patch));
        if (numeric != 0 || IsStable || other.IsStable)
        {
            return numeric != 0 ? numeric : IsStable.CompareTo(other.IsStable);
        }

        var left = Prerelease.Split('.');
        var right = other.Prerelease.Split('.');
        for (var i = 0; i < Math.Min(left.Length, right.Length); i++)
        {
            var a = left[i];
            var b = right[i];
            var aNumeric = a.All(char.IsAsciiDigit);
            var bNumeric = b.All(char.IsAsciiDigit);
            var comparison = aNumeric == bNumeric
                ? (aNumeric && a.Length != b.Length ? a.Length.CompareTo(b.Length) : string.CompareOrdinal(a, b))
                : bNumeric.CompareTo(aNumeric);
            if (comparison != 0)
            {
                return comparison;
            }
        }

        return left.Length.CompareTo(right.Length);
    }

    public override string ToString() => FormattableString.Invariant(
        $"{Major}.{Minor}.{Patch}{(IsStable ? "" : "-" + Prerelease)}");

    [GeneratedRegex(@"\Av?(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-([0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?\z",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex VersionPattern();
}
