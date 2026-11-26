using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;
using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;
using WinCoreManager.ViewModels;

namespace WinCoreManager.Tests;

/// <summary>Regressões da auditoria de segurança. Só usa diretórios temporários e Registro falso.</summary>
public class ResidueHardeningTests : IDisposable
{
    private const string OwnKey = @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ExemploApp";
    private const string AppKey = @"SOFTWARE\Exemplo App";
    private const string AppKeyFull = @"HKEY_CURRENT_USER\SOFTWARE\Exemplo App";

    private readonly ResidueTestEnvironment _env = new("hard");
    private readonly FakeResidueRegistryReader _reader = new();
    private readonly FakeResidueRegistryWriter _writer = new();
    private readonly FakeHistoryService _history = new();
    private readonly string _quarantineRoot;
    private readonly ResidueQuarantineService _quarantine;
    private readonly ResidueDetectionEngine _engine;
    private readonly ResidueCleanupExecutor _executor;

    public ResidueHardeningTests()
    {
        _quarantineRoot = Path.Combine(_env.Root, "quarantine");
        _quarantine = new ResidueQuarantineService(_quarantineRoot, _reader, _writer, _env.Rules);
        _engine = new ResidueDetectionEngine(new FakeRegistryView(), _reader, _env.Rules);
        _executor = new ResidueCleanupExecutor(_quarantine, _reader, _writer, _history);
    }

    public void Dispose() => _env.Dispose();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr security);

    private static InstalledProgram Program(string location = "", string name = "Exemplo App", string registryPath = OwnKey) => new()
    {
        Name = name,
        RegistryKeyName = "ExemploApp",
        RegistryPath = registryPath,
        Is64Bit = true,
        InstallLocation = location,
    };

    private static InstalledProgram Other(string name, string location = "", string publisher = "") => new()
    {
        Name = name,
        Publisher = publisher,
        RegistryKeyName = name.Replace(" ", string.Empty),
        RegistryPath = $@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{name.Replace(" ", string.Empty)}",
        InstallLocation = location,
    };

    private static void CreateJunction(string link, string target)
    {
        var info = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(info)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        Assert.True(new DirectoryInfo(link).Attributes.HasFlag(FileAttributes.ReparsePoint), "Pré-condição: junction criada.");
    }

    private static ResidueItem Approved(string path, ResidueCategory category) => new()
    {
        Id = Guid.NewGuid(),
        Category = category,
        Location = path,
        Confidence = ResidueConfidence.High,
        Fingerprint = ResidueFileGuard.Inspect(path, 1000, CancellationToken.None)!.Fingerprint,
    };

    private ResidueItem ApprovedKey(string subKey = AppKey, string full = AppKeyFull, bool is64 = true) => new()
    {
        Id = Guid.NewGuid(),
        Category = ResidueCategory.RegistryKey,
        Location = full,
        Is64BitView = is64,
        Confidence = ResidueConfidence.High,
        Fingerprint = RegistrySnapshot.ComputeHash(RegistrySnapshot.Capture(_reader, ResidueRegistryHive.CurrentUser, subKey, is64)),
    };

    private static ResidueCleanupPlan Plan(params ResidueItem[] items) => new()
    {
        OperationId = Guid.NewGuid(),
        Program = Program(),
        Approved = items,
    };

    private string NewFile(string name = "Exemplo App.log", string content = "dados")
    {
        var path = Path.Combine(_env.ProgramData, name);
        File.WriteAllText(path, content);
        return path;
    }

    private QuarantineEntry ExportAndRemove()
    {
        var entry = _quarantine.ExportRegistryKey(ResidueRegistryHive.CurrentUser, AppKey, true, AppKeyFull, "Exemplo App", CancellationToken.None);
        _reader.Remove(ResidueRegistryHive.CurrentUser, AppKey);
        return entry;
    }

    // ---------------------------------------------------------------- Caminhos

    [Theory]
    [InlineData(@"C:\Temp\arquivo.txt:fluxo")]
    [InlineData(@"C:\Temp\NUL")]
    [InlineData(@"C:\Temp\con.txt")]
    [InlineData(@"C:\Temp\COM1.log")]
    [InlineData(@"C:\Temp\LPT1\x")]
    [InlineData(@"\\?\C:\Temp\x")]
    [InlineData(@"\\.\C:\Temp\x")]
    [InlineData(@"\\servidor\share\x")]
    [InlineData(@"C:Temp\x")]
    public void CaminhosDeDispositivoFluxoOuAmbiguosSaoRecusados(string path) =>
        Assert.False(ResiduePathRules.TryNormalize(path, out _));

    private static ResiduePathRules SystemRules() => new()
    {
        WindowsDirectory = @"C:\Windows",
        ProgramFiles = @"C:\Program Files",
        ProgramFilesX86 = @"C:\Program Files (x86)",
        ProgramData = @"C:\ProgramData",
        UserProfile = @"C:\Users\atual",
        AppDataRoaming = @"C:\Users\atual\AppData\Roaming",
        AppDataLocal = @"C:\Users\atual\AppData\Local",
        AppBaseDirectory = @"D:\Apps\WinCore Manager",
        AdditionalProtectedDirectories = [@"C:\Users\atual\AppData\Local\WinCoreManager"],
    };

    [Theory]
    [InlineData(@"C:\Program Files\Common Files\Foo")]
    [InlineData(@"C:\Program Files\WindowsApps\Foo")]
    [InlineData(@"C:\Program Files (x86)\Microsoft\Edge")]
    [InlineData(@"C:\ProgramData\Microsoft\Foo")]
    [InlineData(@"C:\ProgramData\Package Cache\{guid}")]
    [InlineData(@"C:\Users\atual\AppData\Local\Microsoft\Foo")]
    [InlineData(@"C:\Users\atual\AppData\Local\Temp\Foo")]
    [InlineData(@"C:\Users\atual\AppData\Local\Programs")]
    [InlineData(@"C:\Users\atual\AppData\Local\WinCoreManager\Quarantine")]
    [InlineData(@"C:\$Recycle.Bin\S-1\x")]
    [InlineData(@"D:\System Volume Information\x")]
    [InlineData(@"C:\Users")]
    [InlineData(@"C:\Users\atual\AppData")]
    [InlineData(@"D:\Apps")]
    public void PastasCompartilhadasEAncestraisSaoProtegidas(string path) =>
        Assert.NotNull(SystemRules().DescribeFileSystemProtection(path, isReparsePoint: false));

    [Fact]
    public void ProgramaPorUsuarioContinuaAvaliavel() =>
        Assert.Null(SystemRules().DescribeFileSystemProtection(@"C:\Users\atual\AppData\Local\Programs\Foo", isReparsePoint: false));

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Microsoft\Windows")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Classes\Foo")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Policies\Foo")]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Windows\Foo")]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\\Microsoft")]
    [InlineData(@"HKEY_CURRENT_USER\SOFTWARE\..\SYSTEM")]
    public void ChavesCriticasOuMalformadasSaoProtegidas(string path) =>
        Assert.NotNull(SystemRules().DescribeRegistryProtection(path, []));

    [Theory]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App", true)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\App", true)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", false)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\App\Sub", false)]
    [InlineData(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft", false)]
    [InlineData("", false)]
    public void SomenteEntradaDiretaDeDesinstalacaoEhReconhecida(string path, bool expected) =>
        Assert.Equal(expected, ResiduePathRules.IsUninstallEntryPath(path));

    // ---------------------------------------------------------------- Classificação

    [Fact]
    public void LocalDigitadoManualmenteNaoComprovaPropriedade()
    {
        var directory = Path.Combine(_env.Root, "Dados", "Exemplo App");
        Directory.CreateDirectory(directory);

        var result = _engine.Detect(Program(directory, registryPath: string.Empty), [], CancellationToken.None);

        var item = Assert.Single(result.Items, i => i.Location == directory);
        Assert.Equal(ResidueConfidence.Medium, item.Confidence);
    }

    [Fact]
    public void DiretorioDoDesinstaladorNaoComprovaPropriedade()
    {
        var shared = Path.Combine(_env.ProgramFiles, "Ferramentas Comuns");
        Directory.CreateDirectory(shared);
        var uninstaller = Path.Combine(shared, "setup.exe");
        File.WriteAllText(uninstaller, "x");
        var program = new InstalledProgram
        {
            Name = "Exemplo App",
            RegistryKeyName = "ExemploApp",
            RegistryPath = OwnKey,
            UninstallString = $"\"{uninstaller}\" /remove",
        };

        var result = _engine.Detect(program, [], CancellationToken.None);

        Assert.DoesNotContain(result.Items, i => i.Location == shared);
    }

    [Fact]
    public void ExecutavelEmSubpastaImpedeAltaConfianca()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(Path.Combine(directory, "bin"));
        File.WriteAllText(Path.Combine(directory, "bin", "app.dll"), "x");

        var item = Assert.Single(_engine.Detect(Program(directory), [], CancellationToken.None).Items, i => i.Location == directory);

        Assert.Equal(ResidueConfidence.Medium, item.Confidence);
    }

    [Fact]
    public void JunctionInternaImpedeAltaConfianca()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        CreateJunction(Path.Combine(directory, "link"), _env.WindowsDir);

        var item = Assert.Single(_engine.Detect(Program(directory), [], CancellationToken.None).Items, i => i.Location == directory);

        Assert.Equal(ResidueConfidence.Medium, item.Confidence);
    }

    [Fact]
    public void HardLinkInternoImpedeAltaConfianca()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var external = NewFile("externo.dat");
        Assert.True(CreateHardLinkW(Path.Combine(directory, "dados.dat"), external, IntPtr.Zero));

        var item = Assert.Single(_engine.Detect(Program(directory), [], CancellationToken.None).Items, i => i.Location == directory);

        Assert.Equal(ResidueConfidence.Medium, item.Confidence);
    }

    [Fact]
    public void AncestralComJunctionEhProtegido()
    {
        var real = Path.Combine(_env.Root, "real");
        Directory.CreateDirectory(Path.Combine(real, "Exemplo App"));
        var link = Path.Combine(_env.ProgramFiles, "Vinculo");
        CreateJunction(link, real);
        var viaLink = Path.Combine(link, "Exemplo App");

        var item = Assert.Single(_engine.Detect(Program(viaLink), [], CancellationToken.None).Items, i => i.Location == viaLink);

        Assert.Equal(ResidueConfidence.Protected, item.Confidence);
    }

    [Fact]
    public void PastaDeFabricanteDeOutroProgramaNaoEhElegivel()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Acme Tools");
        Directory.CreateDirectory(directory);

        var result = _engine.Detect(Program(directory, "Acme Tools"), [Other("Produto Diferente", publisher: "Acme Tools")], CancellationToken.None);

        Assert.Equal(ResidueConfidence.Medium, Assert.Single(result.Items, i => i.Location == directory).Confidence);
    }

    [Theory]
    [InlineData("subchave-outro")]
    [InlineData("ilegivel")]
    [InlineData("link")]
    public void ChaveCompartilhadaIlegivelOuLinkNaoEhElegivel(string scenario)
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        _reader.Add(ResidueRegistryHive.CurrentUser, AppKey, new Dictionary<string, string> { ["InstallDir"] = directory });
        var child = $@"{AppKey}\Produto Vizinho";
        _reader.Add(ResidueRegistryHive.CurrentUser, child);
        InstalledProgram[] others = [];
        switch (scenario)
        {
            case "subchave-outro":
                others = [Other("Produto Vizinho")];
                break;
            case "ilegivel":
                _reader.UnreadableKeys.Add(child);
                break;
            default:
                _reader.SymbolicLinks.Add(child);
                break;
        }

        var result = _engine.Detect(Program(directory), others, CancellationToken.None);

        Assert.NotEqual(ResidueConfidence.High, Assert.Single(result.Items, i => i.Location == AppKeyFull).Confidence);
    }

    [Fact]
    public void ChaveOrfaDeBundleNaoEquivaleAInstalacaoAtiva()
    {
        var id = Guid.NewGuid();
        var name = $"{id:B}_is1";
        var key = $@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{name}";
        var fullKey = @"HKEY_CURRENT_USER\" + key;
        var missing = Path.Combine(_env.Root, "missing");
        var program = new InstalledProgram
        {
            Name = "Exemplo App",
            RegistryKeyName = name,
            RegistryPath = fullKey,
            Is64Bit = true,
            InstallLocation = missing,
            UninstallString = $"\"{Path.Combine(missing, "unins000.exe")}\"",
            Kind = ProgramKind.InnoSetup,
            BundleProviderKey = id,
        };
        var view = new FakeRegistryView(new FakeRegistryKey(name, fullKey, true));
        _reader.Add(ResidueRegistryHive.CurrentUser, key);
        var result = new ResidueDetectionEngine(view, _reader, _env.Rules).Detect(program, [], CancellationToken.None);
        var leftover = Assert.Single(result.Items, item => item.Location == fullKey);
        Assert.Equal(ResidueConfidence.High, leftover.Confidence);
    }

    [Fact]
    public void ChaveDeDesinstalacaoDeProgramaInstaladoNaoEhElegivel()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var view = new FakeRegistryView(new FakeRegistryKey("ExemploApp", OwnKey, true));
        _reader.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ExemploApp");

        var result = new ResidueDetectionEngine(view, _reader, _env.Rules).Detect(Program(directory), [], CancellationToken.None);

        Assert.DoesNotContain(result.Items, i => i.IsEligibleForCleanup);
    }

    // ---------------------------------------------------------------- Corridas e identidade

    [Fact]
    public void HandleValidadoImpedeTrocaDoItemPorOutroProcesso()
    {
        var directory = Path.Combine(_env.ProgramData, "Exemplo App");
        Directory.CreateDirectory(directory);

        using var guarded = ResidueFileGuard.OpenForMove(directory, ResidueCategory.Folder);

        Assert.ThrowsAny<Exception>(() => Directory.Move(directory, directory + "_trocado"));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task ItemTrocadoDepoisDoPlanoNaoEhMovido()
    {
        var directory = Path.Combine(_env.ProgramData, "Exemplo App");
        Directory.CreateDirectory(directory);
        var item = Approved(directory, ResidueCategory.Folder);
        Directory.Delete(directory);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "dados-novos.txt"), "importante");

        var report = await _executor.ExecuteAsync(Plan(item));

        Assert.Equal(ResidueCleanupItemOutcome.BackupFailed, Assert.Single(report.Results).Outcome);
        Assert.True(File.Exists(Path.Combine(directory, "dados-novos.txt")));
    }

    [Fact]
    public async Task CaminhoQuePassaAApontarParaOutroDestinoEhBloqueado()
    {
        var directory = Path.Combine(_env.ProgramData, "Exemplo App");
        Directory.CreateDirectory(directory);
        var item = Approved(directory, ResidueCategory.Folder);
        Directory.Delete(directory);
        var sensitive = Path.Combine(_env.Root, "sensivel");
        Directory.CreateDirectory(sensitive);
        File.WriteAllText(Path.Combine(sensitive, "a.txt"), "x");
        CreateJunction(directory, sensitive);

        var report = await _executor.ExecuteAsync(Plan(item));

        Assert.Equal(ResidueCleanupItemOutcome.BackupFailed, Assert.Single(report.Results).Outcome);
        Assert.True(File.Exists(Path.Combine(sensitive, "a.txt")));
        Assert.Empty(_quarantine.GetEntries());
    }

    [Fact]
    public async Task ArquivoQueDesapareceDepoisDoPlanoNaoContaComoSucesso()
    {
        var file = NewFile();
        var item = Approved(file, ResidueCategory.File);
        File.Delete(file);

        var report = await _executor.ExecuteAsync(Plan(item));

        Assert.False(report.IsFullSuccess);
        Assert.Equal(0, report.SuccessCount);
    }

    [Fact]
    public void AncestralComJunctionBloqueiaMovimento()
    {
        var real = Path.Combine(_env.Root, "real");
        Directory.CreateDirectory(real);
        File.WriteAllText(Path.Combine(real, "a.log"), "x");
        var link = Path.Combine(_env.ProgramData, "Vinculo");
        CreateJunction(link, real);

        Assert.Throws<InvalidOperationException>(() =>
            _quarantine.MoveToQuarantine(Path.Combine(link, "a.log"), ResidueCategory.File, "X", CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(real, "a.log")));
    }

    [Fact]
    public void QuarentenaSobJunctionEhRecusada()
    {
        var real = Path.Combine(_env.Root, "real");
        Directory.CreateDirectory(real);
        var link = Path.Combine(_env.Root, "quarentena-link");
        CreateJunction(link, real);
        var service = new ResidueQuarantineService(link, _reader, _writer, _env.Rules);
        var file = NewFile();

        Assert.Throws<InvalidOperationException>(() =>
            service.MoveToQuarantine(file, ResidueCategory.File, "X", CancellationToken.None));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void ArquivoComHardLinkNaoEhMovido()
    {
        var file = NewFile();
        var link = Path.Combine(_env.ProgramData, "vinculo.log");
        Assert.True(CreateHardLinkW(link, file, IntPtr.Zero));

        Assert.Throws<InvalidOperationException>(() =>
            _quarantine.MoveToQuarantine(file, ResidueCategory.File, "X", CancellationToken.None));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void TipoDiferenteDoAutorizadoEhBloqueado()
    {
        var file = NewFile();

        Assert.Throws<InvalidOperationException>(() =>
            _quarantine.MoveToQuarantine(file, ResidueCategory.Folder, "X", CancellationToken.None));
        Assert.True(File.Exists(file));
    }

    [Fact]
    public void RenomeacaoNuncaSobrescreveDestino()
    {
        var source = NewFile("origem.txt", "origem");
        var target = NewFile("destino.txt", "destino");
        using (var guarded = ResidueFileGuard.OpenForMove(source, ResidueCategory.File))
        {
            Assert.Throws<InvalidOperationException>(() => ResidueFileGuard.RenameNoReplace(guarded.Handle, target));
        }

        Assert.Equal("destino", File.ReadAllText(target));
        Assert.Equal("origem", File.ReadAllText(source));
    }

    [Fact]
    public void ProcessoElevadoNaoLimpaNemRecupera()
    {
        var file = NewFile();
        _reader.Add(ResidueRegistryHive.CurrentUser, AppKey);
        var elevated = new ResidueQuarantineService(_quarantineRoot, _reader, _writer, _env.Rules, isElevatedProcess: true);

        Assert.Throws<InvalidOperationException>(() => elevated.MoveToQuarantine(file, ResidueCategory.File, "X", CancellationToken.None));
        Assert.Throws<InvalidOperationException>(() => elevated.ExportRegistryKey(ResidueRegistryHive.CurrentUser, AppKey, true, AppKeyFull, "X", CancellationToken.None));
        Assert.Equal(QuarantineRestoreStatus.Failed, elevated.Restore(Guid.NewGuid(), false, CancellationToken.None).Status);
        Assert.True(File.Exists(file));
    }

    // ---------------------------------------------------------------- Volumes reais (C: e D:)

    [Fact]
    public void QuarentenaEmOutroVolumeBloqueiaSemCopiar()
    {
        var otherVolumeRoot = Path.Combine(AppContext.BaseDirectory, $"xv_{Guid.NewGuid():N}");
        var file = NewFile();

        // shortcut: exige temporário e saída de testes em unidades diferentes; em unidade única não há o que provar.
        if (!ResidueQuarantineService.RequiresCrossVolume(file, otherVolumeRoot))
        {
            return;
        }

        var service = new ResidueQuarantineService(otherVolumeRoot, _reader, _writer, _env.Rules);
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                service.MoveToQuarantine(file, ResidueCategory.File, "X", CancellationToken.None));

            Assert.Contains("unidade diferente", error.Message);
            Assert.Equal("dados", File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(otherVolumeRoot))
            {
                Directory.Delete(otherVolumeRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void RenomeacaoPeloHandleNaoCopiaEntreVolumes()
    {
        var otherVolumeRoot = Path.Combine(AppContext.BaseDirectory, $"xr_{Guid.NewGuid():N}");
        var file = NewFile();

        // shortcut: exige temporário e saída de testes em unidades diferentes; em unidade única não há o que provar.
        if (!ResidueQuarantineService.RequiresCrossVolume(file, otherVolumeRoot))
        {
            return;
        }

        Directory.CreateDirectory(otherVolumeRoot);
        var target = Path.Combine(otherVolumeRoot, "copiado.log");
        try
        {
            using (var guarded = ResidueFileGuard.OpenForMove(file, ResidueCategory.File))
            {
                var error = Assert.Throws<InvalidOperationException>(() => ResidueFileGuard.RenameNoReplace(guarded.Handle, target));
                Assert.Contains("outro volume", error.Message);
            }

            Assert.True(File.Exists(file));
            Assert.False(File.Exists(target));
        }
        finally
        {
            Directory.Delete(otherVolumeRoot, recursive: true);
        }
    }

    // ---------------------------------------------------------------- Manifesto e interrupções

    private void SeedManifest(params QuarantineEntry[] entries)
    {
        Directory.CreateDirectory(_quarantineRoot);
        File.WriteAllText(Path.Combine(_quarantineRoot, "manifest.json"), JsonSerializer.Serialize(entries));
    }

    [Fact]
    public void QuedaDeEnergiaDepoisDoMovimentoMantemItemRecuperavel()
    {
        var original = Path.Combine(_env.ProgramData, "Exemplo App.log");
        Directory.CreateDirectory(_quarantineRoot);
        var stored = Path.Combine(_quarantineRoot, "abc_item.log");
        File.WriteAllText(stored, "dados");
        var id = Guid.NewGuid();
        SeedManifest(new QuarantineEntry { Id = id, State = QuarantineEntryState.Pending, OriginalPath = original, QuarantinedPath = stored, Category = ResidueCategory.File });

        Assert.Equal(QuarantineEntryState.Complete, Assert.Single(_quarantine.GetEntries()).State);
        Assert.Equal(QuarantineRestoreStatus.Restored, _quarantine.Restore(id, false, CancellationToken.None).Status);
        Assert.Equal("dados", File.ReadAllText(original));
    }

    [Fact]
    public void QuedaDeEnergiaAntesDoMovimentoDescartaEntradaPendente()
    {
        var original = NewFile();
        SeedManifest(new QuarantineEntry
        {
            Id = Guid.NewGuid(),
            State = QuarantineEntryState.Pending,
            OriginalPath = original,
            QuarantinedPath = Path.Combine(_quarantineRoot, "nunca_movido.log"),
            Category = ResidueCategory.File,
        });

        Assert.Empty(_quarantine.GetEntries());
        Assert.True(File.Exists(original));
    }

    [Fact]
    public void FalhaNoManifestoDepoisDoMovimentoDesfazOperacao()
    {
        var file = NewFile();
        _quarantine.AfterMoveForTest = () => throw new IOException("disco cheio");

        Assert.Throws<InvalidOperationException>(() =>
            _quarantine.MoveToQuarantine(file, ResidueCategory.File, "X", CancellationToken.None));

        _quarantine.AfterMoveForTest = null;
        Assert.Equal("dados", File.ReadAllText(file));
        Assert.Empty(_quarantine.GetEntries());
    }

    [Fact]
    public async Task FalhaSemDesfazerEhRelatadaComoIncompletaERecuperavel()
    {
        var file = NewFile();
        var item = Approved(file, ResidueCategory.File);
        _quarantine.AfterMoveForTest = () =>
        {
            File.WriteAllText(file, "recriado");
            throw new IOException("disco cheio");
        };

        var report = await _executor.ExecuteAsync(Plan(item));

        _quarantine.AfterMoveForTest = null;
        Assert.Equal(ResidueCleanupItemOutcome.Failed, Assert.Single(report.Results).Outcome);
        Assert.Equal("recriado", File.ReadAllText(file));
        var entry = Assert.Single(_quarantine.GetEntries());
        Assert.Equal("dados", File.ReadAllText(entry.QuarantinedPath));
    }

    [Fact]
    public void ManifestoCorrompidoEhPreservadoEBloqueiaRecuperacao()
    {
        Directory.CreateDirectory(_quarantineRoot);
        var manifest = Path.Combine(_quarantineRoot, "manifest.json");
        File.WriteAllText(manifest, "{ corrompido");

        Assert.Empty(_quarantine.GetEntries());
        Assert.Contains("corrompido", _quarantine.ManifestProblem);
        Assert.Equal(QuarantineRestoreStatus.Failed, _quarantine.Restore(Guid.NewGuid(), false, CancellationToken.None).Status);
        Assert.Equal("{ corrompido", File.ReadAllText(manifest));
        Assert.Single(Directory.GetFiles(_quarantineRoot, "manifest.corrupt-*.json"));
    }

    // ---------------------------------------------------------------- Recuperação

    [Fact]
    public void RecuperaMultiplosItens()
    {
        var first = NewFile("Exemplo A.log", "a");
        var folder = Path.Combine(_env.ProgramData, "Exemplo B");
        Directory.CreateDirectory(Path.Combine(folder, "sub"));
        File.WriteAllText(Path.Combine(folder, "sub", "b.txt"), "b");
        var a = _quarantine.MoveToQuarantine(first, ResidueCategory.File, "X", CancellationToken.None);
        var b = _quarantine.MoveToQuarantine(folder, ResidueCategory.Folder, "X", CancellationToken.None);

        var restoredA = _quarantine.Restore(a.Id, false, CancellationToken.None);
        var restoredB = _quarantine.Restore(b.Id, false, CancellationToken.None);
        Assert.True(restoredA.Status == QuarantineRestoreStatus.Restored, restoredA.Message);
        Assert.True(restoredB.Status == QuarantineRestoreStatus.Restored, restoredB.Message);

        Assert.Equal("a", File.ReadAllText(first));
        Assert.Equal("b", File.ReadAllText(Path.Combine(folder, "sub", "b.txt")));
        Assert.Empty(_quarantine.GetEntries());
    }

    [Fact]
    public void ManifestoComOrigemForaDaQuarentenaNaoERecuperado()
    {
        var outside = NewFile("fora-da-quarentena.txt", "payload");
        var destination = Path.Combine(_env.ProgramData, "Destino Permitido.txt");
        var id = Guid.NewGuid();
        SeedManifest(new QuarantineEntry { Id = id, OriginalPath = destination, QuarantinedPath = outside, Category = ResidueCategory.File });

        var result = _quarantine.Restore(id, false, CancellationToken.None);

        Assert.Equal(QuarantineRestoreStatus.Failed, result.Status);
        Assert.False(File.Exists(destination));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public void ArquivoModificadoNaQuarentenaNaoERecuperado()
    {
        var file = NewFile();
        var entry = _quarantine.MoveToQuarantine(file, ResidueCategory.File, "X", CancellationToken.None);
        File.AppendAllText(entry.QuarantinedPath, " adulterado");

        var result = _quarantine.Restore(entry.Id, false, CancellationToken.None);

        Assert.Equal(QuarantineRestoreStatus.Failed, result.Status);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(entry.QuarantinedPath));
    }

    [Fact]
    public void ItemAusenteNaQuarentenaEhInformado()
    {
        var file = NewFile();
        var entry = _quarantine.MoveToQuarantine(file, ResidueCategory.File, "X", CancellationToken.None);
        File.Delete(entry.QuarantinedPath);

        Assert.Equal(QuarantineRestoreStatus.SourceMissing, _quarantine.Restore(entry.Id, false, CancellationToken.None).Status);
    }

    [Fact]
    public void SubstituicaoConfirmadaGuardaItemAtualNaQuarentena()
    {
        var file = NewFile(content: "antigo");
        var entry = _quarantine.MoveToQuarantine(file, ResidueCategory.File, "X", CancellationToken.None);
        File.WriteAllText(file, "atual");

        var result = _quarantine.Restore(entry.Id, overwriteConfirmed: true, CancellationToken.None);

        Assert.Equal(QuarantineRestoreStatus.Restored, result.Status);
        Assert.Equal("antigo", File.ReadAllText(file));
        Assert.Equal("atual", File.ReadAllText(Assert.Single(_quarantine.GetEntries()).QuarantinedPath));
    }

    [Fact]
    public void PastaOcupadaNuncaEhApagadaNaRecuperacao()
    {
        var folder = Path.Combine(_env.ProgramData, "Exemplo App");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "antigo.txt"), "antigo");
        var entry = _quarantine.MoveToQuarantine(folder, ResidueCategory.Folder, "X", CancellationToken.None);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "atual.txt"), "atual");

        Assert.Equal(QuarantineRestoreStatus.Restored, _quarantine.Restore(entry.Id, true, CancellationToken.None).Status);

        Assert.True(File.Exists(Path.Combine(folder, "antigo.txt")));
        var displaced = Assert.Single(_quarantine.GetEntries());
        Assert.True(File.Exists(Path.Combine(displaced.QuarantinedPath, "atual.txt")));
    }

    [Fact]
    public void RecuperacaoNaoAtravessaJunctionNoDestino()
    {
        var parent = Path.Combine(_env.ProgramData, "Pasta");
        Directory.CreateDirectory(parent);
        var file = Path.Combine(parent, "a.log");
        File.WriteAllText(file, "dados");
        var entry = _quarantine.MoveToQuarantine(file, ResidueCategory.File, "X", CancellationToken.None);
        Directory.Delete(parent);
        var redirected = Path.Combine(_env.Root, "redirecionado");
        Directory.CreateDirectory(redirected);
        CreateJunction(parent, redirected);

        var result = _quarantine.Restore(entry.Id, false, CancellationToken.None);

        Assert.Equal(QuarantineRestoreStatus.Failed, result.Status);
        Assert.False(File.Exists(Path.Combine(redirected, "a.log")));
        Assert.True(File.Exists(entry.QuarantinedPath));
    }

    // ---------------------------------------------------------------- Backup do Registro

    [Fact]
    public void BackupPreservaTiposDadosEUnicode()
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, AppKey, new Dictionary<string, object>
        {
            ["Expandido"] = @"%SystemRoot%\app",
            ["Unicode"] = "Ação 日本語 \"aspas\" \\barra",
            ["Multi"] = new[] { "um", "dois" },
            ["Bin"] = new byte[] { 0, 255 },
            ["Vazio"] = Array.Empty<byte>(),
            ["Neg"] = -1,
            ["Grande"] = long.MinValue,
            ["Linhas"] = "a\r\nb",
        });
        _reader.Kinds[$"{AppKey}|Expandido"] = RegistryValueKind.ExpandString;
        _reader.Kinds[$"{AppKey}|Vazio"] = RegistryValueKind.None;

        var entry = ExportAndRemove();
        var reg = File.ReadAllText(entry.RegistryBackupFile!);

        Assert.Contains("\"Expandido\"=hex(2):25,00,53,00", reg);
        Assert.Contains("\"Unicode\"=\"Ação 日本語 \\\"aspas\\\" \\\\barra\"", reg);
        Assert.Contains("\"Multi\"=hex(7):75,00,6d,00,00,00,64,00", reg);
        Assert.Contains("\"Bin\"=hex:00,ff", reg);
        Assert.Contains("\"Vazio\"=hex(0):", reg);
        Assert.Contains("\"Neg\"=dword:ffffffff", reg);
        Assert.Contains("\"Grande\"=hex(b):00,00,00,00,00,00,00,80", reg);
        Assert.Contains("\"Linhas\"=hex(1):61,00,0d,00,0a,00,62,00,00,00", reg);

        Assert.Equal(QuarantineRestoreStatus.Restored, _quarantine.Restore(entry.Id, false, CancellationToken.None).Status);
        Assert.Equal(RegistryValueKind.ExpandString, _writer.Written[$"{AppKey}|Expandido"].Kind);
        Assert.Equal(@"%SystemRoot%\app", _writer.Written[$"{AppKey}|Expandido"].Data);
        Assert.Equal(new byte[] { 0, 255 }, _writer.Written[$"{AppKey}|Bin"].Data);
        Assert.Equal(new[] { "um", "dois" }, _writer.Written[$"{AppKey}|Multi"].Data);
        Assert.Equal(-1, _writer.Written[$"{AppKey}|Neg"].Data);
        Assert.Equal(long.MinValue, _writer.Written[$"{AppKey}|Grande"].Data);
        Assert.Equal(RegistryValueKind.None, _writer.Written[$"{AppKey}|Vazio"].Kind);
    }

    [Fact]
    public void ExportacaoDaVisao32BitsUsaCaminhoFisico()
    {
        _reader.Add(ResidueRegistryHive.LocalMachine, AppKey);

        var entry = _quarantine.ExportRegistryKey(ResidueRegistryHive.LocalMachine, AppKey, is64BitView: false,
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Exemplo App", "X", CancellationToken.None);

        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\Wow6432Node\Exemplo App]", File.ReadAllText(entry.RegistryBackupFile!));
    }

    [Fact]
    public void ArvoreProfundaDemaisFalhaOBackup()
    {
        var path = AppKey;
        for (var i = 0; i < 40; i++)
        {
            path += $@"\n{i}";
        }

        _reader.Add(ResidueRegistryHive.CurrentUser, path);

        Assert.Throws<InvalidOperationException>(() =>
            _quarantine.ExportRegistryKey(ResidueRegistryHive.CurrentUser, AppKey, true, AppKeyFull, "X", CancellationToken.None));
        Assert.Empty(_quarantine.GetEntries());
    }

    [Fact]
    public void SubchaveIlegivelFalhaOBackupSemDeixarArquivos()
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, $@"{AppKey}\Filho");
        _reader.UnreadableKeys.Add($@"{AppKey}\Filho");

        Assert.Throws<InvalidOperationException>(() =>
            _quarantine.ExportRegistryKey(ResidueRegistryHive.CurrentUser, AppKey, true, AppKeyFull, "X", CancellationToken.None));
        Assert.False(Directory.Exists(_quarantineRoot));
    }

    [Fact]
    public void ExportacaoRecusaChaveCritica()
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Foo");

        Assert.Throws<InvalidOperationException>(() => _quarantine.ExportRegistryKey(ResidueRegistryHive.CurrentUser,
            @"SOFTWARE\Microsoft\Foo", true, @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Foo", "X", CancellationToken.None));
    }

    [Fact]
    public void RecuperacaoComChaveExistenteNaoAlteraNada()
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, AppKey, new Dictionary<string, object> { ["Nome"] = "antigo" });
        var entry = _quarantine.ExportRegistryKey(ResidueRegistryHive.CurrentUser, AppKey, true, AppKeyFull, "X", CancellationToken.None);

        var result = _quarantine.Restore(entry.Id, overwriteConfirmed: true, CancellationToken.None);

        Assert.Equal(QuarantineRestoreStatus.ConflictDestinationExists, result.Status);
        Assert.Empty(_writer.Operations);
    }

    [Fact]
    public void SnapshotAdulteradoNaoERecuperado()
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, AppKey, new Dictionary<string, object> { ["Nome"] = "valor" });
        var entry = ExportAndRemove();
        File.WriteAllText(entry.QuarantinedPath, File.ReadAllText(entry.QuarantinedPath).Replace("valor", "outro"));

        Assert.Equal(QuarantineRestoreStatus.Failed, _quarantine.Restore(entry.Id, false, CancellationToken.None).Status);
        Assert.Empty(_writer.Operations);
    }

    [Fact]
    public void RecuperacaoInformaLimitesDeAcl()
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, AppKey, new Dictionary<string, object> { ["Nome"] = "valor" });
        var entry = ExportAndRemove();

        Assert.Contains("ACL", _quarantine.Restore(entry.Id, false, CancellationToken.None).Message);
    }

    [Fact]
    public void ProvedorNativoMantemEscritasDeResiduosBloqueadas()
    {
        using var reader = new WindowsResidueRegistry();
        IResidueRegistryWriter writer = reader;
        Assert.False(writer.SupportsMutation);
        Assert.False(writer.CreateKey(ResidueRegistryHive.CurrentUser, string.Empty, true, out var reason));
        Assert.False(string.IsNullOrWhiteSpace(reason));
        Assert.False(writer.DeleteEmptySubKey(ResidueRegistryHive.CurrentUser, string.Empty, string.Empty, true, out _));
        Assert.False(writer.SetValue(ResidueRegistryHive.CurrentUser, string.Empty, string.Empty, RegistryValueKind.String, "", true, out _));
    }

    [Fact]
    public void RaizPortatilCanonicaAceitaJuncaoLegitimaSemLiberarTrocaPelaJuncao()
    {
        var physical = Directory.CreateDirectory(Path.Combine(_env.Root, "real-portable")).FullName;
        var parentAlias = Path.Combine(_env.Root, "alias-portable");
        CreateJunction(parentAlias, physical);
        var package = Directory.CreateDirectory(Path.Combine(physical, "package")).FullName;
        Assert.Equal(package, ResidueFileGuard.ResolveLocalDirectory(Path.Combine(parentAlias, "package")));
        Assert.False(WinCoreManager.Services.SelfUpdate.PortableUpdateCoordinator.RequiresRecovery(package, includeRootAncestors: false));
        Assert.Throws<WinCoreManager.Services.SelfUpdate.UpdateException>(() =>
            WinCoreManager.Services.SelfUpdate.PortablePackage.ResolveRoot(Path.Combine(parentAlias, "package")));
    }

    [Fact]
    public void LeitorRealIdentificaLinkETiposSemAlterarORegistro()
    {
        // Somente leitura: CurrentControlSet é um link nativo do Windows.
        using var registry = new WindowsResidueRegistry();

        Assert.True(registry.ReadKeyStrict(ResidueRegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet", true).IsSymbolicLink);
        Assert.False(registry.ReadKeyStrict(ResidueRegistryHive.LocalMachine, @"SYSTEM\Select", true).IsSymbolicLink);

        var environment = registry.ReadKeyStrict(ResidueRegistryHive.LocalMachine,
            @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment", true);
        var windir = Assert.Single(environment.Values, value => value.Name.Equals("windir", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RegistryValueKind.ExpandString, windir.Kind);
        var native = Assert.IsType<NativeRegistryValue>(windir.Data);
        Assert.Contains("%", System.Text.Encoding.Unicode.GetString(native.Bytes));
    }

    [Theory]
    [InlineData("Nome[")]
    [InlineData("Nome]")]
    [InlineData("Nome\rFilho")]
    public void BackupRecusaSubchaveComNomeInvalidoParaReg(string child)
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, $@"{AppKey}\{child}");

        Assert.Throws<InvalidOperationException>(() =>
            _quarantine.ExportRegistryKey(ResidueRegistryHive.CurrentUser, AppKey, true, AppKeyFull, "X", CancellationToken.None));
        Assert.Empty(_quarantine.GetEntries());
    }

    [Fact]
    public void SnapshotPreservaBytesDeMultiStringComEntradaVazia()
    {
        var bytes = System.Text.Encoding.Unicode.GetBytes("a\0\0b\0\0");
        var value = RegistryValueData.From("Multi", RegistryValueKind.MultiString, new NativeRegistryValue(bytes));
        var json = System.Text.Json.JsonSerializer.Serialize(value);
        var restored = System.Text.Json.JsonSerializer.Deserialize<RegistryValueData>(json);

        Assert.NotNull(restored);
        Assert.Equal(bytes, Assert.IsType<NativeRegistryValue>(restored.ToRegistryData()).Bytes);
        Assert.Equal("\"Multi\"=hex(7):61,00,00,00,00,00,62,00,00,00,00,00", restored.ToRegLine());
    }

    // ---------------------------------------------------------------- Executor

    private sealed class MutatingQuarantine(IResidueQuarantineService inner, Action afterExport) : IResidueQuarantineService
    {
        public QuarantineEntry MoveToQuarantine(string originalPath, ResidueCategory category, string programName, CancellationToken ct, string? expectedFingerprint = null) =>
            inner.MoveToQuarantine(originalPath, category, programName, ct, expectedFingerprint);

        public QuarantineEntry ExportRegistryKey(ResidueRegistryHive hive, string subKeyPath, bool is64BitView, string fullKeyPath, string programName, CancellationToken ct, string? expectedFingerprint = null)
        {
            var entry = inner.ExportRegistryKey(hive, subKeyPath, is64BitView, fullKeyPath, programName, ct, expectedFingerprint);
            afterExport();
            return entry;
        }

        public IReadOnlyList<QuarantineEntry> GetEntries() => inner.GetEntries();

        public QuarantineRestoreResult Restore(Guid entryId, bool overwriteConfirmed, CancellationToken ct) =>
            inner.Restore(entryId, overwriteConfirmed, ct);
    }

    [Fact]
    public async Task SubchaveCriadaDepoisDoBackupImpedeARemocao()
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, AppKey);
        var item = ApprovedKey();
        var quarantine = new MutatingQuarantine(_quarantine, () => _reader.Add(ResidueRegistryHive.CurrentUser, $@"{AppKey}\Nova"));
        var executor = new ResidueCleanupExecutor(quarantine, _reader, _writer, _history);

        var result = Assert.Single((await executor.ExecuteAsync(Plan(item))).Results);

        Assert.Equal(ResidueCleanupItemOutcome.Failed, result.Outcome);
        Assert.Contains("NÃO foi removida", result.Message);
        Assert.DoesNotContain(_writer.Operations, o => o.StartsWith("DELETE"));
    }

    [Fact]
    public async Task ChaveEhRemovidaDeBaixoParaCimaSomenteComItensDoBackup()
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, $@"{AppKey}\A\B");
        var report = await _executor.ExecuteAsync(Plan(ApprovedKey()));

        Assert.True(report.IsFullSuccess);
        Assert.Equal(
            [$@"DELETE CurrentUser\{AppKey}\A\B", $@"DELETE CurrentUser\{AppKey}\A", $@"DELETE CurrentUser\{AppKey}"],
            _writer.Operations);
    }

    [Fact]
    public async Task RemocaoParcialDoRegistroNaoEhSucesso()
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, $@"{AppKey}\A");
        var item = ApprovedKey();
        _writer.FailDeleteAt = 2;

        var report = await _executor.ExecuteAsync(Plan(item));

        var result = Assert.Single(report.Results);
        Assert.Equal(ResidueCleanupItemOutcome.Failed, result.Outcome);
        Assert.Contains("PARCIAL", result.Message);
        Assert.NotNull(result.QuarantineEntryId);
        Assert.Equal(HistoryStatus.Failure, Assert.Single(_history.Records).Status);
    }

    [Fact]
    public async Task PrimeiraOperacaoOkESegundaFalhaEhParcialNoHistorico()
    {
        var good = NewFile("Exemplo A.log");
        var bad = NewFile("Exemplo B.log");
        var goodItem = Approved(good, ResidueCategory.File);
        var badItem = Approved(bad, ResidueCategory.File);
        File.AppendAllText(bad, " alterado");

        var report = await _executor.ExecuteAsync(Plan(goodItem, badItem));

        Assert.Equal(1, report.SuccessCount);
        Assert.Equal(1, report.FailureCount);
        Assert.False(report.IsFullSuccess);
        Assert.True(File.Exists(bad));
        var record = Assert.Single(_history.Records);
        Assert.Equal(HistoryStatus.Failure, record.Status);
        Assert.Contains("PARCIAL", record.Message);
    }

    [Fact]
    public async Task ItemSemIdentidadeVerificadaNaoEhExecutado()
    {
        var file = NewFile();
        var item = new ResidueItem { Id = Guid.NewGuid(), Category = ResidueCategory.File, Location = file, Confidence = ResidueConfidence.High };

        var report = await _executor.ExecuteAsync(Plan(item));

        Assert.Equal(ResidueCleanupItemOutcome.SkippedInvalid, Assert.Single(report.Results).Outcome);
        Assert.True(File.Exists(file));
    }

    // ---------------------------------------------------------------- Planejamento

    private sealed class FakeInventory(Func<IReadOnlyList<InstalledProgram>> programs) : IProgramInventoryService
    {
        public Task<IReadOnlyList<InstalledProgram>> GetInstalledProgramsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(programs());
    }

    [Fact]
    public async Task ConteudoNovoSemExecutavelDepoisDaPreviaBloqueiaOPlano()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var analysis = new ResidueAnalysisService(new FakeRegistryView(), _reader, _env.Rules);
        var program = Program(directory);
        var selected = (await analysis.DetectAsync(program, [])).Items.Where(i => i.IsEligibleForCleanup).ToList();
        Assert.NotEmpty(selected);
        File.WriteAllText(Path.Combine(directory, "documento.txt"), "criado depois");

        var plan = await new ResidueCleanupPlanner(analysis).BuildPlanAsync(program, [], selected, CancellationToken.None);

        Assert.Empty(plan.Approved);
    }

    [Fact]
    public async Task ProgramaInstaladoDepoisDaPreviaBloqueiaOPlano()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var analysis = new ResidueAnalysisService(new FakeRegistryView(), _reader, _env.Rules);
        var program = Program(directory);
        var selected = (await analysis.DetectAsync(program, [])).Items.Where(i => i.IsEligibleForCleanup).ToList();
        var planner = new ResidueCleanupPlanner(analysis, new FakeInventory(() => [Other("Novo Produto", directory)]));

        var plan = await planner.BuildPlanAsync(program, [], selected, CancellationToken.None);

        Assert.Empty(plan.Approved);
        Assert.NotEmpty(plan.Blocked);
    }

    [Fact]
    public async Task FalhaAoAtualizarInventarioBloqueiaOPlano()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var analysis = new ResidueAnalysisService(new FakeRegistryView(), _reader, _env.Rules);
        var program = Program(directory);
        var selected = (await analysis.DetectAsync(program, [])).Items.Where(i => i.IsEligibleForCleanup).ToList();
        var planner = new ResidueCleanupPlanner(analysis, new FakeInventory(() => throw new IOException("Registro indisponível")));

        var plan = await planner.BuildPlanAsync(program, [], selected, CancellationToken.None);

        Assert.Empty(plan.Approved);
        Assert.Equal(selected.Count, plan.Blocked.Count);
    }

    // ---------------------------------------------------------------- Interface

    private sealed class ThreadRecordingQuarantine(FakeQuarantineService inner) : IResidueQuarantineService
    {
        public int? MoveThreadId { get; private set; }

        public QuarantineEntry MoveToQuarantine(string originalPath, ResidueCategory category, string programName, CancellationToken ct, string? expectedFingerprint = null)
        {
            MoveThreadId = Environment.CurrentManagedThreadId;
            return inner.MoveToQuarantine(originalPath, category, programName, ct, expectedFingerprint);
        }

        public QuarantineEntry ExportRegistryKey(ResidueRegistryHive hive, string subKeyPath, bool is64BitView, string fullKeyPath, string programName, CancellationToken ct, string? expectedFingerprint = null) =>
            inner.ExportRegistryKey(hive, subKeyPath, is64BitView, fullKeyPath, programName, ct, expectedFingerprint);

        public IReadOnlyList<QuarantineEntry> GetEntries() => inner.GetEntries();

        public QuarantineRestoreResult Restore(Guid entryId, bool overwriteConfirmed, CancellationToken ct) =>
            inner.Restore(entryId, overwriteConfirmed, ct);
    }

    /// <summary>Thread única com fila, como o Dispatcher: continuações voltam sempre para ela.</summary>
    private sealed class SingleThreadContext : SynchronizationContext, IDisposable
    {
        private readonly System.Collections.Concurrent.BlockingCollection<(SendOrPostCallback Callback, object? State)> _queue = new();
        private readonly Thread _thread;

        public SingleThreadContext()
        {
            _thread = new Thread(() =>
            {
                SetSynchronizationContext(this);
                foreach (var (callback, state) in _queue.GetConsumingEnumerable())
                {
                    callback(state);
                }
            });
            _thread.Start();
        }

        public int ThreadId => _thread.ManagedThreadId;

        public override void Post(SendOrPostCallback d, object? state) => _queue.Add((d, state));

        public void Dispose()
        {
            _queue.CompleteAdding();
            _thread.Join();
        }
    }

    private async Task<(ResidueScanViewModel ViewModel, string Directory)> ScannedViewModelAsync(IResidueQuarantineService quarantine)
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var program = Program(directory);
        var analysis = new ResidueAnalysisService(new FakeRegistryView(), _reader, _env.Rules);
        var viewModel = new ResidueScanViewModel(program, [program], analysis, new ResidueCleanupPlanner(analysis),
            new ResidueCleanupExecutor(quarantine, _reader, _writer, _history), quarantine, new ImmediateSynchronizationContext())
        {
            ConfirmHandler = _ => Task.FromResult(true),
        };
        viewModel.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsScanning);
        viewModel.SelectAllEligible = true;
        Assert.Equal(1, viewModel.SelectedCount);
        return (viewModel, directory);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition(), "condição não atingida");
    }

    [Fact]
    public async Task LimpezaNaoExecutaNaThreadDaInterface()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        var program = Program(directory);
        var analysis = new ResidueAnalysisService(new FakeRegistryView(), _reader, _env.Rules);
        var quarantine = new ThreadRecordingQuarantine(new FakeQuarantineService());
        using var ui = new SingleThreadContext();
        var viewModel = new ResidueScanViewModel(program, [program], analysis, new ResidueCleanupPlanner(analysis),
            new ResidueCleanupExecutor(quarantine, _reader, _writer, _history), quarantine, ui)
        {
            ConfirmHandler = _ => Task.FromResult(true),
        };

        ui.Post(_ => viewModel.ScanCommand.Execute(null), null);
        await WaitUntilAsync(() => viewModel.Rows.Count == 1 && !viewModel.IsScanning);
        ui.Post(_ =>
        {
            viewModel.SelectAllEligible = true;
            viewModel.CleanupSelectedCommand.Execute(null);
        }, null);
        await WaitUntilAsync(() => quarantine.MoveThreadId is not null && !viewModel.IsCleaning);

        Assert.NotEqual(ui.ThreadId, quarantine.MoveThreadId);
    }

    [Fact]
    public async Task FalhaNaLimpezaApareceComoParcialELimpaAutorizacao()
    {
        var fake = new FakeQuarantineService { FailNextMove = true };
        var (viewModel, directory) = await ScannedViewModelAsync(fake);

        viewModel.CleanupSelectedCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsCleaning);

        Assert.Contains("PARCIAL", viewModel.StatusText);
        Assert.Contains(viewModel.LogLines, line => line.Contains("Falha:"));
        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.SelectAllEligible);
        Assert.False(viewModel.CleanupSelectedCommand.CanExecute(null));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public async Task BotaoNuncaHabilitaParaMediaBaixaOuProtegido()
    {
        Directory.CreateDirectory(Path.Combine(_env.ProgramFiles, "Exemplo App"));
        var analysis = new ResidueAnalysisService(new FakeRegistryView(), _reader, _env.Rules);
        var fake = new FakeQuarantineService();
        var viewModel = new ResidueScanViewModel(Program(), [Program()], analysis, new ResidueCleanupPlanner(analysis),
            new ResidueCleanupExecutor(fake, _reader, _writer, _history), fake, new ImmediateSynchronizationContext());
        viewModel.ScanCommand.Execute(null);
        await WaitUntilAsync(() => !viewModel.IsScanning);

        viewModel.Rows[0].IsSelected = true;
        viewModel.SelectAllEligible = true;

        Assert.Equal(ResidueConfidence.Medium, viewModel.Rows[0].Item.Confidence);
        Assert.Equal(0, viewModel.SelectedCount);
        Assert.False(viewModel.CleanupSelectedCommand.CanExecute(null));
    }

    [Fact]
    public async Task ConflitoDeRegistroNaoOfereceSobrescrita()
    {
        var fake = new FakeQuarantineService();
        fake.ExportRegistryKey(ResidueRegistryHive.CurrentUser, AppKey, true, AppKeyFull, "X", CancellationToken.None);
        var analysis = new ResidueAnalysisService(new FakeRegistryView(), _reader, _env.Rules);
        var viewModel = new ResidueScanViewModel(Program(), [Program()], analysis, new ResidueCleanupPlanner(analysis),
            new ResidueCleanupExecutor(fake, _reader, _writer, _history), fake, new ImmediateSynchronizationContext());
        var asked = false;
        viewModel.AskOverwriteHandler = _ =>
        {
            asked = true;
            return Task.FromResult(true);
        };
        await viewModel.RefreshQuarantineAsync();

        await viewModel.RestoreCommand.ExecuteAsync(viewModel.QuarantineEntries[0]);

        Assert.False(asked);
        Assert.Single(fake.Entries);
    }
}
