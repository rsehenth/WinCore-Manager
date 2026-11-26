using System.ComponentModel;
using WinCoreManager.Models;
using WinCoreManager.Services.WindowsFeatures;

namespace WinCoreManager.Tests;

public sealed class TweaksTests
{
    [Fact]
    public async Task Enumera_planos_reais_do_provedor_ativa_e_desfaz()
    {
        var history = new FakeHistoryService();
        var state = new WindowsFeaturesFixture(history);
        var reading = await Read(state, TweakSetting.ActivePlan);
        Assert.Equal(TweakValue.ForPlan(state.System.A), reading.Current);
        Assert.Equal(2, reading.Choices.Count);
        Assert.True((await state.Tweaks.ApplyAsync(reading, TweakValue.ForPlan(state.System.B), true)).Success);
        Assert.Equal(TweakValue.ForPlan(state.System.B), (await Read(state, TweakSetting.ActivePlan)).Current);

        var undo = (await state.Tweaks.ReadAsync()).Undo;
        Assert.NotNull(undo);
        Assert.True((await state.Tweaks.UndoAsync(undo, true)).Success);
        Assert.Equal(state.System.A, state.System.Active);
        Assert.Null((await state.Tweaks.ReadAsync()).Undo);
        Assert.All(history.Records, record => Assert.Equal(HistoryStatus.Success, record.Status));
    }

    [Theory]
    [InlineData(TweakSetting.MonitorAc)]
    [InlineData(TweakSetting.MonitorDc)]
    [InlineData(TweakSetting.SleepAc)]
    [InlineData(TweakSetting.SleepDc)]
    public async Task Tempo_do_plano_ativo_altera_um_indice_e_aceita_Nunca(TweakSetting setting)
    {
        var state = new WindowsFeaturesFixture();
        var target = await Read(state, setting);
        var never = Assert.Single(target.Choices, choice => choice.Label == "Nunca");
        Assert.Equal(new TweakValue(0), never.Value);
        Assert.True((await state.Tweaks.ApplyAsync(target, never.Value, true)).Success);
        Assert.Equal(target.Address, Assert.Single(state.System.Writes));
        Assert.Equal(new TweakValue(0), (await Read(state, setting)).Current);
        Assert.True((await state.Tweaks.UndoAsync((await state.Tweaks.ReadAsync()).Undo!, true)).Success);
        Assert.Equal(new TweakValue(600), (await Read(state, setting)).Current);
    }

    [Fact]
    public async Task Sem_bateria_somente_AC_e_exibido()
    {
        var state = new WindowsFeaturesFixture();
        state.System.Battery = false;
        Assert.DoesNotContain((await state.Tweaks.ReadAsync()).Settings, reading => reading.Address.IsBattery);
        Assert.Equal(2, (await state.Tweaks.ReadAsync()).Settings.Count(reading => reading.Address.IsTimeout));
    }

    [Fact]
    public async Task Plano_ausente_recusado_mesmo_quando_foi_mostrado()
    {
        var state = new WindowsFeaturesFixture();
        var old = await Read(state, TweakSetting.ActivePlan);
        state.System.HasOtherPlan = false;
        Assert.False((await state.Tweaks.ApplyAsync(old, TweakValue.ForPlan(state.System.B), true)).Success);
        Assert.Empty(state.System.Writes);
    }

    [Fact]
    public async Task Acesso_negado_na_gravacao_mantem_ajuste_anterior()
    {
        var state = new WindowsFeaturesFixture();
        var reading = await Read(state, TweakSetting.MonitorAc);
        state.System.WriteError = new Win32Exception(5);
        var result = await state.Tweaks.ApplyAsync(reading, new(0u), true);
        Assert.False(result.Success);
        Assert.Contains("Permissão insuficiente", result.Message);
        Assert.Equal(reading.Current, (await Read(state, TweakSetting.MonitorAc)).Current);
        Assert.Null((await state.Tweaks.ReadAsync()).Undo);
    }

    [Fact]
    public async Task Leitura_pos_escrita_e_obrigatoria_para_sucesso()
    {
        var state = new WindowsFeaturesFixture();
        var reading = await Read(state, TweakSetting.SleepAc);
        state.System.IgnoreWrite = true;
        var result = await state.Tweaks.ApplyAsync(reading, new(3600u), true);
        Assert.False(result.Success);
        Assert.Contains("não pôde ser confirmado", result.Message);
        Assert.NotNull(state.Journal.Read().Pending);
        Assert.Null((await state.Tweaks.ReadAsync()).Undo);
    }

    [Fact]
    public async Task Reinicio_recupera_mutacao_ocorrida_antes_da_confirmacao_do_diario()
    {
        var state = new WindowsFeaturesFixture();
        var reading = await Read(state, TweakSetting.WindowAnimation);
        state.System.FailAfterWrite = true;
        Assert.False((await state.Tweaks.ApplyAsync(reading, new(1u), true)).Success);
        var recovered = new TweakService(state.System, state.Journal, new FakeHistoryService(), state.Tools);
        var undo = (await recovered.ReadAsync()).Undo;
        Assert.NotNull(undo);
        Assert.Equal(reading.Address, undo.Address);
        state.System.FailAfterWrite = false;
        Assert.True((await recovered.UndoAsync(undo, true)).Success);
    }

    [Fact]
    public async Task Registro_de_recuperacao_inacessivel_impede_qualquer_escrita()
    {
        var state = new WindowsFeaturesFixture();
        state.Journal.Error = new IOException();
        var reading = await Read(state, TweakSetting.SleepAc);
        Assert.False((await state.Tweaks.ApplyAsync(reading, new(3600u), true)).Success);
        Assert.Empty(state.System.Writes);
    }

    [Fact]
    public async Task Preferencia_externa_impede_sobrescrita_e_gera_leitura_atual()
    {
        var state = new WindowsFeaturesFixture();
        var reading = await Read(state, TweakSetting.FileExtensions);
        state.System.Values[reading.Address] = new(1u);
        Assert.False((await state.Tweaks.ApplyAsync(reading, new(1u), true)).Success);
        Assert.Empty(state.System.Writes);
        Assert.Equal(new TweakValue(1), (await Read(state, TweakSetting.FileExtensions)).Current);
    }

    [Theory]
    [InlineData(TweakSetting.FileExtensions)]
    [InlineData(TweakSetting.HiddenFiles)]
    public async Task Ajuste_de_pasta_alterna_preferencia_sem_tocar_arquivos_protegidos(TweakSetting setting)
    {
        var state = new WindowsFeaturesFixture();
        var reading = await Read(state, setting);
        Assert.True((await state.Tweaks.ApplyAsync(reading, new(1u), true)).Success);
        Assert.Equal(reading.Address, Assert.Single(state.System.Writes));
        Assert.Equal(1u, state.System.ProtectedHidden);
        var on = await Read(state, setting);
        Assert.Equal(new TweakValue(1), on.Current);
        Assert.True((await state.Tweaks.ApplyAsync(on, new(0u), true)).Success);
        Assert.Equal(new TweakValue(0), (await Read(state, setting)).Current);
        Assert.Equal(1u, state.System.ProtectedHidden);
    }

    [Fact]
    public async Task Politica_do_Explorer_impede_gravar_e_mantem_o_estado()
    {
        var state = new WindowsFeaturesFixture();
        var reading = await Read(state, TweakSetting.HiddenFiles);
        state.System.Restricted.Add(TweakSetting.HiddenFiles);
        Assert.Contains("política", (await state.Tweaks.ApplyAsync(reading, new(1u), true)).Message);
        Assert.Empty(state.System.Writes);
        Assert.NotNull((await Read(state, TweakSetting.HiddenFiles)).Restriction);
    }

    [Fact]
    public async Task Efeitos_individuais_preservam_outros_e_perfis_abrem_no_Windows()
    {
        var state = new WindowsFeaturesFixture();
        state.System.Unavailable.Add(TweakSetting.Thumbnails);
        await state.TweaksPage.RefreshCommand.ExecuteAsync(null);
        Assert.Equal(4, state.TweaksPage.VisualProfiles.Count);
        Assert.Equal(7, state.TweaksPage.Effects.Count);
        Assert.False(state.TweaksPage.Effects.Single(row => row.Reading.Address.Setting == TweakSetting.Thumbnails).CanEdit);
        var pointer = state.TweaksPage.Effects.Single(row => row.Reading.Address.Setting == TweakSetting.PointerShadow);
        pointer.Choice = pointer.Choices[1];
        Assert.True(state.TweaksPage.ApplyCommand.CanExecute(pointer));
        await state.TweaksPage.ApplyCommand.ExecuteAsync(pointer);
        Assert.Equal(pointer.Reading.Address, Assert.Single(state.System.Writes));
        Assert.Equal(new TweakValue(0), (await Read(state, TweakSetting.FontSmoothing)).Current);
        Assert.Contains("confirmado", state.TweaksPage.Message);

        state.TweaksPage.OpenPerformanceCommand.Execute(null);
        Assert.Equal((WindowsTool.Performance, null), Assert.Single(state.Tools.Calls));
        await state.TweaksPage.UndoCommand.ExecuteAsync(null);
        Assert.Equal(new TweakValue(0), (await Read(state, TweakSetting.PointerShadow)).Current);
    }

    [Fact]
    public void Diario_real_isolado_preserva_snapshot_e_recusa_corrupcao()
    {
        var dir = Path.Combine(Path.GetTempPath(), "WinCoreTweaksTests", Guid.NewGuid().ToString("N"));
        try
        {
            var journal = new WindowsTweakJournal(dir);
            Assert.False(Directory.Exists(dir));
            Assert.Equal(new TweakJournalState(), journal.Read());
            using var journalLock = journal.Lock();
            var change = new TweakChange(new(TweakSetting.HiddenFiles), new(0u), new(1u));
            journal.Save(new(Pending: change));
            Assert.Equal(change, new WindowsTweakJournal(dir).Read().Pending);
            journal.Save(new(Last: change));
            Assert.Equal(change, new WindowsTweakJournal(dir).Read().Last);
            File.WriteAllText(Path.Combine(dir, "last-change.json"), "{bad");
            Assert.Throws<System.Text.Json.JsonException>(() => journal.Read());
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task Sem_confirmacao_nao_grava_nem_reverte()
    {
        var state = new WindowsFeaturesFixture();
        var change = await Read(state, TweakSetting.FileExtensions);
        Assert.False((await state.Tweaks.ApplyAsync(change, new(1u), false)).Success);
        Assert.False((await state.Tweaks.UndoAsync(new(change.Address, new(0u), new(1u)), false)).Success);
        Assert.Empty(state.System.Writes);
        Assert.Equal(new TweakJournalState(), state.Journal.Read());
    }

    private static async Task<TweakReading> Read(WindowsFeaturesFixture state, TweakSetting setting) =>
        (await state.Tweaks.ReadAsync()).Settings.Single(row => row.Address.Setting == setting);
}

