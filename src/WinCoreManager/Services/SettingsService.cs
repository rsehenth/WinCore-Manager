using System.IO;
using System.Text.Json;
using WinCoreManager.Localization;
using WinCoreManager.Models;

namespace WinCoreManager.Services;

public interface ISettingsService
{
    AppSettings Settings { get; }

    /// <summary>Diretório onde histórico e configurações vivem nesta sessão (resolvido na inicialização).</summary>
    string DataDirectory { get; }

    string HistoryDatabasePath { get; }

    void Save();
}

public sealed class SettingsService : ISettingsService
{
    private const string SettingsFileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
    };

    private readonly string _portableSettingsPath;
    private readonly string _localSettingsPath;
    private readonly IOException? _portableLoadError;
    private readonly IOException? _localLoadError;
    private bool _portableMarkerActive;

    public SettingsService() : this(AppContext.BaseDirectory,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WinCoreManager"))
    {
    }

    internal SettingsService(string executableDirectory, string localDataDirectory)
    {
        // Modo portátil: settings.json junto ao executável.
        _portableSettingsPath = Path.Combine(executableDirectory, SettingsFileName);
        _localSettingsPath = Path.Combine(localDataDirectory, SettingsFileName);
        var portableSettings = Load(_portableSettingsPath, defaultPortableMode: true, out _portableLoadError);
        var portable = portableSettings?.PortableMode == true;
        _portableMarkerActive = portable;
        Settings = portable ? portableSettings! : Load(_localSettingsPath, defaultPortableMode: false, out _localLoadError) ?? new AppSettings();
        Settings.PortableMode = portable;
        DataDirectory = portable ? Path.Combine(executableDirectory, "Data") : localDataDirectory;
        HistoryDatabasePath = Path.Combine(DataDirectory, "history.db");
    }

    public AppSettings Settings { get; }

    public string DataDirectory { get; }

    public string HistoryDatabasePath { get; }

    internal string? LoadProblem => (_portableLoadError ?? _localLoadError)?.Message;

    public void Save()
    {
        if (_portableLoadError is not null)
        {
            throw _portableLoadError;
        }
        if (!Settings.PortableMode)
        {
            if (_localLoadError is not null)
            {
                throw _localLoadError;
            }
            if (_portableMarkerActive)
            {
                _ = Load(_localSettingsPath, defaultPortableMode: false, out var targetError);
                if (targetError is not null) throw targetError;
            }
        }

        var targetPath = Settings.PortableMode ? _portableSettingsPath : _localSettingsPath;
        SaveAtomically(targetPath);
        if (!Settings.PortableMode && _portableMarkerActive)
        {
            // Disable this copy's portable marker after local preferences are safely written.
            SaveAtomically(_portableSettingsPath);
        }

        _portableMarkerActive = Settings.PortableMode;
    }

    private void SaveAtomically(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";

        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, Settings, JsonOptions);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            try
            {
                File.Delete(temporaryPath);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Keep the original save error if the temporary file could not be cleaned up.
            }
        }
    }

    private static AppSettings? Load(string path, bool defaultPortableMode, out IOException? loadError)
    {
        loadError = null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var loaded = new AppSettings { PortableMode = defaultPortableMode };
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
            if (root.TryGetProperty(nameof(AppSettings.PortableMode), out var mode)
                && mode.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                loaded.PortableMode = mode.GetBoolean();
            }

            if (root.TryGetProperty(nameof(AppSettings.HistoryRetentionDays), out var retention)
                && retention.ValueKind == JsonValueKind.Number && retention.TryGetInt32(out var days))
            {
                loaded.HistoryRetentionDays = Math.Clamp(days, 0, 3650);
            }
            if (root.TryGetProperty(nameof(AppSettings.WinGetInactivitySeconds), out var inactivity)
                && inactivity.ValueKind == JsonValueKind.Number && inactivity.TryGetInt32(out var seconds))
            {
                loaded.WinGetInactivitySeconds = Math.Clamp(seconds, 1, 600);
            }
            loaded.Theme = ReadString(root, nameof(AppSettings.Theme));
            loaded.Language = ReadString(root, nameof(AppSettings.Language));
            loaded.OfficialRepositoryUrl = ReadString(root, nameof(AppSettings.OfficialRepositoryUrl));
            if (root.TryGetProperty(nameof(AppSettings.UpdatesListRatio), out var ratio)
                && ratio.ValueKind == JsonValueKind.Number && ratio.TryGetDouble(out var weight)
                && double.IsFinite(weight) && weight > 0 && weight < 1)
            {
                loaded.UpdatesListRatio = weight;
            }
            if (root.TryGetProperty(nameof(AppSettings.HiddenUpdates), out var hidden)
                && hidden.ValueKind == JsonValueKind.Array)
            {
                var keys = new HashSet<(string Source, string Id)>();
                foreach (var entry in hidden.EnumerateArray())
                {
                    if (entry.ValueKind != JsonValueKind.Object) continue;
                    var id = ReadString(entry, nameof(HiddenUpdate.Id));
                    var source = ReadString(entry, nameof(HiddenUpdate.Source));
                    if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(source) || !keys.Add((source, id)))
                        continue;
                    var name = ReadString(entry, nameof(HiddenUpdate.Name));
                    loaded.HiddenUpdates.Add(new(string.IsNullOrWhiteSpace(name) ? id : name, id, source));
                }
            }
            return loaded;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            loadError = new IOException(LocalizationService.Source("Message.SettingsReadProblem", path), ex);
            return new AppSettings { PortableMode = defaultPortableMode, HistoryRetentionDays = 0 };
        }
    }

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
