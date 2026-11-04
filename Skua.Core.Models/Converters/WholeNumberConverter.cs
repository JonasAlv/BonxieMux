using Newtonsoft.Json;

namespace Skua.Core.Models.Converters;

/// <summary>
/// Reads integer fields written as whole-number floats (for example, "123.0").
/// </summary>
public sealed class WholeNumberConverter : JsonConverter
{
    public override bool CanConvert(Type objectType)
    {
        Type type = Nullable.GetUnderlyingType(objectType) ?? objectType;
        return type == typeof(int) || type == typeof(long);
    }

    public override bool CanWrite => false;

    public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer) => throw new NotSupportedException();

    public override object? ReadJson(JsonReader reader, Type objectType, object? existingValue, JsonSerializer serializer)
    {
        if (reader.TokenType == JsonToken.Null || reader.Value is null or "")
            return Nullable.GetUnderlyingType(objectType) is null ? Activator.CreateInstance(objectType) : null;

        Type type = Nullable.GetUnderlyingType(objectType) ?? objectType;
        return Convert.ChangeType(reader.Value, type, System.Globalization.CultureInfo.InvariantCulture);
    }
}
