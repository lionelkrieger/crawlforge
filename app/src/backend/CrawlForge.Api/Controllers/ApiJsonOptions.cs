using System.Text.Json;
using System.Text.Json.Serialization;
using CrawlForge.Contracts.Common;
using CrawlForge.Contracts.Enums;

namespace CrawlForge.Api.Controllers;

/// <summary>
/// Uniform JSON conventions of the loopback REST surface (FS-CRAWL-001 §10.1):
/// camelCase payloads, lowercase string enum values, and the frozen frontend
/// wire shapes — envelope <c>data</c> always present, <c>pagination</c> and
/// <c>error</c> omitted when null, pagination <c>total</c>, and field errors as
/// a field→reasons map. Locked C# DTOs are unchanged; converters bridge the wire.
/// </summary>
public static class ApiJsonOptions
{
    /// <summary>Shared serializer options for every REST response and request body.</summary>
    public static JsonSerializerOptions Default { get; } = Create();

    /// <summary>Creates a fresh options instance with the API conventions.</summary>
    /// <returns>Configured serializer options.</returns>
    public static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        };
        options.Converters.Add(new ApiEnumConverterFactory());
        options.Converters.Add(new ApiEnvelopeConverterFactory());
        options.Converters.Add(new PaginationMetadataConverter());
        options.Converters.Add(new ApiErrorConverter());
        return options;
    }
}

/// <summary>
/// Serializes every enum as its lowercase name (parsing case-insensitively),
/// with the locked exception <see cref="ScheduleTriggerType.CatchUp"/> ↔ "catch-up".
/// </summary>
public sealed class ApiEnumConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) => typeToConvert.IsEnum;

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var converterType = typeof(ApiEnumConverter<>).MakeGenericType(typeToConvert);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

/// <summary>Lowercase string enum converter with the locked catch-up wire value.</summary>
/// <typeparam name="TEnum">Enum type handled by this converter.</typeparam>
public sealed class ApiEnumConverter<TEnum> : JsonConverter<TEnum>
    where TEnum : struct, Enum
{
    private static readonly Dictionary<string, TEnum> ReadMap = BuildReadMap();
    private static readonly Dictionary<TEnum, string> WriteMap = BuildWriteMap();

    /// <inheritdoc />
    public override TEnum Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
        {
            return (TEnum)Enum.ToObject(typeof(TEnum), reader.GetInt32());
        }

        var text = reader.GetString();
        if (text is not null && ReadMap.TryGetValue(text, out var value))
        {
            return value;
        }

        throw new JsonException($"Unknown {typeof(TEnum).Name} value '{text}'.");
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, TEnum value, JsonSerializerOptions options) =>
        writer.WriteStringValue(WriteMap[value]);

    /// <summary>Parses a wire string into the enum (query-string binding).</summary>
    /// <param name="text">Wire value; case-insensitive.</param>
    /// <param name="value">Parsed enum value when successful.</param>
    /// <returns>True when the text is a known wire value.</returns>
    public static bool TryParse(string? text, out TEnum value)
    {
        value = default;
        return text is not null && ReadMap.TryGetValue(text, out value);
    }

    /// <summary>Returns the wire string of an enum value.</summary>
    /// <param name="value">Enum value.</param>
    /// <returns>Lowercase wire string.</returns>
    public static string ToWireValue(TEnum value) => WriteMap[value];

    private static Dictionary<string, TEnum> BuildReadMap()
    {
        var map = new Dictionary<string, TEnum>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in Enum.GetValues<TEnum>())
        {
            map[WireName(value)] = value;
        }

        return map;
    }

    private static Dictionary<TEnum, string> BuildWriteMap()
    {
        var map = new Dictionary<TEnum, string>();
        foreach (var value in Enum.GetValues<TEnum>())
        {
            map[value] = WireName(value);
        }

        return map;
    }

    private static string WireName(TEnum value)
    {
        if (typeof(TEnum) == typeof(ScheduleTriggerType) && value.Equals(ScheduleTriggerType.CatchUp))
        {
            return "catch-up";
        }

        return value.ToString().ToLowerInvariant();
    }
}

/// <summary>
/// Envelope wire converter: <c>data</c> is always written (even when null);
/// <c>pagination</c> and <c>error</c> are omitted when null, matching the frozen
/// frontend envelope guard.
/// </summary>
public sealed class ApiEnvelopeConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert) =>
        typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(ApiEnvelope<>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var payloadType = typeToConvert.GetGenericArguments()[0];
        var converterType = typeof(ApiEnvelopeConverter<>).MakeGenericType(payloadType);
        return (JsonConverter)Activator.CreateInstance(converterType)!;
    }
}

/// <summary>Envelope converter for one payload type.</summary>
/// <typeparam name="T">Payload type.</typeparam>
public sealed class ApiEnvelopeConverter<T> : JsonConverter<ApiEnvelope<T>>
{
    /// <inheritdoc />
    public override ApiEnvelope<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;

        var data = root.TryGetProperty("data", out var dataElement) && dataElement.ValueKind != JsonValueKind.Null
            ? dataElement.Deserialize<T>(options)
            : default;
        var pagination = root.TryGetProperty("pagination", out var paginationElement) && paginationElement.ValueKind == JsonValueKind.Object
            ? paginationElement.Deserialize<PaginationMetadata>(options)
            : null;
        var error = root.TryGetProperty("error", out var errorElement) && errorElement.ValueKind == JsonValueKind.Object
            ? errorElement.Deserialize<ApiError>(options)
            : null;

        return new ApiEnvelope<T>(data, pagination, error);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ApiEnvelope<T> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WritePropertyName("data");
        JsonSerializer.Serialize(writer, value.Data, options);

        if (value.Pagination is not null)
        {
            writer.WritePropertyName("pagination");
            JsonSerializer.Serialize(writer, value.Pagination, options);
        }

        if (value.Error is not null)
        {
            writer.WritePropertyName("error");
            JsonSerializer.Serialize(writer, value.Error, options);
        }

        writer.WriteEndObject();
    }
}

/// <summary>
/// Pagination wire converter: the frozen frontend meta carries <c>total</c>;
/// the locked C# member is <c>TotalCount</c>. Reads accept both spellings.
/// </summary>
public sealed class PaginationMetadataConverter : JsonConverter<PaginationMetadata>
{
    /// <inheritdoc />
    public override PaginationMetadata Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var offset = root.TryGetProperty("offset", out var offsetElement) ? offsetElement.GetInt32() : 0;
        var limit = root.TryGetProperty("limit", out var limitElement) ? limitElement.GetInt32() : 50;
        var total = root.TryGetProperty("total", out var totalElement)
            ? totalElement.GetInt64()
            : root.TryGetProperty("totalCount", out var totalCountElement) ? totalCountElement.GetInt64() : 0;
        return new PaginationMetadata(offset, limit, total);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PaginationMetadata value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("offset", value.Offset);
        writer.WriteNumber("limit", value.Limit);
        writer.WriteNumber("total", value.TotalCount);
        writer.WriteEndObject();
    }
}

/// <summary>
/// Error wire converter: the frozen frontend guard expects <c>fieldErrors</c> as
/// a field→reasons map; the locked C# member is a list of field/reason pairs.
/// Reads accept both shapes.
/// </summary>
public sealed class ApiErrorConverter : JsonConverter<ApiError>
{
    /// <inheritdoc />
    public override ApiError Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() ?? string.Empty : string.Empty;
        var message = root.TryGetProperty("message", out var messageElement) ? messageElement.GetString() ?? string.Empty : string.Empty;

        List<ApiFieldError>? fieldErrors = null;
        if (root.TryGetProperty("fieldErrors", out var fieldElement))
        {
            fieldErrors = [];
            if (fieldElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in fieldElement.EnumerateObject())
                {
                    foreach (var reason in property.Value.EnumerateArray())
                    {
                        fieldErrors.Add(new ApiFieldError(property.Name, reason.GetString() ?? string.Empty));
                    }
                }
            }
            else if (fieldElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in fieldElement.EnumerateArray())
                {
                    var field = item.TryGetProperty("field", out var f) ? f.GetString() ?? string.Empty : string.Empty;
                    var reason = item.TryGetProperty("reason", out var r) ? r.GetString() ?? string.Empty : string.Empty;
                    fieldErrors.Add(new ApiFieldError(field, reason));
                }
            }
        }

        return new ApiError(code, message, fieldErrors);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, ApiError value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("code", value.Code);
        writer.WriteString("message", value.Message);

        if (value.FieldErrors is { Count: > 0 })
        {
            writer.WritePropertyName("fieldErrors");
            writer.WriteStartObject();
            foreach (var group in value.FieldErrors.GroupBy(e => e.Field, StringComparer.Ordinal))
            {
                writer.WritePropertyName(group.Key);
                writer.WriteStartArray();
                foreach (var entry in group)
                {
                    writer.WriteStringValue(entry.Reason);
                }

                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }
}
