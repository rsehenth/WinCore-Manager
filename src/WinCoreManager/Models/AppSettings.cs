using System.Text.Json;
using System.Text.Json.Serialization;

namespace WinCoreManager.Models;

public sealed class AppSettings
{
    public int WinGetInactivitySeconds { get; set; } = 120;

    public bool PortableMode { get; set; }

    public int HistoryRetentionDays { get; set; } = 365;

    public string? OfficialRepositoryUrl { get; set; }

    /// <summary>Skin escolhida ("Dark" ou "Light"). Texto, não enum: um valor inválido no arquivo não pode descartar as outras preferências.</summary>
    public string? Theme { get; set; }

    [JsonConverter(typeof(LanguageSettingConverter))]
    public string? Language { get; set; }

    public List<HiddenUpdate> HiddenUpdates { get; set; } = [];
    public double? UpdatesListRatio { get; set; }
}

public sealed record HiddenUpdate(string Name, string Id, string Source);

internal sealed class LanguageSettingConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            return reader.GetString();
        }

        reader.Skip();
        return null;
    }

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value);
}
