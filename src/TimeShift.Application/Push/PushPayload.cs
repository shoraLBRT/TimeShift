using System.Text.Json;
using System.Text.Json.Serialization;
using TimeShift.Application.Conversations;

namespace TimeShift.Application.Push;

public sealed record PushAction(string Action, string Title);

/// <summary>Что получает service worker (docs/web-api.md, Push). <c>Actions</c> — только у пинга во время занятия.</summary>
public sealed record PushPayload(
    string Kind,
    string Title,
    string Body,
    string Tag,
    string Url,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<PushAction>? Actions)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Пинг устаревает быстро: «чем занят?» через час уже не нужно.</summary>
    public static readonly TimeSpan PingTimeToLive = TimeSpan.FromHours(1);

    public static readonly TimeSpan EveningTimeToLive = TimeSpan.FromHours(3);

    public static readonly TimeSpan TestTimeToLive = TimeSpan.FromMinutes(5);

    public static PushPayload Test { get; } = new("test", "TimeShift", "Уведомления работают", "ts-test", "/", null);

    public TimeSpan TimeToLive => Kind switch
    {
        "ping" => PingTimeToLive,
        "evening" => EveningTimeToLive,
        _ => TestTimeToLive,
    };

    /// <summary>Ответ бота → уведомление; <c>null</c> — это не уведомление (обычное сообщение).</summary>
    public static PushPayload? From(BotReply reply)
    {
        ArgumentNullException.ThrowIfNull(reply);
        return reply.Notice switch
        {
            { Kind: NoticeKind.Ping } n => new PushPayload(
                "ping", n.Title, n.Body, "ts-ping", "/?from=ping",
                n.CanAct ? [new PushAction("continue", "Продолжаю"), new PushAction("stop", "Закончил")] : null),
            { Kind: NoticeKind.Evening } n => new PushPayload("evening", n.Title, Shorten(n.Body), "ts-evening", "/?from=evening", null),
            _ => null,
        };
    }

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..159] + "…";
}
