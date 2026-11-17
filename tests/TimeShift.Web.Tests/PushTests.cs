using System.Buffers.Text;
using System.Net;
using System.Text.Json.Nodes;

namespace TimeShift.Web.Tests;

public class PushTests
{
    private const string Endpoint = "https://push.example.test/send/abc";

    private static object Subscription(string endpoint = Endpoint, string p256dh = "key-1", string auth = "auth-1") =>
        new { endpoint, keys = new { p256dh, auth } };

    [Fact]
    public async Task Subscribing_is_idempotent_by_endpoint_and_renews_the_keys()
    {
        await using var app = new TestApp();

        Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsync("/api/push/subscriptions", Subscription())).Status);
        Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsync("/api/push/subscriptions", Subscription(p256dh: "key-2", auth: "auth-2"))).Status);

        var info = (await app.GetAsync("/api/push")).Json;
        Assert.True(info["enabled"]!.GetValue<bool>());
        Assert.Equal(1, info["subscriptions"]!.GetValue<int>());

        await app.PostAsync("/api/push/test");
        var sent = Assert.Single(app.Push.Sent).Request;
        Assert.Equal(Endpoint, sent.Endpoint);
        Assert.Equal("key-2", sent.P256dh);
        Assert.Equal("auth-2", sent.Auth);
    }

    [Fact]
    public async Task Test_without_subscriptions_is_409_and_with_one_sends_the_test_payload()
    {
        await using var app = new TestApp();

        Assert.Equal(HttpStatusCode.Conflict, (await app.PostAsync("/api/push/test")).Status);
        Assert.Empty(app.Push.Sent);

        await app.PostAsync("/api/push/subscriptions", Subscription());
        Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsync("/api/push/test")).Status);

        var (request, keys) = Assert.Single(app.Push.Sent);
        var payload = JsonNode.Parse(request.Payload)!;
        Assert.Equal("test", payload["kind"]!.GetValue<string>());
        Assert.Equal("TimeShift", payload["title"]!.GetValue<string>());
        Assert.Equal("Уведомления работают", payload["body"]!.GetValue<string>());
        Assert.Equal("ts-test", payload["tag"]!.GetValue<string>());
        Assert.Null(payload["actions"]);
        Assert.Equal("ts-test", request.Topic);
        Assert.Equal("mailto:owner@localhost", keys.Subject);
    }

    [Fact]
    public async Task A_gone_subscription_is_deleted_and_the_others_still_get_the_message()
    {
        await using var app = new TestApp();
        await app.PostAsync("/api/push/subscriptions", Subscription("https://push.example.test/gone"));
        await app.PostAsync("/api/push/subscriptions", Subscription("https://push.example.test/failing"));
        await app.PostAsync("/api/push/subscriptions", Subscription("https://push.example.test/ok"));
        app.Push.GoneEndpoints.Add("https://push.example.test/gone");
        app.Push.FailingEndpoints.Add("https://push.example.test/failing");

        Assert.Equal(HttpStatusCode.NoContent, (await app.PostAsync("/api/push/test")).Status);

        Assert.Equal("https://push.example.test/ok", Assert.Single(app.Push.Sent, s => !app.Push.GoneEndpoints.Contains(s.Request.Endpoint)).Request.Endpoint);
        // 410 убрала подписку, сбой другого устройства оставил её.
        Assert.Equal(2, (await app.GetAsync("/api/push")).Json["subscriptions"]!.GetValue<int>());
    }

    [Fact]
    public async Task Unsubscribing_removes_the_device_and_is_idempotent()
    {
        await using var app = new TestApp();
        await app.PostAsync("/api/push/subscriptions", Subscription());

        var first = await app.SendAsync(HttpMethod.Delete, "/api/push/subscriptions", new { endpoint = Endpoint });
        var second = await app.SendAsync(HttpMethod.Delete, "/api/push/subscriptions", new { endpoint = Endpoint });

        Assert.Equal(HttpStatusCode.NoContent, first.Status);
        Assert.Equal(HttpStatusCode.NoContent, second.Status);
        Assert.Equal(0, (await app.GetAsync("/api/push")).Json["subscriptions"]!.GetValue<int>());
    }

    [Fact]
    public async Task Bad_subscriptions_are_rejected_with_the_field_name()
    {
        await using var app = new TestApp();

        var http = await app.PostAsync("/api/push/subscriptions", Subscription("http://push.example.test/x"));
        var noKeys = await app.PostAsync("/api/push/subscriptions", new { endpoint = Endpoint });
        var none = await app.PostAsync("/api/push/subscriptions", new { });

        Assert.Equal("endpoint", http.Json["field"]!.GetValue<string>());
        Assert.Equal("keys", noKeys.Json["field"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, (await app.GetAsync("/api/push", authenticated: false)).Status);
    }

    [Fact]
    public async Task Vapid_keys_are_generated_once_and_survive_a_restart()
    {
        var db = Path.Combine(Path.GetTempPath(), $"timeshift-web-{Guid.NewGuid():N}.db");
        string first;
        await using (var app = new TestApp(dbPath: db, deleteDb: false))
        {
            first = (await app.GetAsync("/api/push")).Json["publicKey"]!.GetValue<string>();
            Assert.Equal(first, (await app.GetAsync("/api/meta", authenticated: false)).Json["push"]!["publicKey"]!.GetValue<string>());
            var point = Base64Url.DecodeFromChars(first);
            Assert.Equal(65, point.Length);
            Assert.Equal(0x04, point[0]);
        }

        await using (var again = new TestApp(dbPath: db))
        {
            Assert.Equal(first, (await again.GetAsync("/api/push")).Json["publicKey"]!.GetValue<string>());
            await again.PostAsync("/api/push/subscriptions", Subscription());
            await again.PostAsync("/api/push/test");
            var keys = Assert.Single(again.Push.Sent).Keys;
            Assert.Equal(first, keys.PublicKey);
            Assert.Equal(32, Base64Url.DecodeFromChars(keys.PrivateKey).Length);
        }
    }

    [Fact]
    public async Task Configured_keys_and_subject_win_over_generated_ones()
    {
        await using var app = new TestApp(extra: new Dictionary<string, string?>
        {
            ["Push:PublicKey"] = "configured-public",
            ["Push:PrivateKey"] = "configured-private",
            ["Push:Subject"] = "mailto:me@example.test",
        });
        await app.PostAsync("/api/push/subscriptions", Subscription());
        await app.PostAsync("/api/push/test");

        Assert.Equal("configured-public", (await app.GetAsync("/api/push")).Json["publicKey"]!.GetValue<string>());
        var keys = Assert.Single(app.Push.Sent).Keys;
        Assert.Equal(("configured-private", "mailto:me@example.test"), (keys.PrivateKey, keys.Subject));
    }
}
