using System.Globalization;
using Microsoft.AspNetCore.Http;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using TimeShift.Application.Clients;

namespace TimeShift.Web;

/// <summary>Неверный запрос: битый JSON или значение не того типа (<c>400 bad_request</c>).</summary>
internal sealed class BadRequestException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Единые настройки JSON: camelCase, перечисления строками в нижнем регистре, моменты UTC с <c>Z</c>, местное время
/// без смещения (<c>2026-11-17T14:30</c>), время суток <c>HH:mm</c>; <c>null</c> пишутся.
/// </summary>
public static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create() => new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new TimeOnlyConverter(), new DateTimeConverter() },
    };

    private sealed class TimeOnlyConverter : JsonConverter<TimeOnly>
    {
        private static readonly string[] Formats = ["HH:mm", "HH:mm:ss"];

        public override TimeOnly Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String
            && TimeOnly.TryParseExact(reader.GetString(), Formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                ? time
                : throw new JsonException("Время — в виде 09:30.");

        public override void Write(Utf8JsonWriter writer, TimeOnly value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value.ToString("HH:mm", CultureInfo.InvariantCulture));
    }

    /// <summary>UTC (<c>Z</c>) — момент, без <c>Z</c> — местное время пользователя (<c>DateTimeKind.Unspecified</c>).</summary>
    private sealed class DateTimeConverter : JsonConverter<DateTime>
    {
        private static readonly string[] LocalFormats = ["yyyy-MM-dd'T'HH:mm", "yyyy-MM-dd'T'HH:mm:ss"];

        private static readonly string[] UtcFormats = ["yyyy-MM-dd'T'HH:mm'Z'", "yyyy-MM-dd'T'HH:mm:ss'Z'", "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"];

        public override DateTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            var text = reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
            if (text is not null)
            {
                if (DateTime.TryParseExact(text, LocalFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
                {
                    return DateTime.SpecifyKind(local, DateTimeKind.Unspecified);
                }

                if (DateTime.TryParseExact(text, UtcFormats, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var utc))
                {
                    return utc;
                }
            }

            throw new JsonException("Время — в виде 2026-11-17T14:30.");
        }

        public override void Write(Utf8JsonWriter writer, DateTime value, JsonSerializerOptions options)
        {
            if (value.Kind == DateTimeKind.Unspecified)
            {
                writer.WriteStringValue(value.ToString("yyyy-MM-dd'T'HH:mm", CultureInfo.InvariantCulture));
                return;
            }

            writer.WriteStringValue(value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>Тело запроса как объект: поля читаются по именам, отсутствие и <c>null</c> различаются (для PATCH).</summary>
internal sealed class Fields(JsonObject body)
{
    public static readonly Fields Empty = new([]);

    public static async Task<Fields> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > 65536)
            {
                throw new BadRequestException("Слишком большой запрос.");
            }
        }

        if (buffer.Length == 0)
        {
            return Empty;
        }

        try
        {
            return new Fields(JsonSerializer.Deserialize<JsonObject>(buffer.ToArray(), ApiJson.Options)
                ?? throw new BadRequestException("Ожидается JSON-объект."));
        }
        catch (JsonException e)
        {
            throw new BadRequestException("Не получилось разобрать JSON.", e);
        }
    }

    public bool Has(string name) => body.ContainsKey(name);

    /// <summary>Значение поля; <c>null</c> — поля нет или оно <c>null</c>.</summary>
    public T? Get<T>(string name)
        where T : struct
    {
        try
        {
            return body[name] is { } node ? node.Deserialize<T>(ApiJson.Options) : null;
        }
        catch (JsonException e)
        {
            throw new BadRequestException($"Поле «{name}» неверного вида.", e);
        }
    }

    public string? Str(string name)
    {
        try
        {
            return body[name] is { } node ? node.Deserialize<string>(ApiJson.Options) : null;
        }
        catch (JsonException e)
        {
            throw new BadRequestException($"Поле «{name}» неверного вида.", e);
        }
    }

    public List<int>? Ints(string name)
    {
        try
        {
            return body[name] is { } node ? node.Deserialize<List<int>>(ApiJson.Options) : null;
        }
        catch (JsonException e)
        {
            throw new BadRequestException($"Поле «{name}» неверного вида.", e);
        }
    }

    public T Require<T>(string name)
        where T : struct =>
        Get<T>(name) ?? throw ClientException.Invalid(name, "Поле обязательно.");

    /// <summary>Поле PATCH: не передано / передано (в том числе <c>null</c>).</summary>
    public Optional<T?> Opt<T>(string name)
        where T : struct => body.ContainsKey(name) ? Optional<T?>.Of(Get<T>(name)) : Optional<T?>.Unset;

    public Optional<string?> OptStr(string name) => body.ContainsKey(name) ? Optional<string?>.Of(Str(name)) : Optional<string?>.Unset;

    public Fields? Object(string name)
    {
        try
        {
            return body[name] is JsonObject child ? new Fields(child) : body[name] is null ? null : throw new BadRequestException($"Поле «{name}» должно быть объектом.");
        }
        catch (InvalidOperationException e)
        {
            throw new BadRequestException($"Поле «{name}» неверного вида.", e);
        }
    }
}
