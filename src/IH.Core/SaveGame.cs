using System.Text.Json;

namespace IH.Core;

public static class SaveGame
{
    public static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true, IgnoreReadOnlyProperties = true, WriteIndented = false,
    };

    public static string ToJson<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T FromJson<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("empty save file");
}
