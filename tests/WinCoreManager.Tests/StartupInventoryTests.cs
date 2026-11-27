using Microsoft.Win32;
using System.ComponentModel;
using System.Runtime.InteropServices;
using WinCoreManager.Models;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Tests;

public sealed class StartupInventoryTests
{
    [Fact]
    public async Task Registro_usa_HKCU_compartilhado_e_ambas_as_visoes_HKLM()
    {
        using var env = new StartupFixture();
        env.Registry.Put(RegistryHive.CurrentUser, RegistryView.Registry64, StartupRegistrySource.RunKey, "Steam", $"\"{env.Executable}\" -silent");
        var unknown = Path.Combine(env.Root, "App.exe");
        env.Registry.Put(RegistryHive.LocalMachine, RegistryView.Registry64, StartupRegistrySource.RunKey, "App", unknown);
        env.Registry.Put(RegistryHive.LocalMachine, RegistryView.Registry32, StartupRegistrySource.RunKey, "App", unknown);
        env.Registry.Approve(RegistryHive.LocalMachine, "Run32", "App", 3);
        env.Registry.Approve(RegistryHive.CurrentUser, "Run", "Steam", 6);

        var result = await env.Inventory.LoadAsync(default);

        Assert.Equal(3, result.Entries.Count);
        Assert.Empty(result.Warnings);
        Assert.Equal("Steam", result.Entries[0].Name);
        Assert.Equal("Valve Corp.", result.Entries[0].Publisher);
        Assert.Equal("-silent", result.Entries[0].Arguments);
        Assert.Equal(env.Executable, result.Entries[0].FilePath);
        Assert.Contains(result.Entries, e => e.Source == StartupSource.MachineRun64 && e.Status == StartupStatus.Enabled);
        Assert.Contains(result.Entries, e => e.Source == StartupSource.MachineRun32 && e.Status == StartupStatus.Disabled);
        Assert.All(result.Entries, entry => Assert.False(entry.CanChange));
        Assert.DoesNotContain(env.Registry.Reads, item => item is (RegistryHive.CurrentUser, RegistryView.Registry32, _));
        Assert.DoesNotContain(env.Registry.Reads, item => item.Key.EndsWith("RunOnce", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Registro_em_Windows_de_32_bits_nao_duplica_entradas()
    {
        using var env = new StartupFixture();
        env.Registry.HasSeparateMachineViews = false;
        env.Registry.Put(RegistryHive.LocalMachine, RegistryView.Default, StartupRegistrySource.RunKey, "App", env.Executable);

        var result = await env.Inventory.LoadAsync(default);

        Assert.Single(result.Entries);
        Assert.All(env.Registry.Reads, item => Assert.Equal(RegistryView.Default, item.View));
    }

    [Fact]
    public async Task Registro_expande_somente_valores_do_tipo_expandivel_e_mostra_comando_indireto()
    {
        using var env = new StartupFixture();
        const string command = "\"%SystemRoot%\\WinCoreFixtureMissing.exe\" --sample";
        env.Registry.Put(RegistryHive.CurrentUser, RegistryView.Registry64, StartupRegistrySource.RunKey, "Literal", command);
        env.Registry.Put(RegistryHive.CurrentUser, RegistryView.Registry64, StartupRegistrySource.RunKey, "Expansível",
            new ExpandableStartupCommand(command));

        var result = await env.Inventory.LoadAsync(default);

        var literal = Assert.Single(result.Entries, entry => entry.Name == "Literal");
        Assert.Null(literal.FilePath);
        Assert.Equal(command, literal.OriginalCommand);
        var expanded = Assert.Single(result.Entries, entry => entry.Name == "Expansível");
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinCoreFixtureMissing.exe"),
            expanded.FilePath, ignoreCase: true);
        Assert.Equal("--sample", expanded.Arguments);
    }

    [Fact]
    public async Task Falha_em_uma_visao_do_Registro_preserva_as_demais_fontes()
    {
        using var env = new StartupFixture();
        env.Registry.Failure = (RegistryHive.LocalMachine, RegistryView.Registry64, StartupRegistrySource.RunKey);
        env.Registry.Put(RegistryHive.LocalMachine, RegistryView.Registry32, StartupRegistrySource.RunKey, "App", env.Executable);
        env.PersonalShortcut();
        var result = await env.Inventory.LoadAsync(default);
        Assert.Contains(result.Entries, e => e.Source == StartupSource.MachineRun32);
        Assert.Contains(result.Entries, e => e.Source == StartupSource.UserFolder);
        Assert.NotEmpty(result.Warnings);
    }

    [Theory]
    [InlineData(2, StartupStatus.Enabled)]
    [InlineData(3, StartupStatus.Disabled)]
    [InlineData(6, StartupStatus.Enabled)]
    [InlineData(7, StartupStatus.Disabled)]
    [InlineData(42, StartupStatus.Unknown)]
    public async Task Estado_Windows_de_atalhos_nas_duas_pastas(byte data, StartupStatus expected)
    {
        using var env = new StartupFixture();
        env.PersonalShortcut();
        File.WriteAllText(Path.Combine(env.Common, "Steam Shared.lnk"), env.Executable);
        File.WriteAllText(Path.Combine(env.Personal, "desktop.ini"), "Desktop");
        env.Registry.Approve(RegistryHive.CurrentUser, "StartupFolder", "Steam.lnk", data);
        env.Registry.Approve(RegistryHive.LocalMachine, "StartupFolder", "Steam Shared.lnk", data);

        var result = await env.Inventory.LoadAsync(default);

        Assert.Equal(2, result.Entries.Count);
        Assert.All(result.Entries, entry => Assert.Equal(expected, entry.Status));
        var personal = Assert.Single(result.Entries, entry => entry.Source == StartupSource.UserFolder);
        Assert.Equal(expected == StartupStatus.Enabled, personal.CanChange);
        Assert.False(Assert.Single(result.Entries, e => e.Source == StartupSource.CommonFolder).CanChange);
        Assert.False(Directory.Exists(env.BackupRoot));
        Assert.True(File.Exists(personal.Location));
    }

    [Fact]
    public async Task Inventario_mantem_estado_indeterminado_quando_aprovacao_esta_malformada()
    {
        using var env = new StartupFixture();
        env.PersonalShortcut();
        env.Registry.Put(RegistryHive.CurrentUser, RegistryView.Registry64,
            FakeStartupRegistry.ApprovalPrefix + "StartupFolder", "Steam.lnk", new byte[] { 3 });

        var entry = Assert.Single((await env.Inventory.LoadAsync(default)).Entries);
        Assert.Equal(StartupStatus.Unknown, entry.Status);
        Assert.False(entry.CanChange);
    }

    [Theory]
    [InlineData(@"\\server\share\app.exe")]
    [InlineData(@"C:app.exe")]
    [InlineData(@"C:\Program Files\app.exe -start")]
    [InlineData(@"C:\Windows\System32\cmd.exe /c app.exe")]
    [InlineData(@"C:\Windows\explorer.exe shell:AppsFolder\App")]
    [InlineData(@"C:\app.exe:other")]
    [InlineData("steam://rungameid/123")]
    [InlineData("\"C:\\Program Files\\app.exe\"xyz")]
    public void Comando_invalido_ou_indireto_nao_inventa_executavel(string raw)
    {
        var result = StartupPaths.Command(raw, true);
        Assert.Null(result.Path);
        Assert.NotNull(result.Problem);
    }

    [Fact]
    public void Comando_direto_mostra_argumentos_e_indisponibilidade_do_arquivo()
    {
        using var env = new StartupFixture();
        var existing = StartupPaths.Command($"\"{env.Executable}\" -silent", true);
        Assert.Equal(env.Executable, existing.Path);
        Assert.Equal("-silent", existing.Arguments);
        Assert.Null(existing.Problem);
        var missing = StartupPaths.Command($"\"{Path.Combine(env.Root, "absent.exe")}\" -still", true);
        Assert.NotNull(missing.Path);
        Assert.NotNull(missing.Problem);
    }

    [Fact]
    public async Task Assinatura_nao_e_pre_requisito_para_atalho_comum_mas_antivirus_continua_protegido()
    {
        using var env = new StartupFixture();
        env.PersonalShortcut();
        env.Products.Signer = null;
        var unverified = Assert.Single((await env.Inventory.LoadAsync(default)).Entries);
        Assert.True(unverified.Recognized);
        Assert.True(unverified.CanChange);

        env.Products.Signer = "Valve Corp.";
        env.Products.Product = new("Antivírus", "Valve Corp.", "steam.exe");
        var security = Assert.Single((await env.Inventory.LoadAsync(default)).Entries);
        Assert.False(security.CanChange);
    }

    [Fact]
    public async Task Auditoria_aplicativo_assinado_fora_de_lista_de_editores_pode_ser_desativado()
    {
        using var env = new StartupFixture();
        env.PersonalShortcut();
        env.Products.Signer = "Contoso Tools Ltd.";
        env.Products.Product = new("Contoso Tray", "Contoso Tools Ltd.", "contoso-tray.exe");
        Assert.True(Assert.Single((await env.Inventory.LoadAsync(default)).Entries).CanChange);
    }

    [Fact]
    public void Tarefas_de_logon_e_boot_sao_consulta_e_gatilhos_inativos_sao_respeitados()
    {
        using var env = new StartupFixture();
        var scheduler = new StartupTaskSource(env.Products, new StartupPolicy(env.Products));
        string Definition(string trigger, string actions) => $"""
            <Task xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task"><Triggers>{trigger}</Triggers>
            <Actions>{actions}</Actions></Task>
            """;
        var exec = $"<Exec><Command>{env.Executable}</Command><Arguments>-silent</Arguments></Exec>";
        var logon = scheduler.Parse(@"\Steam", "Steam", true, Definition("<LogonTrigger/>", exec));
        Assert.NotNull(logon);
        Assert.Equal(StartupStatus.Enabled, logon.Status);
        Assert.Equal("-silent", logon.Arguments);
        Assert.False(logon.CanChange);
        var boot = scheduler.Parse(@"\Teste", "Teste", true,
            Definition("<BootTrigger><Enabled>false</Enabled></BootTrigger>", "<ComHandler/>"));
        Assert.NotNull(boot);
        Assert.Equal(StartupStatus.Disabled, boot.Status);
        Assert.Null(boot.FilePath);
        Assert.False(boot.CanChange);
        Assert.Null(scheduler.Parse(@"\Ignorar", "Ignorar", true, Definition("<TimeTrigger/>", exec)));
    }

    [Fact]
    public async Task Leitor_nativo_do_atalho_inspeciona_destino_sem_executar_inclusive_no_backup()
    {
        using var env = new StartupFixture();
        var link = Path.Combine(env.Personal, "Real.lnk");
        var stored = Path.Combine(env.Root, "Real.lnk.bin");
        await StartupInventory.RunOnSta(() =>
        {
            dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
            try
            {
                dynamic shortcut = shell.CreateShortcut(link);
                try
                {
                    shortcut.TargetPath = env.Executable;
                    shortcut.Arguments = "-silent";
                    shortcut.Save();
                }
                finally { Marshal.ReleaseComObject(shortcut); }
            }
            finally { Marshal.ReleaseComObject(shell); }

            File.Copy(link, stored);
            var reader = new WindowsStartupShortcutReader();
            foreach (var path in new[] { link, stored })
            {
                var target = reader.Read(path);
                Assert.Null(target.Problem);
                Assert.Equal(env.Executable, target.Path, ignoreCase: true);
                Assert.Equal("-silent", target.Arguments);
            }
            return true;
        }, default);
    }
}

public sealed class StartupChangesTests
{
    [Fact]
    public async Task Desativa_e_restaura_com_rename_nativo_e_historico_sem_tocar_no_executavel()
    {
        using var env = new StartupFixture();
        var path = env.PersonalShortcut();
        var changes = env.CreateChanges();
        var active = await env.SingleAsync();
        Assert.True(active.CanChange);
        Assert.False(Directory.Exists(env.BackupRoot));

        Assert.Equal(StartupChangeStatus.Applied, (await changes.SetEnabledAsync(active, false, default)).Status);
        Assert.False(File.Exists(path));
        Assert.True(File.Exists(env.Executable));
        var disabled = await env.SingleAsync();
        Assert.Equal(active.Id, disabled.Id);
        Assert.True(disabled.Stored);
        Assert.Equal(StartupStatus.Disabled, disabled.Status);
        var backup = Assert.Single(env.Backups.Read([], default));
        Assert.Equal(path, backup.Snapshot.Original);
        Assert.Equal(env.Executable, File.ReadAllText(backup.FilePath));
        Assert.NotEmpty(backup.Snapshot.Approval);

        var reopened = env.CreateChanges();
        Assert.Equal(StartupChangeStatus.Applied, (await reopened.SetEnabledAsync(disabled, true, default)).Status);
        Assert.Equal(env.Executable, File.ReadAllText(path));
        Assert.Equal(StartupStatus.Enabled, (await env.SingleAsync()).Status);
        Assert.Empty(env.Backups.Read([], default));
        Assert.Empty(Directory.EnumerateFiles(env.BackupRoot, "*.json"));
        Assert.Equal(2, env.History.Records.Count);
        Assert.All(env.History.Records, r =>
        {
            Assert.Equal(HistoryType.Startup, r.Type);
            Assert.Equal(HistoryStatus.Success, r.Status);
        });
    }

    [Fact]
    public async Task Entrada_ausente_entre_inventario_e_execucao_e_rejeitada()
    {
        using var env = new StartupFixture();
        var path = env.PersonalShortcut();
        var entry = await env.SingleAsync();
        File.Delete(path);

        var result = await env.CreateChanges().SetEnabledAsync(entry, false, default);
        Assert.Equal(StartupChangeStatus.Conflict, result.Status);
        Assert.Empty(env.Backups.Read([], default));
        Assert.Empty(env.History.Records);
    }

    [Fact]
    public async Task Alteracao_concorrente_dos_bytes_apos_snapshot_e_rejeitada_pelo_handle()
    {
        using var env = new StartupFixture();
        var path = env.PersonalShortcut();
        var entry = await env.SingleAsync();
        var mover = new InterceptMove
        {
            Before = () => File.AppendAllText(path, " mudou"),
        };

        var result = await env.CreateChanges(mover).SetEnabledAsync(entry, false, default);

        Assert.Equal(StartupChangeStatus.Conflict, result.Status);
        Assert.EndsWith(" mudou", File.ReadAllText(path));
        Assert.Empty(env.Backups.Read([], default));
        Assert.Empty(Directory.GetFiles(env.BackupRoot, "*.json"));
    }

    [Fact]
    public async Task Mudanca_concorrente_em_StartupApproved_e_rejeitada_na_ultima_verificacao()
    {
        using var env = new StartupFixture();
        var path = env.PersonalShortcut();
        var entry = await env.SingleAsync();
        var mover = new InterceptMove
        {
            Before = () => env.Registry.Approve(RegistryHive.CurrentUser, "StartupFolder", "Steam.lnk", 3),
        };
        var result = await env.CreateChanges(mover).SetEnabledAsync(entry, false, default);

        Assert.Equal(StartupChangeStatus.Conflict, result.Status);
        Assert.True(File.Exists(path));
        Assert.Empty(env.Backups.Read([], default));
    }

    [Fact]
    public async Task Restauracao_nao_sobrescreve_um_atalho_recriado_durante_a_operacao()
    {
        using var env = new StartupFixture();
        var path = env.PersonalShortcut();
        Assert.Equal(StartupChangeStatus.Applied, (await env.CreateChanges().SetEnabledAsync(await env.SingleAsync(), false, default)).Status);
        var disabled = await env.SingleAsync();
        var mover = new InterceptMove { Before = () => File.WriteAllText(path, "Novo atalho") };

        var result = await env.CreateChanges(mover).SetEnabledAsync(disabled, true, default);

        Assert.Equal(StartupChangeStatus.Conflict, result.Status);
        Assert.Equal("Novo atalho", File.ReadAllText(path));
        Assert.Single(env.Backups.Read([], default));
        Assert.All((await env.Inventory.LoadAsync(default)).Entries, entry => Assert.False(entry.CanChange));
    }

    [Fact]
    public async Task Reinicio_do_provedor_preserva_backup_enquanto_estado_do_Windows_mudou()
    {
        using var env = new StartupFixture();
        var path = env.PersonalShortcut();
        var entry = await env.SingleAsync();
        Assert.Equal(StartupChangeStatus.Applied, (await env.CreateChanges().SetEnabledAsync(entry, false, default)).Status);

        env.Registry.Approve(RegistryHive.CurrentUser, "StartupFolder", "Steam.lnk", 3);
        var backedUp = await env.SingleAsync();

        Assert.False(backedUp.CanChange);
        Assert.Equal(StartupChangeStatus.Protected, (await env.CreateChanges().SetEnabledAsync(backedUp, true, default)).Status);
        Assert.False(File.Exists(path));
        Assert.Single(env.Backups.Read([], default));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Cancelamento_antes_da_confirmacao_nativa_nao_move_o_atalho(bool cancelBeforeCall)
    {
        using var env = new StartupFixture();
        var path = env.PersonalShortcut();
        var entry = await env.SingleAsync();
        using var cancel = new CancellationTokenSource();
        if (cancelBeforeCall) cancel.Cancel();
        var mover = new InterceptMove { Before = cancel.Cancel };

        var result = await env.CreateChanges(mover).SetEnabledAsync(entry, false, cancel.Token);

        Assert.Equal(StartupChangeStatus.Cancelled, result.Status);
        Assert.True(File.Exists(path));
        Assert.Empty(env.Backups.Read([], default));
        Assert.Empty(env.History.Records);
    }

    [Fact]
    public async Task Permissao_insuficiente_informa_e_preserva_atalho()
    {
        using var env = new StartupFixture();
        var path = env.PersonalShortcut();
        var mover = new InterceptMove { Before = () => throw new Win32Exception(5) };
        var result = await env.CreateChanges(mover).SetEnabledAsync(await env.SingleAsync(), false, default);

        Assert.Equal(StartupChangeStatus.AccessDenied, result.Status);
        Assert.True(File.Exists(path));
        Assert.Empty(env.Backups.Read([], default));
        Assert.Equal(HistoryStatus.Failure, Assert.Single(env.History.Records).Status);
    }

    [Fact]
    public async Task Bloqueio_entre_instancias_impede_operacao_simultanea()
    {
        using var env = new StartupFixture();
        var path = env.PersonalShortcut();
        var entry = await env.SingleAsync();
        using var heldByAnotherOperation = env.Backups.Lock(env.Personal);

        var result = await env.CreateChanges().SetEnabledAsync(entry, false, default);

        Assert.Equal(StartupChangeStatus.Conflict, result.Status);
        Assert.True(File.Exists(path));
        Assert.Empty(env.Backups.Read([], default));
    }

    [Fact]
    public async Task Backup_sem_manifesto_bloqueia_novas_alteracoes_e_preserva_os_arquivos()
    {
        using var env = new StartupFixture();
        var path = env.PersonalShortcut();
        var before = await env.SingleAsync();
        Directory.CreateDirectory(env.BackupRoot);
        var orphan = Path.Combine(env.BackupRoot, $"{Guid.NewGuid():N}.lnk.bin");
        File.WriteAllText(orphan, "backup antigo");

        var result = await env.Inventory.LoadAsync(default);
        Assert.False(Assert.Single(result.Entries).CanChange);
        Assert.NotEmpty(result.Warnings);
        Assert.Equal(StartupChangeStatus.Protected, (await env.CreateChanges().SetEnabledAsync(before, false, default)).Status);
        Assert.True(File.Exists(path));
        Assert.Equal("backup antigo", File.ReadAllText(orphan));
    }

    [Fact]
    public async Task Estado_igual_e_entrada_de_Registo_nao_disparam_escrita()
    {
        using var env = new StartupFixture();
        env.PersonalShortcut();
        var entry = await env.SingleAsync();
        var changes = env.CreateChanges(new InterceptMove { Before = () => throw new InvalidOperationException() });

        Assert.Equal(StartupChangeStatus.Unchanged, (await changes.SetEnabledAsync(entry, true, default)).Status);
        Assert.Equal(StartupChangeStatus.Protected,
            (await changes.SetEnabledAsync(entry with { Source = StartupSource.UserRun }, false, default)).Status);
        Assert.False(Directory.Exists(env.BackupRoot));
    }

    private sealed class InterceptMove : IStartupFileMove
    {
        public Action? Before { get; init; }
        public bool MoveVerified(string source, string destination, string digest, Func<bool> revalidate, CancellationToken token)
        {
            Before?.Invoke();
            return new WindowsStartupFileMove().MoveVerified(source, destination, digest, revalidate, token);
        }
    }
}

internal sealed class FakeStartupRegistry : IStartupRegistry
{
    internal const string ApprovalPrefix = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\";
    private readonly Dictionary<(RegistryHive, RegistryView, string), Dictionary<string, object>> _data = [];
    public List<(RegistryHive Hive, RegistryView View, string Key)> Reads { get; } = [];
    public (RegistryHive, RegistryView, string)? Failure { get; set; }
    public bool HasSeparateMachineViews { get; set; } = true;

    public IReadOnlyDictionary<string, object> Read(RegistryHive hive, RegistryView view, string path)
    {
        var id = (hive, view, path);
        Reads.Add(id);
        if (id == Failure) throw new UnauthorizedAccessException();
        return _data.TryGetValue(id, out var values) ? values : StartupApproval.Empty;
    }

    public void Put(RegistryHive hive, RegistryView view, string key, string name, object value)
    {
        var id = (hive, view, key);
        if (!_data.TryGetValue(id, out var values))
            _data[id] = values = new(StringComparer.OrdinalIgnoreCase);
        values[name] = value;
    }

    public void Approve(RegistryHive hive, string kind, string name, byte value) =>
        Put(hive, RegistryView.Registry64, ApprovalPrefix + kind, name, new byte[] { value, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 });
}

internal sealed class StartupFixture : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "WinCoreManagerStartupTests", Guid.NewGuid().ToString("N"));
    public string Personal => Path.Combine(Root, "personal");
    public string Common => Path.Combine(Root, "common");
    public string BackupRoot => Path.Combine(Root, "backup");
    public string Executable => Path.Combine(Root, "Steam.exe");
    public FakeStartupRegistry Registry { get; } = new();
    public FakeProductReader Products { get; } = new();
    public FakeHistoryService History { get; } = new();
    public StartupBackupStore Backups { get; }
    public StartupFolderSource Folders { get; }
    public IStartupInventory Inventory { get; }

    public StartupFixture()
    {
        Directory.CreateDirectory(Personal);
        Directory.CreateDirectory(Common);
        File.WriteAllText(Executable, "arquivo simulado");
        Backups = new(BackupRoot);
        var policy = new StartupPolicy(Products);
        Folders = new(Personal, Common, Registry, new FakeShortcuts(), Products, policy, Backups);
        Inventory = new StartupInventory([Folders, new StartupRegistrySource(Registry, Products, policy)]);
    }

    public string PersonalShortcut()
    {
        var link = Path.Combine(Personal, "Steam.lnk");
        File.WriteAllText(link, Executable);
        return link;
    }

    public async Task<StartupEntry> SingleAsync() => Assert.Single((await Inventory.LoadAsync(default)).Entries);
    public StartupChanges CreateChanges(IStartupFileMove? mover = null) =>
        new(Folders, Backups, mover ?? new WindowsStartupFileMove(), History);

    public void Dispose()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException ex) when (attempt < 5 && (ex.HResult & 0xFFFF) is 32 or 33)
            {
                Thread.Sleep(120);
            }
        }
    }

    private sealed class FakeShortcuts : IStartupShortcutReader
    {
        public StartupTarget Read(string path) => StartupPaths.Executable(File.ReadAllText(path), "-silent", expand: false);
    }

    internal sealed class FakeProductReader : IStartupProductReader
    {
        public StartupProduct Product { get; set; } = new("Steam", "Valve Corp.", "steam.exe");
        public string? Signer { get; set; } = "Valve Corp.";
        public StartupProduct? Read(string? path) => StartupPaths.IsSafeFile(path) ? Product : null;
        public string? VerifiedPublisher(string path) => Signer;
    }
}
