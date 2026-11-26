using WinCoreManager.Models;
using WinCoreManager.Services;

namespace WinCoreManager.Tests;

/// <summary>Fake de ISettingsService apontando para um diretório temporário.</summary>
public sealed class TestSettings : ISettingsService
{
    public TestSettings()
    {
        DataDirectory = Path.Combine(Path.GetTempPath(), "WinCoreManagerTests", Guid.NewGuid().ToString("N"));
        HistoryDatabasePath = Path.Combine(DataDirectory, "history.db");
        Settings = new AppSettings();
    }

    public AppSettings Settings { get; }

    public string DataDirectory { get; }

    public string HistoryDatabasePath { get; }

    public void Save()
    {
    }
}
