using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Tests;

public class UninstallPlannerTests
{
    private readonly UninstallPlanner _planner = new();

    private static InstalledProgram CreateProgram(
        ProgramKind kind = ProgramKind.Executable,
        string uninstallString = "",
        Guid bundleProviderKey = default,
        string installLocation = "",
        bool isProtected = false,
        bool isSystemComponent = false,
        bool isUpdate = false)
    {
        return new InstalledProgram
        {
            Name = "Teste",
            RegistryKeyName = "Teste",
            RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Teste",
            Origin = "Registro (64 bits)",
            Kind = kind,
            UninstallString = uninstallString,
            MsiProductCode = bundleProviderKey,
            InstallLocation = installLocation,
            IsProtected = isProtected,
            IsSystemComponent = isSystemComponent,
            IsUpdate = isUpdate,
        };
    }

    [Fact]
    public void PlanoMsiUsaMsiexecDoSistemaESuprimeReinicio()
    {
        var guid = Guid.NewGuid();
        var program = CreateProgram(ProgramKind.Msi, bundleProviderKey: guid);

        var plan = _planner.CreatePlan(program, silentPreferred: false);

        Assert.True(plan.IsExecutable);
        Assert.Equal(UninstallMethod.Msi, plan.Method);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), plan.ExecutablePath);
        Assert.Equal($"/x{guid:B} REBOOT=ReallySuppress /norestart", plan.Arguments);
    }

    [Fact]
    public void MsiSemGuidSemDesinstaladorENaoSuportado()
    {
        var program = CreateProgram(ProgramKind.Msi, bundleProviderKey: Guid.Empty);

        var plan = _planner.CreatePlan(program, silentPreferred: false);

        Assert.False(plan.IsExecutable);
        Assert.Equal(UninstallMethod.NotSupported, plan.Method);
        Assert.False(string.IsNullOrWhiteSpace(plan.NotExecutableReason));
    }

    [Fact]
    public void PlanoExeDivideComandoCitado()
    {
        var exe = Path.Combine(Path.GetTempPath(), "WinCoreManagerTests", $"un_{Guid.NewGuid():N}.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
        File.WriteAllText(exe, "fake");

        try
        {
            var program = CreateProgram(ProgramKind.Executable, uninstallString: $@"""{exe}"" /quiet");

            var plan = _planner.CreatePlan(program, silentPreferred: false);

            Assert.True(plan.IsExecutable);
            Assert.Equal(exe, plan.ExecutablePath);
            Assert.Equal("/quiet", plan.Arguments);
        }
        finally
        {
            File.Delete(exe);
        }
    }

    [Fact]
    public void MsiSemCodigoNaoUsaComandoRegistrado()
    {
        var program = CreateProgram(ProgramKind.Msi, uninstallString: "msiexec.exe /x setup.msi");
        Assert.False(_planner.CreatePlan(program, silentPreferred: false).IsExecutable);
    }

    [Theory]
    [InlineData(@"\\fixture.invalid\share\uninstall.exe")]
    [InlineData(@"\\?\C:\fixture\uninstall.exe")]
    [InlineData(@"\fixture\uninstall.exe")]
    [InlineData(@"Z:uninstall.exe")]
    [InlineData("pwsh.exe")]
    [InlineData("uninstall.cmd")]
    public void ComandoComOrigemOuTipoInvalidoENegado(string command)
    {
        var program = CreateProgram(uninstallString: command, installLocation: Path.GetTempPath());
        Assert.False(_planner.CreatePlan(program, silentPreferred: false).IsExecutable);
    }

    [Fact]
    public void ExecutavelRelativoForaDaInstalacaoERecusadoMesmoSeExistir()
    {
        var root = Path.Combine(Path.GetTempPath(), "WinCoreManagerTests", Guid.NewGuid().ToString("N"));
        var installDirectory = Path.Combine(root, "installation");
        var executable = Path.Combine(root, "uninstall.exe");
        Directory.CreateDirectory(installDirectory);
        File.WriteAllText(executable, "fake");
        try
        {
            var program = CreateProgram(uninstallString: @"..\uninstall.exe", installLocation: installDirectory);
            Assert.False(_planner.CreatePlan(program, silentPreferred: false).IsExecutable);
        }
        finally
        {
            File.Delete(executable);
            Directory.Delete(installDirectory);
            Directory.Delete(root);
        }
    }

    [Fact]
    public void PlanoAceitaCaminhoCitadoSemExtensaoQuandoExeLocalExiste()
    {
        var executable = Path.Combine(Path.GetTempPath(), $"uninstall fixture {Guid.NewGuid():N}.exe");
        File.WriteAllText(executable, "fixture");
        try
        {
            var plan = _planner.CreatePlan(CreateProgram(uninstallString: $"\"{Path.ChangeExtension(executable, null)}\" --quiet"), false);
            Assert.Equal(executable, plan.ExecutablePath);
            Assert.Equal("--quiet", plan.Arguments);
        }
        finally
        {
            File.Delete(executable);
        }
    }

    [Fact]
    public void CadeiaViaCmdERecusadaPorSeguranca()
    {
        var program = CreateProgram(ProgramKind.Executable, uninstallString: @"cmd /c ""C:\malicioso.bat"" && rd /s /q C:\dados");

        var plan = _planner.CreatePlan(program, silentPreferred: false);

        Assert.False(plan.IsExecutable);
        Assert.Equal(UninstallMethod.NotSupported, plan.Method);
        Assert.Contains(plan.Warnings, w => w.Contains("cmd", StringComparison.OrdinalIgnoreCase) || w.Contains("shell", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExecutavelInexistenteEmCaminhoAbsolutoENaoExecutavel()
    {
        var program = CreateProgram(ProgramKind.Executable, uninstallString: @"C:\diretorio\que\nao\existe\un.exe");

        var plan = _planner.CreatePlan(program, silentPreferred: false);

        Assert.False(plan.IsExecutable);
        Assert.Contains(plan.Warnings, w => w.Contains("não foi encontrado", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ExecutavelRelativoEResolvidoPeloDiretorioDeInstalacao()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinCoreManagerTests", $"dir_{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var exe = Path.Combine(directory, "unins000.exe");
        File.WriteAllText(exe, "fake");

        try
        {
            var program = CreateProgram(ProgramKind.Executable, uninstallString: "unins000.exe", installLocation: directory);

            var plan = _planner.CreatePlan(program, silentPreferred: false);

            Assert.True(plan.IsExecutable);
            Assert.Equal(exe, plan.ExecutablePath);
        }
        finally
        {
            File.Delete(exe);
            Directory.Delete(directory);
        }
    }

    [Fact]
    public void TiposNaoSuportadosNaoGeramPlanoExecutavel()
    {
        foreach (var kind in new[] { ProgramKind.PowerShell, ProgramKind.Steam, ProgramKind.StoreApp, ProgramKind.WindowsFeature, ProgramKind.WindowsUpdate, ProgramKind.SdbInst })
        {
            var program = CreateProgram(kind, uninstallString: @"C:\qualquer\un.exe");

            var plan = _planner.CreatePlan(program, silentPreferred: false);

            Assert.False(plan.IsExecutable);
            Assert.False(string.IsNullOrWhiteSpace(plan.NotExecutableReason));
        }
    }

    [Fact]
    public void AvisosDeProtecaoSistemaEAtualizacaoAparecem()
    {
        var program = CreateProgram(
            ProgramKind.Msi,
            bundleProviderKey: Guid.NewGuid(),
            isProtected: true,
            isSystemComponent: true,
            isUpdate: true);

        var plan = _planner.CreatePlan(program, silentPreferred: false);

        Assert.Contains(plan.Warnings, w => w.Contains("NoRemove"));
        Assert.Contains(plan.Warnings, w => w.Contains("componente de sistema"));
        Assert.Contains(plan.Warnings, w => w.Contains("atualização/hotfix"));
    }
}
