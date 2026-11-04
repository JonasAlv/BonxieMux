using Newtonsoft.Json;

namespace Skua.Core.Models.Converters;

public class IntConverter : JsonConverter<int>
{
    public override void WriteJson(JsonWriter writer, int value, JsonSerializer serializer)
    {
        writer.WriteValue(value);
    }

    public override int ReadJson(JsonReader reader, Type objectType, int existingValue, bool hasExistingValue, JsonSerializer serializer)
    {
        if (reader.Value is null or (object)"null")
            return 0;

        if (reader.Value is long l)
            return (int)l;

        if (reader.Value is int i)
            return i;

        return int.TryParse(reader.Value.ToString(), out int result) ? result : 0;
    }
}