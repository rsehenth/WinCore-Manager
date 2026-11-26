using WinCoreManager.Models;
using WinCoreManager.Services;
using Microsoft.Data.Sqlite;

namespace WinCoreManager.Tests;

public class HistoryServiceTests : IDisposable
{
    private readonly TestSettings _settings = new();
    private readonly HistoryService _history;

    public HistoryServiceTests()
    {
        _history = new HistoryService(_settings);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(47)]
    [InlineData(4096)]
    public async Task BancoTruncadoPermaneceIntactoEHistoricoInformaOCaminho(int size)
    {
        Directory.CreateDirectory(_settings.DataDirectory);
        var bytes = Enumerable.Repeat((byte)0x5a, size).ToArray();
        File.WriteAllBytes(_settings.HistoryDatabasePath, bytes);
        var exception = await Assert.ThrowsAsync<HistoryStorageException>(() => _history.RecentAsync(5));
        Assert.Contains(_settings.HistoryDatabasePath, exception.Message);
        await Assert.ThrowsAsync<HistoryStorageException>(() => _history.AddAsync(new HistoryRecord()));
        Assert.Equal(bytes, File.ReadAllBytes(_settings.HistoryDatabasePath));
        Assert.False(File.Exists(_settings.HistoryDatabasePath + "-wal"));
    }

    [Fact]
    public async Task SchemaLegadoMigraPreservandoIdDadosEColunasDesconhecidas()
    {
        using (var seed = OpenFixture())
        {
            using var command = seed.CreateCommand();
            command.CommandText = """
                CREATE TABLE history(id INTEGER PRIMARY KEY, timestamp TEXT NOT NULL, type INTEGER NOT NULL,
                    target TEXT NOT NULL, status INTEGER NOT NULL, legacy_note TEXT);
                INSERT INTO history VALUES (42,'2020-01-03T13:45:10.0000000+00:00',0,'Inicializar',0,'legado');
                """;
            command.ExecuteNonQuery();
        }

        var record = Assert.Single(await _history.RecentAsync(5));
        Assert.Equal(42, record.Id);
        Assert.Equal("Inicializar", record.Target);
        Assert.Empty(record.Message);
        await _history.AddAsync(new HistoryRecord { Target = "outro" });
        Assert.Equal(2, (await new HistoryService(_settings).RecentAsync(5)).Count);
        using var check = OpenFixture();
        using var query = check.CreateCommand();
        query.CommandText = "SELECT legacy_note FROM history WHERE id=42;";
        Assert.Equal("legado", query.ExecuteScalar());
        query.CommandText = "PRAGMA user_version;";
        Assert.Equal(1L, query.ExecuteScalar());
    }

    [Fact]
    public async Task SchemaFuturoFicaIntactoSemReinicializacao()
    {
        using (var seed = OpenFixture())
        {
            using var command = seed.CreateCommand();
            command.CommandText = "CREATE TABLE future_only(payload TEXT); INSERT INTO future_only VALUES ('fixture'); PRAGMA user_version=7;";
            command.ExecuteNonQuery();
        }
        var bytes = File.ReadAllBytes(_settings.HistoryDatabasePath);
        await Assert.ThrowsAsync<HistoryStorageException>(() => _history.AddAsync(new HistoryRecord()));
        Assert.Equal(bytes, File.ReadAllBytes(_settings.HistoryDatabasePath));
    }

    [Fact]
    public async Task IndiceDerivadoInconsistenteEReconstruidoPreservandoLinhas()
    {
        using (var seed = OpenFixture())
        {
            using var command = seed.CreateCommand();
            command.CommandText = """
                CREATE TABLE history(id INTEGER PRIMARY KEY, timestamp TEXT NOT NULL, type INTEGER NOT NULL,
                    target TEXT NOT NULL, status INTEGER NOT NULL);
                INSERT INTO history VALUES (42,'2020-01-03T13:45:10.0000000+00:00',0,'fixture',0);
                CREATE INDEX ix_history_utc_timestamp ON history(id);
                PRAGMA writable_schema=ON;
                UPDATE sqlite_schema
                SET sql = 'CREATE INDEX ix_history_utc_timestamp ON history (timestamp DESC, id DESC)'
                WHERE name = 'ix_history_utc_timestamp';
                PRAGMA writable_schema=OFF;
                """;
            command.ExecuteNonQuery();
        }
        using (var before = OpenFixture())
        {
            using var check = before.CreateCommand();
            check.CommandText = "PRAGMA integrity_check(1);";
            Assert.NotEqual("ok", check.ExecuteScalar());
        }
        Assert.Equal(42, Assert.Single(await _history.RecentAsync(5)).Id);
        using var after = OpenFixture();
        using var query = after.CreateCommand();
        query.CommandText = "PRAGMA integrity_check(1);";
        Assert.Equal("ok", query.ExecuteScalar());
    }

    [Fact]
    public async Task ReinicioRecuperaCommitDoWalSemArquivoShm()
    {
        using (var seed = OpenFixture())
        {
            using var command = seed.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                CREATE TABLE history(id INTEGER PRIMARY KEY, timestamp TEXT NOT NULL, type INTEGER NOT NULL,
                    target TEXT NOT NULL, status INTEGER NOT NULL);
                PRAGMA wal_checkpoint(TRUNCATE);
                INSERT INTO history VALUES (73,'2020-01-03T13:45:10.0000000+00:00',0,'persistido no WAL',0);
                """;
            command.ExecuteNonQuery();
            var restart = new TestSettings();
            try
            {
                Directory.CreateDirectory(restart.DataDirectory);
                File.Copy(_settings.HistoryDatabasePath, restart.HistoryDatabasePath);
                File.Copy(_settings.HistoryDatabasePath + "-wal", restart.HistoryDatabasePath + "-wal");
                Assert.False(File.Exists(restart.HistoryDatabasePath + "-shm"));
                var record = Assert.Single(await new HistoryService(restart).RecentAsync(5));
                Assert.Equal(73, record.Id);
                Assert.Equal("persistido no WAL", record.Target);
            }
            finally
            {
                using var pool = new SqliteConnection(new SqliteConnectionStringBuilder
                {
                    DataSource = restart.HistoryDatabasePath, Mode = SqliteOpenMode.ReadWriteCreate, DefaultTimeout = 5,
                }.ToString());
                SqliteConnection.ClearPool(pool);
                Directory.Delete(restart.DataDirectory, recursive: true);
            }
        }
    }

    private SqliteConnection OpenFixture()
    {
        Directory.CreateDirectory(_settings.DataDirectory);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = _settings.HistoryDatabasePath,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_settings.DataDirectory))
            {
                Directory.Delete(_settings.DataDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public async Task SQLiteMantemWALERetornaLinhasValidasComDataInvalidaNoBanco()
    {
        await _history.AddAsync(new HistoryRecord { Target = "Válido" });
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = _settings.HistoryDatabasePath }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        Assert.Equal("wal", command.ExecuteScalar());
        command.CommandText = "INSERT INTO history (timestamp, type, target, status) VALUES ('invalid', 0, 'fixture', 0);";
        command.ExecuteNonQuery();
        Assert.Equal("Válido", Assert.Single(await _history.RecentAsync(5)).Target);
    }

    [Fact]
    public async Task Registro_inserido_é_lido_com_todos_os_campos()
    {
        await _history.AddAsync(new HistoryRecord
        {
            Type = HistoryType.Update,
            Target = "App Exemplo",
            Details = "Id.Exemplo — origem: winget",
            Status = HistoryStatus.Success,
            ExitCode = 0,
            Message = "Atualizado com sucesso.",
            DurationMs = 42_500,
            VersionFrom = "1.0",
            VersionTo = "2.0",
        });

        var records = await _history.QueryAsync(new HistoryFilter());

        var record = Assert.Single(records);
        Assert.Equal("App Exemplo", record.Target);
        Assert.Equal(HistoryType.Update, record.Type);
        Assert.Equal(HistoryStatus.Success, record.Status);
        Assert.Equal(0, record.ExitCode);
        Assert.Equal(42_500, record.DurationMs);
        Assert.Equal("1.0", record.VersionFrom);
        Assert.Equal("2.0", record.VersionTo);
    }

    [Fact]
    public async Task Filtros_de_tipo_status_e_busca_funcionam()
    {
        await _history.AddAsync(new HistoryRecord { Type = HistoryType.Update, Target = "App Um", Status = HistoryStatus.Success });
        await _history.AddAsync(new HistoryRecord { Type = HistoryType.Maintenance, Target = "SFC Scannow", Status = HistoryStatus.Failure, Message = "código 3" });
        await _history.AddAsync(new HistoryRecord { Type = HistoryType.Maintenance, Target = "Flush DNS", Status = HistoryStatus.Success });

        var onlyMaintenance = await _history.QueryAsync(new HistoryFilter { Type = HistoryType.Maintenance });
        Assert.Equal(2, onlyMaintenance.Count);

        var onlyFailures = await _history.QueryAsync(new HistoryFilter { Status = HistoryStatus.Failure });
        Assert.Equal("SFC Scannow", Assert.Single(onlyFailures).Target);

        var bySearch = await _history.QueryAsync(new HistoryFilter { Search = "scannow" });
        Assert.Equal("SFC Scannow", Assert.Single(bySearch).Target);
    }

    [Fact]
    public async Task Recent_retorna_mais_recentes_primeiro()
    {
        await _history.AddAsync(new HistoryRecord { Type = HistoryType.Maintenance, Target = "Primeiro" });
        await Task.Delay(20);
        await _history.AddAsync(new HistoryRecord { Type = HistoryType.Maintenance, Target = "Segundo" });

        var recent = await _history.RecentAsync(1);

        var record = Assert.Single(recent);
        Assert.Equal("Segundo", record.Target);
    }

    [Fact]
    public async Task ClearRemove_todos_e_DeleteOlderThan_remove_apenas_antigos()
    {
        await _history.AddAsync(new HistoryRecord { Target = "A", Type = HistoryType.Update, Status = HistoryStatus.Success });
        await _history.AddAsync(new HistoryRecord { Target = "B", Type = HistoryType.Maintenance, Status = HistoryStatus.Success });

        var removed = await _history.DeleteOlderThanAsync(DateTimeOffset.Now.AddDays(1));
        Assert.Equal(2, removed);

        await _history.AddAsync(new HistoryRecord { Target = "C", Type = HistoryType.Update, Status = HistoryStatus.Success });
        var cleared = await _history.ClearAsync();
        Assert.Equal(1, cleared);

        var after = await _history.QueryAsync(new HistoryFilter());
        Assert.Empty(after);
    }

    [Fact]
    public async Task Ordenação_e_retenção_comparam_instantes_com_offsets_diferentes()
    {
        var older = new DateTimeOffset(2026, 1, 1, 12, 0, 0, TimeSpan.FromHours(3));
        var newer = new DateTimeOffset(2026, 1, 1, 8, 0, 0, TimeSpan.FromHours(-3));
        await _history.AddAsync(new HistoryRecord { Target = "Antigo", Timestamp = older });
        await _history.AddAsync(new HistoryRecord { Target = "Recente", Timestamp = newer });

        Assert.Equal("Recente", Assert.Single(await _history.RecentAsync(1)).Target);
        Assert.Equal(1, await _history.DeleteOlderThanAsync(new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Recente", Assert.Single(await _history.RecentAsync(5)).Target);
    }

    [Fact]
    public async Task Busca_trata_percentual_e_sublinhado_como_texto()
    {
        await _history.AddAsync(new HistoryRecord { Target = "App_100%" });
        await _history.AddAsync(new HistoryRecord { Target = "App X 100" });
        var result = await _history.QueryAsync(new HistoryFilter { Search = "_100%" });
        Assert.Equal("App_100%", Assert.Single(result).Target);
    }
}
