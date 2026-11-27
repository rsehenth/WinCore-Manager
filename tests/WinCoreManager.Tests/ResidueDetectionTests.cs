using Microsoft.Win32;
using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;

namespace WinCoreManager.Tests;

/// <summary>Registro em memória para os testes de detecção/limpeza (nenhum acesso ao Registro real).</summary>
public sealed class FakeResidueRegistryReader : IResidueRegistryReader
{
    private sealed class Node
    {
        public Dictionary<string, object> Values { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> SubKeys { get; } = new();
    }

    private readonly Dictionary<string, Node> _nodes = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(ResidueRegistryHive hive, string subKeyPath) =>
        $"{(hive == ResidueRegistryHive.LocalMachine ? "HKEY_LOCAL_MACHINE" : "HKEY_CURRENT_USER")}\\{subKeyPath.Trim('\\')}";

    public FakeResidueRegistryReader Add(ResidueRegistryHive hive, string subKeyPath, Dictionary<string, string>? values = null)
    {
        Dictionary<string, object>? converted = null;
        if (values is not null)
        {
            converted = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, value) in values)
            {
                converted[name] = value;
            }
        }

        return Add(hive, subKeyPath, converted);
    }

    public FakeResidueRegistryReader Add(ResidueRegistryHive hive, string subKeyPath, Dictionary<string, object>? values)
    {
        var segments = subKeyPath.Trim('\\').Split('\\');
        for (var i = 0; i < segments.Length; i++)
        {
            var partial = string.Join('\\', segments[..(i + 1)]);
            if (!_nodes.TryGetValue(Key(hive, partial), out var node))
            {
                node = new Node();
                _nodes[Key(hive, partial)] = node;
                if (i > 0)
                {
                    _nodes[Key(hive, string.Join('\\', segments[..i]))].SubKeys.Add(segments[i]);
                }
            }

            if (i == segments.Length - 1 && values is not null)
            {
                foreach (var (name, value) in values)
                {
                    node.Values[name] = value;
                }
            }
        }

        return this;
    }

    public IReadOnlyList<string> GetSubKeyNames(ResidueRegistryHive hive, string subKeyPath, bool is64BitView) =>
        _nodes.TryGetValue(Key(hive, subKeyPath), out var node) ? node.SubKeys : Array.Empty<string>();

    public IReadOnlyList<string> GetValueNames(ResidueRegistryHive hive, string subKeyPath, bool is64BitView) =>
        _nodes.TryGetValue(Key(hive, subKeyPath), out var node) ? node.Values.Keys.ToList() : Array.Empty<string>();

    public string? GetStringValue(ResidueRegistryHive hive, string subKeyPath, string valueName, bool is64BitView) =>
        _nodes.TryGetValue(Key(hive, subKeyPath), out var node) && node.Values.TryGetValue(valueName, out var value) ? value as string : null;

    public object? GetRawValue(ResidueRegistryHive hive, string subKeyPath, string valueName, bool is64BitView) =>
        _nodes.TryGetValue(Key(hive, subKeyPath), out var node) && node.Values.TryGetValue(valueName, out var value) ? value : null;

    public bool KeyExists(ResidueRegistryHive hive, string subKeyPath, bool is64BitView) => _nodes.ContainsKey(Key(hive, subKeyPath));

    /// <summary>Tipos explícitos por "caminho|valor" (ex.: REG_EXPAND_SZ); sem entrada, deduz pelo dado.</summary>
    public Dictionary<string, RegistryValueKind> Kinds { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> SymbolicLinks { get; } = new(StringComparer.OrdinalIgnoreCase);

    public HashSet<string> UnreadableKeys { get; } = new(StringComparer.OrdinalIgnoreCase);

    public RegistryKeyContents ReadKeyStrict(ResidueRegistryHive hive, string subKeyPath, bool is64BitView)
    {
        if (UnreadableKeys.Contains(subKeyPath) || !_nodes.TryGetValue(Key(hive, subKeyPath), out var node))
        {
            throw new InvalidOperationException($"Leitura negada: {subKeyPath}");
        }

        var values = node.Values.Select(pair => new RegistryRawValue(pair.Key,
            Kinds.TryGetValue($"{subKeyPath}|{pair.Key}", out var kind) ? kind : pair.Value switch
            {
                int => RegistryValueKind.DWord,
                long => RegistryValueKind.QWord,
                byte[] => RegistryValueKind.Binary,
                string[] => RegistryValueKind.MultiString,
                _ => RegistryValueKind.String,
            }, pair.Value)).ToList();
        return new RegistryKeyContents(values, node.SubKeys.ToList(), SymbolicLinks.Contains(subKeyPath));
    }

    public void Remove(ResidueRegistryHive hive, string subKeyPath)
    {
        foreach (var path in _nodes.Keys.Where(k => k.Equals(Key(hive, subKeyPath), StringComparison.OrdinalIgnoreCase)
                                                    || k.StartsWith(Key(hive, subKeyPath) + "\\", StringComparison.OrdinalIgnoreCase)).ToList())
        {
            _nodes.Remove(path);
        }

        var separator = subKeyPath.LastIndexOf('\\');
        if (separator > 0 && _nodes.TryGetValue(Key(hive, subKeyPath[..separator]), out var parent))
        {
            parent.SubKeys.RemoveAll(name => name.Equals(subKeyPath[(separator + 1)..], StringComparison.OrdinalIgnoreCase));
        }
    }

    public void Dispose()
    {
    }
}

/// <summary>Escritor de Registro em memória que grava todas as operações para verificação nos testes.</summary>
public sealed class FakeResidueRegistryWriter : IResidueRegistryWriter
{
    public List<string> Operations { get; } = [];

    public bool FailNextOperations { get; set; }

    /// <summary>Falha a exclusão a partir da N-ésima chave (0 = nunca), para simular remoção parcial.</summary>
    public int FailDeleteAt { get; set; }

    public bool DeleteEmptySubKey(ResidueRegistryHive hive, string parentSubKeyPath, string keyName, bool is64BitView, out string error)
    {
        Operations.Add($"DELETE {hive}\\{parentSubKeyPath}\\{keyName}");
        if (FailNextOperations || (FailDeleteAt > 0 && Operations.Count(o => o.StartsWith("DELETE")) >= FailDeleteAt))
        {
            error = "falha simulada";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public bool CreateKey(ResidueRegistryHive hive, string subKeyPath, bool is64BitView, out string error)
    {
        Operations.Add($"CREATE {hive}\\{subKeyPath}");
        if (FailNextOperations)
        {
            error = "falha simulada";
            return false;
        }

        error = string.Empty;
        return true;
    }

    public Dictionary<string, (RegistryValueKind Kind, object Data)> Written { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool SetValue(ResidueRegistryHive hive, string subKeyPath, string valueName, RegistryValueKind kind, object data, bool is64BitView, out string error)
    {
        Written[$"{subKeyPath}|{valueName}"] = (kind, data);
        Operations.Add($"SET {hive}\\{subKeyPath}\\{valueName}={data}");
        if (FailNextOperations)
        {
            error = "falha simulada";
            return false;
        }

        error = string.Empty;
        return true;
    }
}

/// <summary>Ambiente sintético de arquivos + regras apontando para ele (nenhum diretório real do Windows).</summary>
public sealed class ResidueTestEnvironment : IDisposable
{
    public string Root { get; }
    public string WindowsDir { get; }
    public string ProgramFiles { get; }
    public string ProgramFilesX86 { get; }
    public string ProgramData { get; }
    public string Roaming { get; }
    public string Local { get; }
    public string AppBase { get; }
    public ResiduePathRules Rules { get; }

    public ResidueTestEnvironment(string name = "res")
    {
        Root = Path.Combine(Path.GetTempPath(), "WinCoreManagerTests", $"{name}_{Guid.NewGuid():N}");
        WindowsDir = Path.Combine(Root, "Windows");
        ProgramFiles = Path.Combine(Root, "Program Files");
        ProgramFilesX86 = Path.Combine(Root, "Program Files (x86)");
        ProgramData = Path.Combine(Root, "ProgramData");
        Roaming = Path.Combine(Root, "Users", "atual", "AppData", "Roaming");
        Local = Path.Combine(Root, "Users", "atual", "AppData", "Local");
        AppBase = Path.Combine(ProgramFiles, "WinCore Manager");

        foreach (var directory in new[] { WindowsDir, ProgramFiles, ProgramFilesX86, ProgramData, Roaming, Local, AppBase })
        {
            Directory.CreateDirectory(directory);
        }

        Rules = new ResiduePathRules
        {
            WindowsDirectory = WindowsDir,
            ProgramFiles = ProgramFiles,
            ProgramFilesX86 = ProgramFilesX86,
            ProgramData = ProgramData,
            UserProfile = Path.Combine(Root, "Users", "atual"),
            AppDataRoaming = Roaming,
            AppDataLocal = Local,
            AppBaseDirectory = AppBase,
        };
    }

    public void Dispose()
    {
        try
        {
            RemoveReparsePoints(Root);
            Directory.Delete(Root, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        static void RemoveReparsePoints(string directory)
        {
            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                {
                    var info = new DirectoryInfo(child);
                    if (info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        // Remove o link em si, sem atravessá-lo.
                        Directory.Delete(child, recursive: false);
                    }
                    else
                    {
                        RemoveReparsePoints(child);
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }
}

public class ResidueDetectionTests : IDisposable
{
    private readonly ResidueTestEnvironment _env = new();
    private readonly FakeResidueRegistryReader _registry = new();
    private readonly FakeRegistryView _uninstallView = new();
    private readonly ResidueDetectionEngine _engine;

    public ResidueDetectionTests()
    {
        _engine = new ResidueDetectionEngine(_uninstallView, _registry, _env.Rules);
    }

    private static InstalledProgram Program(string name = "Exemplo App", string? installLocation = null, Guid guid = default) =>
        new()
        {
            Name = name,
            RegistryKeyName = "ExemploApp",
            RegistryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ExemploApp",
            Is64Bit = true,
            InstallLocation = installLocation ?? string.Empty,
            MsiProductCode = guid,
        };

    private ResidueItem? Find(ResidueScanResult result, string locationPart) =>
        result.Items.FirstOrDefault(i => i.Location.Contains(locationPart, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void Auditoria_dados_do_usuario_com_nome_exato_do_programa_removido_sao_elegiveis_e_local_apagado_some()
    {
        // FMSuperScout version 1.7.3 (Inno): Program Files already removed, data left in AppData\Local,
        // including its WebView2 profile with component .dll and extension .js files.
        var data = Path.Combine(_env.Local, "FMSuperScout");
        Directory.CreateDirectory(Path.Combine(data, "history"));
        Directory.CreateDirectory(Path.Combine(data, "window", "Domain Actions"));
        File.WriteAllText(Path.Combine(data, "dump.json"), "{}");
        File.WriteAllText(Path.Combine(data, "window", "Domain Actions", "domain_actions.dll"), "componente simulado");
        // Same exact name outside the AppData roots (ProgramData) still needs manual review.
        var withExecutable = Path.Combine(_env.ProgramData, "FMSuperScout");
        Directory.CreateDirectory(withExecutable);
        File.WriteAllText(Path.Combine(withExecutable, "helper.exe"), "binário simulado");
        var removedInstall = Path.Combine(_env.ProgramFiles, "FMSuperScout");

        var result = _engine.Detect(Program("FMSuperScout version 1.7.3", removedInstall), [], CancellationToken.None);

        var eligible = Assert.Single(result.Items, i => i.Location.Equals(data, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(ResidueConfidence.High, eligible.Confidence);
        Assert.True(eligible.IsEligibleForCleanup);
        Assert.NotNull(eligible.Fingerprint);
        Assert.Equal(ResidueConfidence.Medium,
            Assert.Single(result.Items, i => i.Location.Equals(withExecutable, StringComparison.OrdinalIgnoreCase)).Confidence);
        Assert.DoesNotContain(result.Items, i => i.Location.StartsWith(removedInstall, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("TraceDraw", ResidueNameMatcher.TrimProductName("TraceDraw versão 0.51.1"));
        Assert.Equal("Ollama", ResidueNameMatcher.TrimProductName("Ollama version 0.40.18"));
    }

    [Fact]
    public void PastaComNomeExatoSemRegistroNaoEhElegivel()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);

        var result = _engine.Detect(Program(), [], CancellationToken.None);

        var item = Find(result, "Exemplo App");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.Medium, item.Confidence);
        Assert.False(item.IsEligibleForCleanup);
    }

    [Fact]
    public void PastaSemCorrespondenciaNaoEhReportada()
    {
        Directory.CreateDirectory(Path.Combine(_env.ProgramFiles, "Totalmente Diferente"));
        Directory.CreateDirectory(Path.Combine(_env.ProgramData, "Outra Coisa"));

        var result = _engine.Detect(Program(), [], CancellationToken.None);

        Assert.DoesNotContain(result.Items, i => i.Category is ResidueCategory.Folder or ResidueCategory.File);
    }

    [Fact]
    public void PastaComExecutaveisRecebeMediaConfianca()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "app.exe"), "binário simulado");

        var result = _engine.Detect(Program(), [], CancellationToken.None);

        var item = Find(result, "Exemplo App");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.Medium, item.Confidence);
        Assert.False(item.IsEligibleForCleanup);
    }

    [Fact]
    public void PastaDeOutroProgramaInstaladoEhProtegida()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);

        var other = new InstalledProgram
        {
            Name = "Exemplo App Suite",
            RegistryKeyName = "ExemploAppSuite",
            RegistryPath = @"HKLM\...\Outro",
            InstallLocation = directory,
        };

        var result = _engine.Detect(Program(), [other], CancellationToken.None);

        var item = Find(result, "Exemplo App");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.Protected, item.Confidence);
        Assert.Contains(item.Justifications, r => r.Contains("ainda instalado"));
    }

    [Fact]
    public void NomeUsadoPorOutroProgramaInstaladoEhProtegido()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);

        var other = new InstalledProgram
        {
            Name = "Exemplo App",
            RegistryKeyName = "OutroNome",
            RegistryPath = @"HKLM\...\Outro",
        };

        var result = _engine.Detect(Program("Exemplo"), [other], CancellationToken.None);

        var item = Find(result, "Exemplo App");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.Protected, item.Confidence);
    }

    [Fact]
    public void CorrespondenciaFracaEhBaixaConfianca()
    {
        // Distância 2, sem contenção: menor que um terço do nome (fraca), mas não perfeita.
        Directory.CreateDirectory(Path.Combine(_env.ProgramData, "Aplikativo Exemplos"));

        var result = _engine.Detect(Program("Aplicativo Exemplo"), [], CancellationToken.None);

        var item = Find(result, "Aplikativo Exemplos");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.Low, item.Confidence);
    }

    [Fact]
    public void DiretorioDeInstalacaoRegistradoEhAltaConfiancaMesmoForaDasRaizesPadrao()
    {
        var directory = Path.Combine(_env.Root, "Ferramentas", "Exemplo App");
        Directory.CreateDirectory(directory);

        var result = _engine.Detect(Program(installLocation: directory), [], CancellationToken.None);

        var item = Find(result, "Ferramentas");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.High, item.Confidence);
    }

    [Fact]
    public void PastaComReparsePointEhProtegida()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");

        // Junction real (não exige privilégios); o link não pode existir antes do mklink.
        var target = Path.Combine(_env.Root, "alvo-junction");
        Directory.CreateDirectory(target);
        var junction = new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{directory}\" \"{target}\"")
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = System.Diagnostics.Process.Start(junction);
        var output = process?.StandardOutput.ReadToEnd() + process?.StandardError.ReadToEnd();
        process?.WaitForExit(5000);
        Assert.True(Directory.Exists(directory) && (new DirectoryInfo(directory).Attributes & FileAttributes.ReparsePoint) != 0,
            $"Pré-condição: junction criada pelo teste. Saída: {output?.Trim()}");
        var result = _engine.Detect(Program(), [], CancellationToken.None);

        var item = Find(result, "Exemplo App");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.Protected, item.Confidence);
        Assert.Contains(item.Justifications, r => r.Contains("nova análise"));
    }

    [Fact]
    public void PastaDoProprioWinCoreManagerEhProtegida()
    {
        // O AppBaseDirectory do ambiente fica dentro de Program Files (instalação típica).
        var result = _engine.Detect(new InstalledProgram { Name = "WinCore Manager", RegistryKeyName = "WinCore", RegistryPath = @"HKLM\...\x" }, [], CancellationToken.None);

        var item = Find(result, "WinCore Manager");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.Protected, item.Confidence);
        Assert.Contains(item.Justifications, r => r.Contains("WinCore Manager"));
    }

    [Fact]
    public void ChaveComConexaoExplicitaEhAltaConfianca()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        _registry.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App", new Dictionary<string, string>
        {
            ["InstallDir"] = directory,
        });

        var result = _engine.Detect(Program(installLocation: directory), [], CancellationToken.None);

        var item = Find(result, @"SOFTWARE\Exemplo App");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.High, item.Confidence);
        Assert.Contains(item.Justifications, r => r.Contains("conexão explícita"));
    }

    [Fact]
    public void ChaveComApenasNomeSemelhanteEhMediaConfianca()
    {
        _registry.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App");

        var result = _engine.Detect(Program(), [], CancellationToken.None);

        var item = Find(result, @"SOFTWARE\Exemplo App");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.Medium, item.Confidence);
        Assert.False(item.IsEligibleForCleanup);
    }

    [Fact]
    public void ChaveDeDesinstalacaoAindaPresenteEhAltaConfianca()
    {
        var registryPath = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ExemploApp";
        var view = new FakeRegistryView(new FakeRegistryKey("ExemploApp", registryPath, true));
        _registry.Add(ResidueRegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ExemploApp");
        var engine = new ResidueDetectionEngine(view, _registry, _env.Rules);

        var result = engine.Detect(Program(), [], CancellationToken.None);

        var item = Find(result, "Uninstall\\ExemploApp");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.High, item.Confidence);
    }

    [Fact]
    public void ChaveNaListaNegraDoRegistroNaoEhEscaneada()
    {
        _registry.Add(ResidueRegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Exemplo App");
        _registry.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Classes\Exemplo App");

        var result = _engine.Detect(Program(), [], CancellationToken.None);

        Assert.DoesNotContain(result.Items, i => i.Category == ResidueCategory.RegistryKey);
    }

    [Fact]
    public void ChaveNomeadaComoOutroProgramaInstaladoEhProtegida()
    {
        _registry.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App");

        var other = new InstalledProgram
        {
            Name = "Exemplo App",
            RegistryKeyName = "Exemplo App",
            RegistryPath = @"HKLM\...\Outro",
        };

        var result = _engine.Detect(Program("Exemplo"), [other], CancellationToken.None);

        var item = Find(result, @"SOFTWARE\Exemplo App");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.Protected, item.Confidence);
    }

    [Fact]
    public void ChaveMsiDoProdutoNuncaEhElegivel()
    {
        var guid = new Guid("11111111-2222-3333-4444-555555555555");
        _registry.Add(ResidueRegistryHive.LocalMachine, MsiGuidPacker.GetProductKeySubPath(guid));

        var result = _engine.Detect(Program(guid: guid), [], CancellationToken.None);

        var item = Find(result, "Installer\\Products");
        Assert.NotNull(item);
        Assert.Equal(ResidueConfidence.Medium, item.Confidence);
    }

    [Fact]
    public void ChaveSobWow6432NodeNaoEhProtegidaPorPadrao()
    {
        _registry.Add(ResidueRegistryHive.LocalMachine, @"SOFTWARE\Wow6432Node\Exemplo App");

        var result = _engine.Detect(Program(), [], CancellationToken.None);

        var item = Find(result, "Wow6432Node\\Exemplo App");
        Assert.NotNull(item);
        Assert.NotEqual(ResidueConfidence.Protected, item.Confidence);
    }

    [Fact]
    public void ArquivoComNomeExatoNuncaEhElegivelSomentePeloNome()
    {
        File.WriteAllText(Path.Combine(_env.ProgramData, "Exemplo App.log"), "conteúdo");

        var result = _engine.Detect(Program(), [], CancellationToken.None);

        var item = Find(result, "Exemplo App.log");
        Assert.NotNull(item);
        Assert.Equal(ResidueCategory.File, item.Category);
        Assert.Equal(ResidueConfidence.Medium, item.Confidence);
    }

    [Fact]
    public void CancelamentoInterrompeADeteccao()
    {
        for (var i = 0; i < 30; i++)
        {
            Directory.CreateDirectory(Path.Combine(_env.ProgramFiles, $"Exemplo App {i}"));
        }

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(() => _engine.Detect(Program(), [], cts.Token));
    }

    [Fact]
    public void MuitosResultadosSaoRetornadosSemDuplicidade()
    {
        for (var i = 0; i < 50; i++)
        {
            Directory.CreateDirectory(Path.Combine(_env.ProgramFiles, $"Exemplo App {i}"));
        }
        _registry.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App");

        var result = _engine.Detect(Program(), [], CancellationToken.None);

        Assert.Equal(result.Items.Count, result.Items.DistinctBy(i => $"{i.Category}|{i.Location}|{i.Is64BitView}").Count());
        Assert.True(result.CountOf(ResidueConfidence.Medium) >= 50);
    }

    public void Dispose() => _env.Dispose();
}
