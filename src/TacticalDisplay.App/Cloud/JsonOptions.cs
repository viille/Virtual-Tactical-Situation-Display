using System.Text.Json;
using System.Text.Json.Serialization;

namespace TacticalDisplay.App.Cloud;

public static class JsonOptions
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters =
        {
            new FlexibleStringConverter(),
            new LenientEnumConverterFactory(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };
}

public sealed class FlexibleStringConverter : JsonConverter<string>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => reader.TryGetInt64(out var integer)
                ? integer.ToString(System.Globalization.CultureInfo.InvariantCulture)
                : reader.GetDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
            JsonTokenType.True => bool.TrueString,
            JsonTokenType.False => bool.FalseString,
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Cannot convert {reader.TokenType} to string.")
        };

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}

public sealed class LenientEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options) =>
        (JsonConverter)Activator.CreateInstance(typeof(LenientEnumConverter<>).MakeGenericType(typeToConvert))!;
}

public sealed class LenientEnumConverter<TEnum> : JsonConverter<TEnum> where TEnum : struct, Enum
{
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                var normalized = Normalize(value);
                if (TryParseAlias(normalized, out var alias)) return alias;
                if (Enum.TryParse<TEnum>(normalized, ignoreCase: true, out var parsed)) return parsed;
            }

            return default;
        }

        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var numeric) &&
            Enum.IsDefined(typeof(TEnum), numeric))
            return (TEnum)Enum.ToObject(typeof(TEnum), numeric);

        return default;
    }

    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(JsonNamingPolicy.CamelCase.ConvertName(value.ToString()));

    private static string Normalize(string value) => value.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    private static bool TryParseAlias(string value, out TEnum result)
    {
        if (typeof(TEnum) == typeof(CollectionAccessSource))
        {
            if (value.Equals("owned", StringComparison.OrdinalIgnoreCase))
            {
                result = (TEnum)(object)CollectionAccessSource.Owner;
                return true;
            }

            if (value.Equals("org", StringComparison.OrdinalIgnoreCase) ||
                value.Equals("organisation", StringComparison.OrdinalIgnoreCase))
            {
                result = (TEnum)(object)CollectionAccessSource.Organization;
                return true;
            }
        }

        result = default;
        return false;
    }
}
