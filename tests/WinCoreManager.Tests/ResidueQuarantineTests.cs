using WinCoreManager.Models;
using WinCoreManager.Services.Uninstaller;
using System.Security.Cryptography;

namespace WinCoreManager.Tests;

public class ResidueQuarantineTests : IDisposable
{
    private readonly ResidueTestEnvironment _env = new("quar");
    private readonly string _quarantineRoot;
    private readonly FakeResidueRegistryReader _reader = new();
    private readonly FakeResidueRegistryWriter _writer = new();
    private readonly ResidueQuarantineService _service;

    public ResidueQuarantineTests()
    {
        _quarantineRoot = Path.Combine(_env.Root, "quarantine");
        _service = new ResidueQuarantineService(_quarantineRoot, _reader, _writer, _env.Rules);
    }

    [Fact]
    public async Task ArquivoEMovidoEregistradoNoManifest()
    {
        var file = Path.Combine(_env.ProgramData, "Exemplo App.log");
        await File.WriteAllTextAsync(file, "conteúdo");

        var entry = _service.MoveToQuarantine(file, ResidueCategory.File, "Exemplo App", CancellationToken.None);

        Assert.False(File.Exists(file));
        Assert.True(File.Exists(entry.QuarantinedPath));
        Assert.Equal(file, entry.OriginalPath);
        Assert.Equal("Exemplo App", entry.ProgramName);
        var entries = _service.GetEntries();
        Assert.Contains(entries, e => e.Id == entry.Id);
    }

    [CrossVolumeFact]
    [Trait("Topology", "CrossVolume")]
    public void ArquivoEmOutroVolumeUsaQuarentenaNaOrigemERestauraBytesAposReabrir()
    {
        var secondVolume = CrossVolumeFactAttribute.SecondVolume!;
        Assert.True(CrossVolumeFactAttribute.AreSeparateVolumes(_env.Root, secondVolume));

        var primary = Path.Combine(secondVolume, "fixture-" + Guid.NewGuid().ToString("N"));
        var service = new ResidueQuarantineService(primary, _reader, _writer, _env.Rules, sourceVolumeRoot: _quarantineRoot);
        var file = Path.Combine(_env.ProgramData, "sintetico.dat");
        byte[] bytes = [0, 49, 255, 0, 31];
        File.WriteAllBytes(file, bytes);

        var entry = service.MoveToQuarantine(file, ResidueCategory.File, "Fixture", CancellationToken.None);
        Assert.False(File.Exists(file));
        Assert.Equal(_quarantineRoot, Path.GetDirectoryName(entry.QuarantinedPath));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(bytes)), entry.ContentSha256);
        Assert.False(Directory.Exists(primary));

        var reopened = new ResidueQuarantineService(primary, _reader, _writer, _env.Rules, sourceVolumeRoot: _quarantineRoot);
        Assert.Equal(entry.Id, Assert.Single(reopened.GetEntries()).Id);
        Assert.Equal(QuarantineRestoreStatus.Restored, reopened.Restore(entry.Id, false, CancellationToken.None).Status);
        Assert.Equal(bytes, File.ReadAllBytes(file));
        Assert.Empty(reopened.GetEntries());
    }

    [CrossVolumeFact]
    [Trait("Topology", "CrossVolume")]
    public void PastaEmOutroVolumePermaneceNaOrigem()
    {
        var secondVolume = CrossVolumeFactAttribute.SecondVolume!;
        Assert.True(CrossVolumeFactAttribute.AreSeparateVolumes(_env.Root, secondVolume));
        var primary = Path.Combine(secondVolume, "fixture-" + Guid.NewGuid().ToString("N"));
        var service = new ResidueQuarantineService(primary, _reader, _writer, _env.Rules, sourceVolumeRoot: _quarantineRoot);
        var directory = Directory.CreateDirectory(Path.Combine(_env.ProgramData, "fixture-folder")).FullName;
        var file = Path.Combine(directory, "synthetic.txt");
        File.WriteAllText(file, "fixture");
        Assert.Throws<InvalidOperationException>(() => service.MoveToQuarantine(directory, ResidueCategory.Folder, "Fixture", CancellationToken.None));
        Assert.Equal("fixture", File.ReadAllText(file));
        Assert.Empty(service.GetEntries());
    }

    [CrossVolumeFact]
    [Trait("Topology", "CrossVolume")]
    public void PastaComProvaPodeUsarVolumeDeOrigemERestaurarFluxosEPermissoes()
    {
        var secondVolume = CrossVolumeFactAttribute.SecondVolume!;
        Assert.True(CrossVolumeFactAttribute.AreSeparateVolumes(_env.Root, secondVolume));
        var primary = Path.Combine(secondVolume, "fixture-" + Guid.NewGuid().ToString("N"));
        var service = new ResidueQuarantineService(primary, _reader, _writer, _env.Rules, sourceVolumeRoot: _quarantineRoot);
        var directory = Directory.CreateDirectory(Path.Combine(_env.ProgramData, "fixture-proven")).FullName;
        var child = Path.Combine(Directory.CreateDirectory(Path.Combine(directory, "inside")).FullName, "synthetic.txt");
        File.WriteAllText(child, "fixture");
        File.WriteAllText(child + ":test", "alternate");
        var fingerprint = ResidueFileGuard.Inspect(directory, 10, CancellationToken.None)!.Fingerprint;
        var evidence = ResidueRecoveryIntegrity.Capture(directory, directory: true, CancellationToken.None);

        var entry = service.MoveToQuarantine(directory, ResidueCategory.Folder, "Fixture", CancellationToken.None, fingerprint);
        Assert.Equal(_quarantineRoot, Path.GetDirectoryName(entry.QuarantinedPath));
        Assert.Equal(evidence, entry.RecoveryFingerprint);
        Assert.False(Directory.Exists(primary));
        var storedChild = Path.Combine(entry.QuarantinedPath, "inside", "synthetic.txt");
        File.SetLastWriteTimeUtc(storedChild, DateTime.UtcNow.AddHours(-1));
        File.WriteAllText(storedChild + ":test", "different");
        Assert.Equal(QuarantineRestoreStatus.Failed, service.Restore(entry.Id, false, CancellationToken.None).Status);
        File.WriteAllText(storedChild + ":test", "alternate");
        Assert.Equal(QuarantineRestoreStatus.Restored, service.Restore(entry.Id, false, CancellationToken.None).Status);
        Assert.Equal(evidence, ResidueRecoveryIntegrity.Capture(directory, directory: true, CancellationToken.None));
        Assert.Equal("alternate", File.ReadAllText(child + ":test"));
    }

    [Fact]
    public void MovimentoERecuperacaoDePastaToleramFalhaDeJournalSemPerderDados()
    {
        var directory = Directory.CreateDirectory(Path.Combine(_env.ProgramData, "fixture-recovery")).FullName;
        var file = Path.Combine(directory, "payload.txt");
        File.WriteAllText(file, "first");
        var service = new ResidueQuarantineService(_quarantineRoot, _reader, _writer, _env.Rules, sourceVolumeFilesOnly: true);
        var fingerprint = ResidueFileGuard.Inspect(directory, 10, CancellationToken.None)!.Fingerprint;
        service.AfterMoveForTest = () => throw new IOException("Synthetic commit failure.");
        Assert.Throws<InvalidOperationException>(() =>
            service.MoveToQuarantine(directory, ResidueCategory.Folder, "Fixture", CancellationToken.None, fingerprint));
        Assert.Equal("first", File.ReadAllText(file));
        Assert.Empty(service.GetEntries());

        service.AfterMoveForTest = null;
        var entry = service.MoveToQuarantine(directory, ResidueCategory.Folder, "Fixture", CancellationToken.None, fingerprint);
        service.AfterRestoreForTest = () => throw new IOException("Synthetic journal failure.");
        Assert.Equal(QuarantineRestoreStatus.Restored, service.Restore(entry.Id, false, CancellationToken.None).Status);
        Assert.Equal("first", File.ReadAllText(file));
        Assert.Empty(new ResidueQuarantineService(_quarantineRoot, _reader, _writer, _env.Rules, sourceVolumeFilesOnly: true).GetEntries());
    }

    [Fact]
    public void QuarentenaLocalRecusaArquivoComConteudoAlterado()
    {
        var service = new ResidueQuarantineService(_quarantineRoot, _reader, _writer, _env.Rules, sourceVolumeFilesOnly: true);
        var file = Path.Combine(_env.ProgramData, "sintetico.dat");
        File.WriteAllBytes(file, [1, 2, 3]);
        var entry = service.MoveToQuarantine(file, ResidueCategory.File, "Fixture", CancellationToken.None);
        var timestamp = File.GetLastWriteTimeUtc(entry.QuarantinedPath);
        File.WriteAllBytes(entry.QuarantinedPath, [1, 2, 4]);
        File.SetLastWriteTimeUtc(entry.QuarantinedPath, timestamp);

        Assert.Equal(QuarantineRestoreStatus.Failed, service.Restore(entry.Id, false, CancellationToken.None).Status);
        Assert.False(File.Exists(file));
        Assert.True(File.Exists(entry.QuarantinedPath));
    }

    [Fact]
    public async Task PastaEMovidaInteira()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(Path.Combine(directory, "sub"));
        await File.WriteAllTextAsync(Path.Combine(directory, "sub", "a.txt"), "x");

        var entry = _service.MoveToQuarantine(directory, ResidueCategory.Folder, "Exemplo App", CancellationToken.None);

        Assert.False(Directory.Exists(directory));
        Assert.True(Directory.Exists(entry.QuarantinedPath));
        Assert.True(File.Exists(Path.Combine(entry.QuarantinedPath, "sub", "a.txt")));
    }

    [Fact]
    public void ItemInexistenteFalhaSemExcecaoInesperada()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _service.MoveToQuarantine(@"C:\nao\existe\nunca_738912", ResidueCategory.File, "X", CancellationToken.None));
    }

    [Fact]
    public void MovimentacaoEntreVolumesNaoEhAutomatica()
    {
        Assert.True(ResidueQuarantineService.RequiresCrossVolume(@"C:\Program Files\App", @"D:\quarentena"));
        Assert.False(ResidueQuarantineService.RequiresCrossVolume(@"C:\Program Files\App", @"C:\quarentena"));
    }

    [Fact]
    public void RecuperacaoDeArquivoSemConflitoVoltaAoOriginal()
    {
        var file = Path.Combine(_env.ProgramData, "Exemplo App.log");
        File.WriteAllText(file, "conteúdo");
        var entry = _service.MoveToQuarantine(file, ResidueCategory.File, "Exemplo App", CancellationToken.None);

        var result = _service.Restore(entry.Id, overwriteConfirmed: false, CancellationToken.None);

        Assert.Equal(QuarantineRestoreStatus.Restored, result.Status);
        Assert.True(File.Exists(file));
        Assert.False(File.Exists(entry.QuarantinedPath));
        Assert.DoesNotContain(_service.GetEntries(), e => e.Id == entry.Id);
    }

    [Fact]
    public void RecuperacaoComDestinoOcupadoExigeConfirmacao()
    {
        var file = Path.Combine(_env.ProgramData, "Exemplo App.log");
        File.WriteAllText(file, "original");
        var entry = _service.MoveToQuarantine(file, ResidueCategory.File, "Exemplo App", CancellationToken.None);
        File.WriteAllText(file, "novo conteúdo");

        var blocked = _service.Restore(entry.Id, overwriteConfirmed: false, CancellationToken.None);
        Assert.Equal(QuarantineRestoreStatus.ConflictDestinationExists, blocked.Status);
        Assert.Equal("novo conteúdo", File.ReadAllText(file));

        var confirmed = _service.Restore(entry.Id, overwriteConfirmed: true, CancellationToken.None);
        Assert.Equal(QuarantineRestoreStatus.Restored, confirmed.Status);
        Assert.Equal("original", File.ReadAllText(file));
    }

    [Fact]
    public void RecuperacaoDePastaRecriaArvore()
    {
        var directory = Path.Combine(_env.ProgramFiles, "Exemplo App");
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "a.txt"), "dados");
        var entry = _service.MoveToQuarantine(directory, ResidueCategory.Folder, "Exemplo App", CancellationToken.None);

        var result = _service.Restore(entry.Id, overwriteConfirmed: false, CancellationToken.None);

        Assert.Equal(QuarantineRestoreStatus.Restored, result.Status);
        Assert.Equal("dados", File.ReadAllText(Path.Combine(directory, "a.txt")));
    }

    [Fact]
    public void EntradaInexistenteNaoERecuperada()
    {
        var result = _service.Restore(Guid.NewGuid(), overwriteConfirmed: false, CancellationToken.None);
        Assert.Equal(QuarantineRestoreStatus.EntryNotFound, result.Status);
    }

    [Fact]
    public void ExportacaoDeChaveGeraRegESnapshot()
    {
        _reader.Add(ResidueRegistryHive.LocalMachine, @"SOFTWARE\Exemplo App", new Dictionary<string, object>
        {
            ["Nome"] = "Texto",
            ["Contagem"] = 42,
        });
        _reader.Add(ResidueRegistryHive.LocalMachine, @"SOFTWARE\Exemplo App\Filho", new Dictionary<string, object>
        {
            ["Interno"] = "valor",
        });

        var entry = _service.ExportRegistryKey(
            ResidueRegistryHive.LocalMachine, @"SOFTWARE\Exemplo App", is64BitView: true,
            @"HKEY_LOCAL_MACHINE\SOFTWARE\Exemplo App", "Exemplo App", CancellationToken.None);

        Assert.True(File.Exists(entry.RegistryBackupFile));
        Assert.True(File.Exists(entry.QuarantinedPath));

        var reg = File.ReadAllText(entry.RegistryBackupFile!);
        Assert.StartsWith("Windows Registry Editor Version 5.00", reg);
        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\Exemplo App]", reg);
        Assert.Contains("\"Nome\"=\"Texto\"", reg);
        Assert.Contains("\"Contagem\"=dword:0000002a", reg);
        Assert.Contains(@"[HKEY_LOCAL_MACHINE\SOFTWARE\Exemplo App\Filho]", reg);
        Assert.Contains("\"Interno\"=\"valor\"", reg);

        Assert.Contains(_service.GetEntries(), e => e.Id == entry.Id);
    }

    [Fact]
    public void ExportacaoDeChaveInexistenteFalha()
    {
        Assert.Throws<InvalidOperationException>(() =>
            _service.ExportRegistryKey(ResidueRegistryHive.LocalMachine, @"SOFTWARE\NaoExiste_7712", true,
                @"HKEY_LOCAL_MACHINE\SOFTWARE\NaoExiste_7712", "X", CancellationToken.None));
    }

    [Fact]
    public void RecuperacaoSupervisionadaDeChaveRecriaValores()
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App", new Dictionary<string, object>
        {
            ["Nome"] = "Texto",
            ["Contagem"] = 42,
        });
        var entry = _service.ExportRegistryKey(
            ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App", is64BitView: true,
            @"HKEY_CURRENT_USER\SOFTWARE\Exemplo App", "Exemplo App", CancellationToken.None);
        _reader.Remove(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App");

        var result = _service.Restore(entry.Id, overwriteConfirmed: false, CancellationToken.None);

        Assert.Equal(QuarantineRestoreStatus.Restored, result.Status);
        Assert.Contains(_writer.Operations, o => o.Contains("CREATE CurrentUser\\SOFTWARE\\Exemplo App"));
        Assert.Contains(_writer.Operations, o => o.Contains(@"SET CurrentUser\SOFTWARE\Exemplo App\Nome=Texto"));
        Assert.Contains(_writer.Operations, o => o.Contains(@"SET CurrentUser\SOFTWARE\Exemplo App\Contagem=42"));
    }

    [Fact]
    public void RecuperacaoSupervisionadaComFalhaDeEscorreReportaParcial()
    {
        _reader.Add(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App", new Dictionary<string, object>
        {
            ["Nome"] = "Texto",
        });
        var entry = _service.ExportRegistryKey(
            ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App", is64BitView: true,
            @"HKEY_CURRENT_USER\SOFTWARE\Exemplo App", "Exemplo App", CancellationToken.None);
        _reader.Remove(ResidueRegistryHive.CurrentUser, @"SOFTWARE\Exemplo App");
        _writer.FailNextOperations = true;

        var result = _service.Restore(entry.Id, overwriteConfirmed: false, CancellationToken.None);

        Assert.Equal(QuarantineRestoreStatus.Failed, result.Status);
        Assert.Contains("parcial", result.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(entry.RegistryBackupFile!, result.Message);
    }

    [Fact]
    public void RegistroSinteticoReconciliaRestauracaoIntegralEPreservaConflitoSemEscreverNovamente()
    {
        const string key = @"SOFTWARE\Exemplo App";
        const string fullName = @"HKEY_CURRENT_USER\SOFTWARE\Exemplo App";
        const ResidueRegistryHive hive = ResidueRegistryHive.CurrentUser;
        var original = new Dictionary<string, object> { ["Valor"] = new byte[] { 0, 2, 255 } };
        _reader.Add(hive, key, original);
        var entry = _service.ExportRegistryKey(hive, key, true, fullName, "Fixture", CancellationToken.None);
        _reader.Remove(hive, key);
        _service.AfterRestoreForTest = () => throw new IOException("Synthetic journal interruption.");
        Assert.Equal(QuarantineRestoreStatus.Failed, _service.Restore(entry.Id, false, CancellationToken.None).Status);
        var writes = _writer.Operations.Count;
        var reopened = new ResidueQuarantineService(_quarantineRoot, _reader, _writer, _env.Rules);
        _reader.Add(hive, key, new Dictionary<string, object> { ["Valor"] = new byte[] { 3 } });
        Assert.Equal(QuarantineRestoreStatus.ConflictDestinationExists, reopened.Restore(entry.Id, true, CancellationToken.None).Status);
        Assert.Equal(writes, _writer.Operations.Count);
        _reader.Remove(hive, key);
        _reader.Add(hive, key, original);
        Assert.Equal(QuarantineRestoreStatus.Restored, reopened.Restore(entry.Id, false, CancellationToken.None).Status);
        Assert.Equal(writes, _writer.Operations.Count);
        Assert.Empty(reopened.GetEntries());
    }

    public void Dispose() => _env.Dispose();
}

[AttributeUsage(AttributeTargets.Method)]
internal sealed class CrossVolumeFactAttribute : FactAttribute
{
    internal static string? SecondVolume => Environment.GetEnvironmentVariable("WINCORE_QUARANTINE_SECOND_VOLUME");

    public CrossVolumeFactAttribute()
    {
        if (SecondVolume is not { } target || !AreSeparateVolumes(Path.GetTempPath(), target))
            Skip = "Cross-volume integration requires TEMP and WINCORE_QUARANTINE_SECOND_VOLUME on distinct local NTFS volumes.";
    }

    internal static bool AreSeparateVolumes(string source, string target)
    {
        if (!Path.IsPathFullyQualified(target) || !Directory.Exists(target)) return false;
        try
        {
            return ResidueQuarantineService.RequiresCrossVolume(source, target) &&
                new DriveInfo(Path.GetPathRoot(source)!).DriveFormat == "NTFS" &&
                new DriveInfo(Path.GetPathRoot(target)!).DriveFormat == "NTFS" &&
                ResidueFileGuard.GetVolumeSerial(source) != ResidueFileGuard.GetVolumeSerial(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return false; }
    }
}
