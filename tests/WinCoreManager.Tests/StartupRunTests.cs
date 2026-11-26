using System.Text;
using Microsoft.Win32;
using WinCoreManager.Models;
using WinCoreManager.Services.Startup;

namespace WinCoreManager.Tests;

public sealed class StartupRunTests
{
    [Theory]
    [InlineData(1u)]
    [InlineData(2u)]
    public async Task Desativar_e_reativar_preserva_nome_tipo_e_bytes_inclusive_espacos(uint type)
    {
        using var env = new RunFixture();
        var original = env.Put(type, "Steam – usuário");
        var active = await env.Entry();
        Assert.Equal(StartupStatus.Enabled, active.Status);
        Assert.True(active.CanChange);
        Assert.False(Directory.Exists(env.Journal));

        Assert.Equal(StartupChangeStatus.Applied, (await env.Change(active, false)).Status);
        Assert.Empty(env.Registry.Read());
        Assert.True(File.Exists(env.Host.Executable));
        Assert.Single(Directory.EnumerateFiles(env.Journal, "*.json"));

        var disabled = await env.Entry();
        Assert.True(disabled.Stored);
        Assert.True(disabled.CanChange);
        Assert.Equal(StartupStatus.Disabled, disabled.Status);
        Assert.Equal(original.Name, disabled.Name);

        Assert.Equal(StartupChangeStatus.Applied, (await env.Change(disabled, true)).Status);
        Assert.True(original.Matches(Assert.Single(env.Registry.Read())));
        Assert.False((await env.Entry()).Stored);
        Assert.Empty(Directory.EnumerateFiles(env.Journal, "*.json"));
        Assert.All(env.Host.History.Records, row => Assert.Equal(HistoryStatus.Success, row.Status));
    }

    [Fact]
    public async Task Valor_externamente_alterado_entre_leitura_e_commit_permanece_intacto()
    {
        using var env = new RunFixture();
        var original = env.Put();
        var active = await env.Entry();
        var modified = original with { Type = 2 };
        env.Registry.BeforeCompare = () => env.Registry.Value = modified;
        Assert.Equal(StartupChangeStatus.Conflict, (await env.Change(active, false)).Status);
        Assert.True(modified.Matches(env.Registry.Value));
        Assert.Equal(0, env.Registry.Writes);
    }

    [Fact]
    public async Task Colisao_sem_distincao_de_caixa_bloqueia_restauracao_e_exibe_backup()
    {
        using var env = new RunFixture();
        var original = env.Put();
        Assert.Equal(StartupChangeStatus.Applied, (await env.Change(await env.Entry(), false)).Status);
        var disabled = await env.Entry();
        var collision = original with { Name = original.Name.ToUpperInvariant() };
        env.Registry.Value = collision;
        Assert.Equal(StartupChangeStatus.Conflict, (await env.Change(disabled, true)).Status);
        Assert.True(collision.Matches(env.Registry.Value));
        var listed = (await env.Load()).Entries;
        Assert.Equal(2, listed.Count);
        Assert.All(listed, entry => Assert.False(entry.CanChange));
        Assert.Single(listed, entry => entry.Stored);
    }

    [Fact]
    public async Task Backup_corrompido_permanece_no_disco_e_bloqueia_nova_alteracao()
    {
        using var env = new RunFixture();
        var original = env.Put();
        Assert.Equal(StartupChangeStatus.Applied, (await env.Change(await env.Entry(), false)).Status);
        var disabled = await env.Entry();
        var path = Assert.Single(Directory.EnumerateFiles(env.Journal, "*.json"));
        await File.WriteAllTextAsync(path, "{journal incompleto");
        Assert.Equal(StartupChangeStatus.Conflict, (await env.Change(disabled, true)).Status);
        Assert.Empty(env.Registry.Read());
        Assert.True(File.Exists(path));
        Assert.Contains((await env.Load()).Warnings, warning => warning.Contains("corrompido", StringComparison.Ordinal));

        env.Registry.Value = original;
        Assert.False((await env.Entry()).CanChange);
    }

    [Fact]
    public async Task Falha_de_escrita_mantem_valor_e_snapshot_preparado_para_nova_tentativa()
    {
        using var env = new RunFixture();
        var original = env.Put();
        env.Registry.Error = new UnauthorizedAccessException();
        Assert.Equal(StartupChangeStatus.AccessDenied, (await env.Change(await env.Entry(), false)).Status);
        Assert.True(original.Matches(env.Registry.Value));
        Assert.Single(Directory.EnumerateFiles(env.Journal, "*.json"));
        env.Registry.Error = null;
        Assert.Equal(StartupChangeStatus.Applied, (await env.Change(await env.Entry(), false)).Status);
        Assert.True((await env.Entry()).Stored);
    }

    [Fact]
    public async Task Falha_de_confirmacao_pos_commit_preserva_recuperacao_apos_reabrir()
    {
        using var env = new RunFixture();
        var original = env.Put();
        env.Registry.FailAfterCommit = true;
        Assert.Equal(StartupChangeStatus.Unconfirmed, (await env.Change(await env.Entry(), false)).Status);
        Assert.Empty(env.Registry.Read());
        env.Registry.FailAfterCommit = false;
        var recovered = await env.Entry();
        Assert.True(recovered.Stored);
        Assert.Equal(StartupChangeStatus.Applied, (await env.Change(recovered, true)).Status);
        Assert.True(original.Matches(env.Registry.Value));
    }

    [Theory]
    [InlineData(3, StartupStatus.Disabled)]
    [InlineData(7, StartupStatus.Disabled)]
    [InlineData(255, StartupStatus.Unknown)]
    public async Task Estado_do_Windows_impede_alterar_uma_entrada(byte state, StartupStatus expected)
    {
        using var env = new RunFixture();
        var original = env.Put();
        env.Host.Registry.Approve(RegistryHive.CurrentUser, "Run", original.Name, state);
        var active = await env.Entry();
        Assert.Equal(expected, active.Status);
        Assert.False(active.CanChange);
        Assert.Equal(StartupChangeStatus.Protected, (await env.Change(active, false)).Status);
        Assert.False(Directory.Exists(env.Journal));
    }

    [Fact]
    public async Task Mudanca_de_StartupApproved_apos_backup_impede_reativar()
    {
        using var env = new RunFixture();
        var original = env.Put();
        await env.Change(await env.Entry(), false);
        var disabled = await env.Entry();
        env.Host.Registry.Approve(RegistryHive.CurrentUser, "Run", original.Name, 3);
        Assert.False((await env.Entry()).CanChange);
        Assert.Equal(StartupChangeStatus.Conflict, (await env.Change(disabled, true)).Status);
        Assert.Empty(env.Registry.Read());
        Assert.Equal(1, env.Registry.Writes);
    }

    [Fact]
    public async Task Alteracao_no_estado_do_Windows_imediatamente_antes_da_escrita_recusa_commit()
    {
        using var env = new RunFixture();
        var original = env.Put();
        var active = await env.Entry();
        env.Registry.BeforeCompare = () => env.Host.Registry.Approve(RegistryHive.CurrentUser, "Run", original.Name, 3);
        Assert.Equal(StartupChangeStatus.Conflict, (await env.Change(active, false)).Status);
        Assert.Equal(0, env.Registry.Writes);
        Assert.Empty(Directory.EnumerateFiles(env.Journal, "*.json"));
    }

    [Theory]
    [InlineData("Monitor de autenticação", "Steam")]
    [InlineData("Steam", "Antivírus")]
    [InlineData("Supervisor de serviços", "Steam")]
    public async Task Regras_de_protecao_bloqueiam_componentes_sensiveis(string name, string product)
    {
        using var env = new RunFixture();
        env.Host.Products.Product = env.Host.Products.Product with { Name = product };
        env.Put(name: name);
        Assert.False((await env.Entry()).CanChange);
    }

    [Fact]
    public async Task Categoria_de_consumo_aceita_produto_assinado_fora_dos_quatro_iniciais()
    {
        using var env = new RunFixture();
        var executable = Path.Combine(env.Host.Root, "firefox.exe");
        await File.WriteAllTextAsync(executable, "arquivo de teste");
        env.Host.Products.Product = new("Firefox", "Mozilla Corporation", "firefox.exe");
        env.Host.Products.Signer = "Mozilla Corporation";
        env.Registry.Value = Raw("Mozilla Firefox", 1, $"\"{executable}\" --background");
        var shortcut = Path.Combine(env.Host.Personal, "Firefox.lnk");
        await File.WriteAllTextAsync(shortcut, executable);

        Assert.True((await env.Entry()).CanChange);
        Assert.True((await env.Host.SingleAsync()).CanChange);
    }

    [Theory]
    [InlineData(3u, "binário")]
    [InlineData(1u, "valor\0ambíguo")]
    public async Task Valores_invalidos_sao_exibidos_sem_controle(uint type, string value)
    {
        using var env = new RunFixture();
        env.Registry.Value = Raw("DesktopApp", type, value);
        var entry = await env.Entry();
        Assert.False(entry.CanChange);
        Assert.Null(entry.Fingerprint);
        Assert.NotNull(entry.Resolution);
    }

    private static StartupRunValue Raw(string name, uint type, string command) =>
        new(name, type, Encoding.Unicode.GetBytes(command + "\0"));

    private sealed class RunFixture : IDisposable
    {
        public StartupFixture Host { get; } = new();
        public FakeRunRegistry Registry { get; }
        public string Journal => Path.Combine(Host.Root, "run-backup");

        public RunFixture() => Registry = new(Host.Registry);

        public StartupRunValue Put(uint type = 1, string name = "Steam") =>
            Registry.Value = Raw(name, type, $"  \"{Host.Executable}\" -silent  ");

        private StartupRunSource Source() => new(Registry, Host.Registry, Host.Products, new(Host.Products), new(Journal));
        public Task<StartupInventoryResult> Load() => new StartupInventory([Source()]).LoadAsync(default);
        public async Task<StartupEntry> Entry() => Assert.Single((await Load()).Entries);
        public Task<StartupChangeResult> Change(StartupEntry entry, bool enabled) =>
            new StartupChanges(Host.Folders, Host.Backups, new WindowsStartupFileMove(), Host.History, Source())
                .SetEnabledAsync(entry, enabled, default);
        public void Dispose() => Host.Dispose();
    }

    private sealed class FakeRunRegistry(FakeStartupRegistry approvals) : IStartupRunRegistry
    {
        public StartupRunValue? Value { get; set; }
        public Exception? Error { get; set; }
        public bool FailAfterCommit { get; set; }
        public Action? BeforeCompare { get; set; }
        public int Writes { get; private set; }

        public IReadOnlyList<StartupRunValue> Read() => Value is { } value ? [value] : [];
        public string? WriteRestriction() => null;

        public bool CompareExchange(string name, StartupRunValue? expected, StartupRunValue? replacement,
            string approval, CancellationToken token)
        {
            BeforeCompare?.Invoke();
            if (Error is not null) throw Error;
            var state = StartupApproval.For(name, StartupApproval.Read(approvals, RegistryHive.CurrentUser, "Run"),
                StartupApproval.Empty);
            if (state.Token != approval || (expected is null ? Value is not null : !expected.Matches(Value))) return false;
            token.ThrowIfCancellationRequested();
            Value = replacement;
            Writes++;
            if (FailAfterCommit) throw new StartupRunCommitException(new IOException());
            return true;
        }
    }
}

