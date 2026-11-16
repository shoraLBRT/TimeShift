using System.Text.Json.Nodes;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Conversations;
using TimeShift.Application.Notifications;
using TimeShift.Application.Push;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Activities;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Tests;

public class NotificationTests
{
    private sealed class FakeChannel(string key, string? messageRef, bool fails = false, bool reachable = true) : INotificationChannel
    {
        public List<BotReply> Sent { get; } = [];

        public List<string> Deleted { get; } = [];

        public string Key => key;

        public Task<bool> CanReachAsync(User user, CancellationToken cancellationToken) =>
            fails ? throw new InvalidOperationException("down") : Task.FromResult(reachable);

        public Task<string?> SendAsync(User user, BotReply reply, CancellationToken cancellationToken)
        {
            if (fails)
            {
                throw new InvalidOperationException("down");
            }

            Sent.Add(reply);
            return Task.FromResult(messageRef);
        }

        public Task DeleteAsync(User user, string messageRef, CancellationToken cancellationToken)
        {
            Deleted.Add(messageRef);
            return Task.CompletedTask;
        }
    }

    private sealed class IgnoreFailures : IFailureReporter
    {
        public void Report(Exception exception, string message)
        {
        }
    }

    private static readonly User Someone = new(1, "Тест", User.DefaultTimeZone, new DateTime(2026, 11, 14, 9, 0, 0, DateTimeKind.Utc));

    private static NotificationFanout Fanout(params INotificationChannel[] channels) => new(channels, new IgnoreFailures());

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Every_channel_gets_the_message_and_refs_are_tagged_by_channel()
    {
        var telegram = new FakeChannel("tg", "42");
        var push = new FakeChannel("wp", null);

        var messageRef = await Fanout(telegram, push).SendAsync(Someone, BotReply.Plain("пинг"), Ct);

        Assert.Single(telegram.Sent);
        Assert.Single(push.Sent);
        Assert.Equal("tg:42", messageRef);
    }

    [Fact]
    public async Task A_failing_channel_does_not_stop_the_others()
    {
        var broken = new FakeChannel("wp", null, fails: true);
        var telegram = new FakeChannel("tg", "7");

        var messageRef = await Fanout(broken, telegram).SendAsync(Someone, BotReply.Plain("пинг"), Ct);

        Assert.Equal("tg:7", messageRef);
        Assert.Single(telegram.Sent);
    }

    [Fact]
    public async Task Without_any_ref_nothing_is_stored_and_too_long_refs_are_dropped()
    {
        Assert.Null(await Fanout(new FakeChannel("wp", null)).SendAsync(Someone, BotReply.Plain("x"), Ct));
        Assert.Null(await Fanout(new FakeChannel("tg", new string('9', 80))).SendAsync(Someone, BotReply.Plain("x"), Ct));
    }

    [Fact]
    public async Task Delete_is_routed_by_the_channel_key_and_old_refs_go_to_telegram()
    {
        var telegram = new FakeChannel("tg", "42");
        var other = new FakeChannel("zz", "abc");
        var fanout = Fanout(telegram, other);

        await fanout.DeleteAsync(Someone, "tg:42,zz:abc", Ct);
        await fanout.DeleteAsync(Someone, "99", Ct);
        await fanout.DeleteAsync(Someone, "unknown:1", Ct);

        Assert.Equal(["42", "99"], telegram.Deleted);
        Assert.Equal(["abc"], other.Deleted);
    }

    [Fact]
    public async Task Can_reach_when_any_channel_can_and_failures_count_as_no()
    {
        Assert.True(await Fanout(new FakeChannel("a", null, reachable: false), new FakeChannel("b", null)).CanReachAsync(Someone, Ct));
        Assert.False(await Fanout(new FakeChannel("a", null, reachable: false), new FakeChannel("b", null, fails: true)).CanReachAsync(Someone, Ct));
        Assert.False(await Fanout().CanReachAsync(Someone, Ct));
    }

    [Fact]
    public void A_ping_during_an_activity_becomes_a_push_with_actions()
    {
        var now = new DateTime(2026, 11, 14, 10, 20, 0, DateTimeKind.Utc);
        var segment = new Segment(now - TimeSpan.FromMinutes(80), null, "Работа", SegmentStatus.Confirmed, SegmentSource.User);
        var reply = TrackingReplies.Ping(new CurrentActivity(segment, null), [], now, TimeZoneInfo.Utc);

        var payload = PushPayload.From(reply)!;

        Assert.Equal("ping", payload.Kind);
        Assert.Equal("Всё ещё «Работа»?", payload.Title);
        Assert.Equal("Идёт 1 ч 20 мин", payload.Body);
        Assert.Equal("ts-ping", payload.Tag);
        Assert.Equal("/?from=ping", payload.Url);
        Assert.Equal(["continue", "stop"], payload.Actions!.Select(a => a.Action));
        Assert.Equal(TimeSpan.FromHours(1), payload.TimeToLive);
        Assert.NotNull(JsonNode.Parse(payload.ToJson())!["actions"]);
    }

    [Fact]
    public void A_ping_with_nothing_running_has_no_actions()
    {
        var reply = TrackingReplies.Ping(null, [new Activity(Guid.NewGuid(), "Чтение")], DateTime.UtcNow, TimeZoneInfo.Utc);

        var payload = PushPayload.From(reply)!;

        Assert.Equal("Чем занят?", payload.Title);
        Assert.Equal("ts-ping", payload.Tag);
        Assert.Null(payload.Actions);
        Assert.Null(JsonNode.Parse(payload.ToJson())!["actions"]);
    }

    [Fact]
    public void The_evening_review_is_short_and_has_its_own_tag()
    {
        var reply = new BotReply("длинный разбор дня…", [])
        {
            Notice = new ClientNotice(NoticeKind.Evening, "Вечерний разбор", new string('я', 400)),
        };

        var payload = PushPayload.From(reply)!;

        Assert.Equal("evening", payload.Kind);
        Assert.Equal("ts-evening", payload.Tag);
        Assert.Equal("/?from=evening", payload.Url);
        Assert.True(payload.Body.Length <= 160);
        Assert.Null(payload.Actions);
    }

    [Fact]
    public void Plain_replies_are_not_notifications_and_the_test_payload_is_fixed()
    {
        Assert.Null(PushPayload.From(BotReply.Plain("обычное сообщение")));
        Assert.Equal(("TimeShift", "Уведомления работают", "ts-test"), (PushPayload.Test.Title, PushPayload.Test.Body, PushPayload.Test.Tag));
    }

    [Fact]
    public void Generated_vapid_keys_have_the_expected_shape()
    {
        var (publicKey, privateKey) = VapidKeyProvider.Generate();

        Assert.Equal(65, System.Buffers.Text.Base64Url.DecodeFromChars(publicKey).Length);
        Assert.Equal(32, System.Buffers.Text.Base64Url.DecodeFromChars(privateKey).Length);
        Assert.NotEqual(publicKey, VapidKeyProvider.Generate().PublicKey);
    }
}
