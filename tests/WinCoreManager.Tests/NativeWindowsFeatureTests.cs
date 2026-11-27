using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WinCoreManager.Services.Startup;
using WinCoreManager.Services.WindowsFeatures;
using Xunit.Abstractions;

namespace WinCoreManager.Tests;

public sealed class NativeWindowsFeatureTests(ITestOutputHelper output)
{
    [Fact]
    public void Hive_isolado_preserva_bytes_e_recusa_gravacao_quando_KTM_esta_indisponivel()
    {
        var directory = Path.Combine(Path.GetTempPath(), "WinCoreRunHiveTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var status = RegLoadAppKeyW(Path.Combine(directory, "startup-fixture.hive"), out var handle, 0xF003F, 1, 0);
            using (handle)
            {
                Assert.Equal(0, status);
                using var root = RegistryKey.FromHandle(handle);
                using var startup = root.CreateSubKey(StartupRegistrySource.RunKey);
                using var approvals = root.CreateSubKey(FakeStartupRegistry.ApprovalPrefix + "Run");
                const string name = "Example Desktop";
                byte[] enabled = [2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
                const string approval = "020000000000000000000000/-";
                startup.SetValue(name, @"  ""%TEMP%\Example.exe"" --background  ", RegistryValueKind.ExpandString);
                approvals.SetValue(name, enabled, RegistryValueKind.Binary);
                var adapter = WindowsStartupRunRegistry.ForApplicationHive(handle);
                var original = Assert.Single(adapter.Read());
                Assert.Equal(name, original.Name);
                Assert.Equal(2u, original.Type);

                if (adapter.WriteRestriction() is { } restriction)
                {
                    output.WriteLine($"Hive isolado: {restriction}");
                    Assert.Throws<System.ComponentModel.Win32Exception>(() =>
                        adapter.CompareExchange(name, original, null, approval, default));
                    Assert.True(original.Matches(Assert.Single(adapter.Read())));
                    Assert.Equal(enabled, (byte[])approvals.GetValue(name)!);
                    return;
                }

                byte[] disabled = [3, 0, 0, 0, 11, 22, 33, 44, 55, 66, 77, 88];
                approvals.SetValue(name, disabled, RegistryValueKind.Binary);
                Assert.False(adapter.CompareApproval(original, enabled, disabled, default));
                Assert.True(adapter.CompareApproval(original, disabled, enabled, default));
                Assert.Equal(enabled, (byte[])approvals.GetValue(name)!);
                Assert.True(adapter.CompareApproval(original, enabled, disabled, default));
                Assert.Equal(disabled, (byte[])approvals.GetValue(name)!);
                Assert.True(original.Matches(Assert.Single(adapter.Read())));
                approvals.SetValue(name, enabled, RegistryValueKind.Binary);

                Assert.False(adapter.CompareExchange(name, original, null, "-/-", default));
                Assert.True(adapter.CompareExchange(name, original, null, approval, default));
                Assert.Empty(adapter.Read());

                startup.SetValue(name.ToUpperInvariant(), @"C:\Synthetic\Collision.exe");
                Assert.False(adapter.CompareExchange(name, null, original, approval, default));
                Assert.Equal(@"C:\Synthetic\Collision.exe", Assert.Single(adapter.Read()).Command);
                startup.DeleteValue(name);

                Assert.True(adapter.CompareExchange(name, null, original, approval, default));
                Assert.True(original.Matches(Assert.Single(adapter.Read())));
                Assert.Equal(enabled, (byte[])approvals.GetValue(name)!);
            }
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Sondagem_nativa_somente_leitura()
    {
        var cleanup = new WindowsCleanupPlatform();
        var windows = new WindowsTools();
        var volume = cleanup.ReadWindowsVolume();
        Assert.True(volume.Capacity > 0 && volume.Free >= 0 && volume.Used >= 0);
        Assert.False(string.IsNullOrWhiteSpace(volume.Identity));

        var categories = cleanup.ReadCatalog();
        var recycle = WindowsVolume.IsDriveRoot(volume.Root) ? cleanup.QueryRecycleBin(volume.Root) : null;
        var tweaks = await new TweakService(new WindowsTweakPlatform(), new WindowsFeaturesFixture.MemoryTweakJournal(),
            new FakeHistoryService(), windows).ReadAsync();
        var startup = new WindowsStartupRunRegistry();
        var values = startup.Read();
        var products = new WindowsStartupProductReader();
        var policy = new StartupPolicy(products);
        var eligible = new List<StartupEntry>();
        var isolatedBackups = Path.Combine(Path.GetTempPath(), $"WinCoreStartupProbe-{Guid.NewGuid():N}");
        new StartupRunSource(startup, new WindowsStartupRegistry(), products, policy,
            new StartupRunBackups(isolatedBackups)).Read(eligible, [], default);
        var firefox = eligible.FirstOrDefault(entry => entry.Arguments == "-os-autostart" && entry.FilePath is not null);
        var firefoxTargetApproved = firefox is not null && policy.PersonalShortcutProtection(
            new StartupTarget(firefox.FilePath, firefox.Arguments, firefox.Resolution), products.Read(firefox.FilePath), firefox.Name) is null;
        var restore = await new RestorePointService(new WindowsRestorePointReader()).ReadAsync();

        output.WriteLine($"Volume do Windows: {volume.Root}. Catálogo: {categories.Count}. Lixeira com estimativa: {recycle is not null}.");
        output.WriteLine($"Categorias registradas: {string.Join(", ", categories.Select(category => category.Id))}.");
        var plan = tweaks.Settings.Single(row => row.Address.Setting == TweakSetting.ActivePlan);
        output.WriteLine($"Planos listados: {plan.Choices.Count}. Plano ativo lido: {plan.Current?.Plan is not null}. Restrição: {plan.Restriction ?? "nenhuma"}.");
        output.WriteLine($"Configurações lidas: {string.Join("; ", tweaks.Settings.Select(setting => $"{setting.Address.Setting}={setting.Current is not null}/{setting.Choices.Count}/{setting.Restriction ?? "ok"}"))}.");
        output.WriteLine($"HKCU Run: {values.Count} valores lidos. Transação sem gravação: {startup.WriteRestriction() ?? "disponível"}. Restauração: {restore.State}.");
        output.WriteLine($"HKCU Run: {eligible.Count(entry => entry.CanChange)} entradas elegíveis. Firefox -os-autostart: estado={firefox?.Status}, alvo aprovado pela política={firefoxTargetApproved}.");
        foreach (var entry in eligible)
            output.WriteLine($"HKCU Run · {entry.Name}: {(entry.CanChange ? "elegível" : entry.Protection ?? entry.Resolution ?? entry.Status.ToString())}");
        output.WriteLine($"Ferramentas presentes: {string.Join(", ", Enum.GetValues<WindowsTool>().Where(windows.IsAvailable))}.");
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int RegLoadAppKeyW(string file, out SafeRegistryHandle hive, int access, int options, int reserved);
}

