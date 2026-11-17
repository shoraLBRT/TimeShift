using System.Net;

namespace TimeShift.Web.Tests;

public class AuthTests
{
    [Fact]
    public async Task Unauthenticated_requests_get_401()
    {
        await using var app = new TestApp();

        var me = await app.GetAsync("/api/me", authenticated: false);
        var forged = await app.GetAsync("/api/me", authenticated: false);
        var tampered = await app.SendAsync(HttpMethod.Get, "/api/me", authenticated: false, cookie: "ts_session=9999999999.AAAA");

        Assert.Equal(HttpStatusCode.Unauthorized, me.Status);
        Assert.Equal("unauthorized", me.Error);
        Assert.Equal(HttpStatusCode.Unauthorized, forged.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, tampered.Status);
    }

    [Fact]
    public async Task Login_with_the_right_key_sets_a_session_cookie_that_opens_the_api()
    {
        await using var app = new TestApp();

        var login = await app.SendAsync(HttpMethod.Post, "/api/auth/login", new { key = TestApp.AccessKey }, authenticated: false);

        Assert.Equal(HttpStatusCode.NoContent, login.Status);
        var setCookie = Assert.Single(login.Raw.Headers.GetValues("Set-Cookie"));
        Assert.StartsWith("ts_session=", setCookie, StringComparison.Ordinal);
        Assert.Contains("httponly", setCookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=lax", setCookie, StringComparison.OrdinalIgnoreCase);

        var cookie = setCookie.Split(';')[0];
        var me = await app.SendAsync(HttpMethod.Get, "/api/me", authenticated: false, cookie: cookie);
        Assert.Equal(HttpStatusCode.OK, me.Status);
    }

    [Fact]
    public async Task Wrong_key_gets_401_and_the_sixth_attempt_in_a_minute_gets_429()
    {
        await using var app = new TestApp();

        for (var i = 0; i < 5; i++)
        {
            var wrong = await app.SendAsync(HttpMethod.Post, "/api/auth/login", new { key = "nope" }, authenticated: false);
            Assert.Equal(HttpStatusCode.Unauthorized, wrong.Status);
        }

        var sixth = await app.SendAsync(HttpMethod.Post, "/api/auth/login", new { key = TestApp.AccessKey }, authenticated: false);
        Assert.Equal(HttpStatusCode.TooManyRequests, sixth.Status);
        Assert.Equal("too_many_requests", sixth.Error);
    }

    [Fact]
    public async Task Mutating_request_without_the_csrf_header_gets_403()
    {
        await using var app = new TestApp();

        var login = await app.SendAsync(HttpMethod.Post, "/api/auth/login", new { key = TestApp.AccessKey }, authenticated: false, csrf: false);
        var undo = await app.SendAsync(HttpMethod.Post, "/api/undo", new { }, csrf: false);

        Assert.Equal(HttpStatusCode.Forbidden, login.Status);
        Assert.Equal("csrf", login.Error);
        Assert.Equal(HttpStatusCode.Forbidden, undo.Status);
    }

    [Fact]
    public async Task Logout_clears_the_cookie()
    {
        await using var app = new TestApp();

        var logout = await app.PostAsync("/api/auth/logout");

        Assert.Equal(HttpStatusCode.NoContent, logout.Status);
        Assert.Contains("ts_session=;", Assert.Single(logout.Raw.Headers.GetValues("Set-Cookie")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_an_access_key_everything_but_meta_answers_503()
    {
        await using var app = new TestApp(accessKey: string.Empty);

        var me = await app.GetAsync("/api/me");
        var login = await app.SendAsync(HttpMethod.Post, "/api/auth/login", new { key = "x" }, authenticated: false);
        var meta = await app.GetAsync("/api/meta", authenticated: false);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, me.Status);
        Assert.Equal("web_disabled", me.Error);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, login.Status);
        Assert.Equal(HttpStatusCode.OK, meta.Status);
        Assert.False(meta.Json["webEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Meta_is_anonymous_and_reports_state()
    {
        await using var app = new TestApp();

        var anonymous = await app.GetAsync("/api/meta", authenticated: false);
        Assert.Equal(HttpStatusCode.OK, anonymous.Status);
        Assert.True(anonymous.Json["webEnabled"]!.GetValue<bool>());
        Assert.False(anonymous.Json["authenticated"]!.GetValue<bool>());
        Assert.False(anonymous.Json["onboarded"]!.GetValue<bool>());
        Assert.Equal(8, anonymous.Json["assetsVersion"]!.GetValue<string>().Length);
        Assert.True(anonymous.Json["push"]!["enabled"]!.GetValue<bool>());
        Assert.Equal(87, anonymous.Json["push"]!["publicKey"]!.GetValue<string>().Length);
        Assert.False(anonymous.Json["llm"]!.GetValue<bool>());

        await app.OnboardAsync();
        var signedIn = await app.GetAsync("/api/meta");
        Assert.True(signedIn.Json["authenticated"]!.GetValue<bool>());
        Assert.True(signedIn.Json["onboarded"]!.GetValue<bool>());
    }

    [Fact]
    public async Task Unknown_api_route_is_a_json_404_and_bad_json_is_400()
    {
        await using var app = new TestApp();

        var missing = await app.GetAsync("/api/nothing-here");
        var bad = await app.SendAsync(HttpMethod.Post, "/api/quiet", "{ not json");

        Assert.Equal(HttpStatusCode.NotFound, missing.Status);
        Assert.Equal("not_found", missing.Error);
        Assert.Equal(HttpStatusCode.BadRequest, bad.Status);
        Assert.Equal("bad_request", bad.Error);
    }

    [Fact]
    public async Task Owner_is_the_configured_telegram_user_or_a_user_without_telegram()
    {
        await using (var plain = new TestApp())
        {
            var me = await plain.GetAsync("/api/me");
            Assert.False(me.Json["telegramLinked"]!.GetValue<bool>());
            Assert.Equal("Пользователь", me.Json["displayName"]!.GetValue<string>());
        }

        await using var linked = new TestApp(telegramChatId: "424242");
        var linkedMe = await linked.GetAsync("/api/me");
        Assert.True(linkedMe.Json["telegramLinked"]!.GetValue<bool>());
    }
}
