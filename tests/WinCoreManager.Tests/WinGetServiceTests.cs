using WinCoreManager.Models;
using WinCoreManager.Services;

namespace WinCoreManager.Tests;

public class WinGetServiceTests
{
    private const string OfficialWinget = """
        {"Name":"winget","Arg":"https://cdn.winget.microsoft.com/cache","Type":"Microsoft.PreIndexed.Package","Identifier":"Microsoft.Winget.Source_8wekyb3d8bbwe","Data":"Microsoft.Winget.Source_8wekyb3d8bbwe","TrustLevel":["Trusted","StoreOrigin"]}
        """;

    [Fact]
    public async Task BattleNet_without_launcher_directory_is_rejected_before_upgrade()
    {
        var launches = 0;
        var service = new WinGetService(() => @"C:\Fixture\winget.exe", (start, _, _, _, _) =>
        {
            if (start.ArgumentList[0] == "upgrade") launches++;
            return Task.FromResult(new ProcessRunResult { Output = [OfficialWinget] });
        }, () => false);

        var result = await service.UpgradePackageAsync(new()
        {
            Id = "Blizzard.BattleNet", Name = "Battle.net", Source = "winget",
            InstalledVersion = "1.0", AvailableVersion = "2.0.0.4232",
        }, null, default);

        Assert.True(result.StartFailed);
        Assert.Equal(0, launches);
    }

    [Theory]
    [InlineData(true, true, 3, true)]
    [InlineData(true, true, 2, true)]
    [InlineData(false, true, 3, false)]
    [InlineData(true, false, 3, false)]
    [InlineData(true, true, 5, false)]
    public void Audit12H09_alias_matches_the_registered_microsoft_package(bool familyMatches, bool targetMatches,
        int origin, bool expected)
    {
        const string local = @"C:\Fixture\User";
        const string installed = @"C:\Program Files\WindowsApps\Fixture";
        var family = familyMatches ? WinGetTrust.AppInstallerFamily : "Other.App";
        var target = Path.Combine(targetMatches ? installed : local, "winget.exe");
        var actual = WinGetTrust.ResolveAlias(local, () => [new(WinGetTrust.AppInstallerFamily, (AppInstallerOrigin)origin, installed)],
            _ => true, _ => (family, target));
        Assert.Equal(expected, actual is not null);
        if (expected)
            Assert.Equal(Path.Combine(local, "Microsoft", "WindowsApps", WinGetTrust.AppInstallerFamily, "winget.exe"), actual);
    }

    [Fact]
    public void Audit12H09_reparse_data_rejects_unknown_versions_and_truncation()
    {
        var family = WinGetTrust.AppInstallerFamily;
        const string target = @"C:\Fixture\winget.exe";
        var payload = System.Text.Encoding.Unicode.GetBytes($"{family}\0{family}!App\0{target}\00\0");
        var bytes = new byte[payload.Length + 12];
        BitConverter.GetBytes(0x8000001Bu).CopyTo(bytes, 0);
        BitConverter.GetBytes((ushort)(payload.Length + 4)).CopyTo(bytes, 4);
        BitConverter.GetBytes(3u).CopyTo(bytes, 8);
        payload.CopyTo(bytes, 12);
        Assert.Equal((family, target), WinCoreManager.Services.Uninstaller.ResidueFileGuard.ParseAppExecutionAlias(bytes));
        Assert.Null(WinCoreManager.Services.Uninstaller.ResidueFileGuard.ParseAppExecutionAlias(bytes.AsSpan(0, bytes.Length - 2)));
        bytes[8] = 4;
        Assert.Null(WinCoreManager.Services.Uninstaller.ResidueFileGuard.ParseAppExecutionAlias(bytes));
    }

    [Theory]
    [InlineData("", true, false)]
    [InlineData("private", true, false)]
    [InlineData("winget", false, false)]
    [InlineData("winget", true, true)]
    public async Task Audit12H09_upgrade_only_launches_with_verified_source(string source, bool trusted, bool expected)
    {
        var calls = new List<string[]>();
        var service = new WinGetService(() => @"C:\Fixture\winget.exe", (start, _, _, _, _) =>
        {
            var arguments = start.ArgumentList.ToArray();
            calls.Add(arguments);
            var export = trusted ? OfficialWinget : OfficialWinget.Replace("cdn.winget.microsoft.com", "other.invalid");
            return Task.FromResult(new ProcessRunResult { Output = arguments[0] == "source" ? [export] : [] });
        }, () => false);
        var result = await service.UpgradePackageAsync(new()
        {
            Id = "Fixture.App", Name = "Fixture", Source = source, AvailableVersion = "2.0"
        }, null, default);
        Assert.Equal(expected, !result.StartFailed);
        Assert.Equal(expected, calls.Any(call => call[0] == "upgrade"));
        if (expected) Assert.Equal(new[] { "--source", "winget" }, calls.Last()[^2..]);
    }

    [Fact]
    public void Audit12H09_rejects_ambiguous_source_exports_and_wrong_catalog_rows()
    {
        Assert.True(WinGetTrust.IsTrustedExport("winget", OfficialWinget));
        Assert.True(WinGetTrust.IsTrustedExport("msstore", """
            {"Name":"msstore","Arg":"https://storeedgefd.dsx.mp.microsoft.com/v9.0","Type":"Microsoft.Rest","Identifier":"StoreEdgeFD","TrustLevel":["Trusted"]}
            """));
        Assert.False(WinGetTrust.IsTrustedExport("winget", OfficialWinget.Replace("\"Name\":\"winget\"", "\"Name\":\"other\",\"Name\":\"winget\"")));
        Assert.False(WinGetTrust.IsTrustedExport("winget", OfficialWinget.Replace("\"Data\":\"Microsoft.Winget.Source_8wekyb3d8bbwe\"", "\"Data\":\"Other.Source\"")));
        Assert.True(WinGetTrust.IsTrustedExport("winget", OfficialWinget.Replace("\"Data\":\"Microsoft.Winget.Source_8wekyb3d8bbwe\"", """
            "Data":"{\"fullIdentity\":\"Microsoft.Winget.Source_8wekyb3d8bbwe\",\"deltaIdentity\":\"Microsoft.Winget.Source.Delta_8wekyb3d8bbwe\",\"baselineIdentity\":\"Microsoft.Winget.Source.Baseline_8wekyb3d8bbwe\"}"
            """)));
        const string header = "Name         Id           Version   Available";
        const string row = "Fixture      Fixture.App  1.0       2.0";
        Assert.Equal("winget", Assert.Single(WinGetService.ParseScopedUpdates(header + "\n----------------------------------------------------\n" + row, "winget", out _)).Source);
        Assert.Empty(WinGetService.ParseScopedUpdates(header + "    Source\n----------------------------------------------------\n" + row + "         private", "winget", out _));
    }

    [Fact]
    public void Upgrade_limita_id_origem_e_versão_sem_argumento_de_reinício()
    {
        var package = new WinGetPackage
        {
            Name = "Exemplo",
            Id = "Example.App",
            Source = "winget",
            InstalledVersion = "1.0",
            AvailableVersion = "2.0",
        };

        var start = WinGetService.BuildUpgradeStartInfo("winget.exe", package);

        Assert.NotNull(start);
        Assert.Empty(start.Arguments);
        Assert.Equal(new[] { "upgrade", "--id", "Example.App", "--exact", "--version", "2.0",
            "--disable-interactivity", "--accept-source-agreements", "--accept-package-agreements", "--source", "winget" }, start.ArgumentList);
    }

    [Fact]
    public void Upgrade_rejeita_campos_truncados()
    {
        var package = new WinGetPackage { Name = "Exemplo", Id = "Example.App…", AvailableVersion = "2.0" };

        Assert.Null(WinGetService.BuildUpgradeStartInfo("winget.exe", package));
    }

    [Fact]
    public void Falha_de_inicio_inclui_codigo_nativo_e_dica_de_acesso_negado()
    {
        var result = new ProcessRunResult { StartFailed = true, StartError = "Access is denied.", StartErrorCode = 5 };

        var message = WinGetService.DescribeStartFailure(result, @"C:\alias\winget.exe");

        Assert.Contains("código nativo 5 / 0x5", message, StringComparison.Ordinal);
        Assert.Contains(@"C:\alias\winget.exe", message, StringComparison.Ordinal);
        Assert.Contains("administrador", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Falha_de_inicio_sem_codigo_nativo_nao_inclui_dica()
    {
        var result = new ProcessRunResult { StartFailed = true, StartError = "arquivo não encontrado" };

        var message = WinGetService.DescribeStartFailure(result, @"C:\x\winget.exe");

        Assert.DoesNotContain("código nativo", message, StringComparison.Ordinal);
        Assert.DoesNotContain("administrador", message, StringComparison.Ordinal);
    }

    [Fact]
    public void Resolver_nao_executa_arquivo_comum_no_caminho_do_alias()
    {
        var local = Path.Combine(Path.GetTempPath(), "WinCore-WinGet-" + Guid.NewGuid().ToString("N"));
        var directory = Directory.CreateDirectory(Path.Combine(local, "Microsoft", "WindowsApps")).FullName;
        var executable = Path.Combine(directory, "winget.exe");
        try
        {
            File.WriteAllText(executable, "fixture");
            Assert.Null(WinGetService.ResolveExecutablePath(local));
        }
        finally
        {
            File.Delete(executable);
            Directory.Delete(directory);
            Directory.Delete(Path.GetDirectoryName(directory)!);
            Directory.Delete(local);
        }
    }
}
