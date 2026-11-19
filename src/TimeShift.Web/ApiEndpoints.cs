using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Clients;
using TimeShift.Application.Conversations;
using TimeShift.Application.Push;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Pings;
using TimeShift.Domain.Users;

namespace TimeShift.Web;

/// <summary>Тело ошибки: <c>{ "error", "message", "field"? }</c>.</summary>
internal sealed record ApiError(string Error, string Message, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Field);

internal sealed record PushInfo(bool Enabled, string? PublicKey);

internal sealed record MetaResponse(
    string Version, string AssetsVersion, bool WebEnabled, bool Authenticated, bool Onboarded, bool Llm, bool Speech, int SpeechMaxSec,
    PushInfo Push);

internal sealed record DayResponse(DayModel Day);

internal sealed record UndoResponse(DayModel Day, string Result);

internal sealed record ReplyDto(string Text, bool ReplacesSource, bool HasImage, IReadOnlyList<IReadOnlyList<ReplyButton>> Buttons);

internal sealed record MessagesResponse(IReadOnlyList<ReplyDto> Replies, DayModel Day);

/// <summary>Метка: маршрут доступен без сессии.</summary>
internal sealed class AnonymousApi;

/// <summary>Метка: маршрут отвечает и когда web-клиент не настроен.</summary>
internal sealed class WorksWhenDisabled;

/// <summary>
/// REST API web-клиента (docs/web-api.md). Адаптер только переводит: JSON ↔ модели Application, ошибки
/// <see cref="ClientException"/> ↔ коды ответа; вся логика — в сервисах Application.
/// </summary>
public static class ApiEndpoints
{
    private const string CsrfHeader = "X-TimeShift";

    private const string OwnerKey = "ts.owner";

    /// <summary>Подключает <c>/api/*</c>. Нужны <c>UseRateLimiter</c> и <see cref="WebExtensions.AddTimeShiftWeb"/>.</summary>
    private static Optional<QuietHoursModel?> QuietHoursField(Fields f)
    {
        if (!f.Has("quietHours"))
        {
            return Optional<QuietHoursModel?>.Unset;
        }

        var quiet = f.Object("quietHours");
        return Optional<QuietHoursModel?>.Of(
            quiet is null ? null : new QuietHoursModel(quiet.Require<TimeOnly>("start"), quiet.Require<TimeOnly>("end")));
    }

    public static RouteGroupBuilder MapTimeShiftApi(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        var api = endpoints.MapGroup("/api");
        api.AddEndpointFilter<ApiGuard>();

        api.MapGet("/meta", MetaAsync).WithMetadata(new AnonymousApi(), new WorksWhenDisabled());

        api.MapPost("/auth/login", LoginAsync).WithMetadata(new AnonymousApi()).RequireRateLimiting(WebExtensions.LoginRateLimit);
        api.MapPost("/auth/logout", Logout).WithMetadata(new AnonymousApi());

        api.MapGet("/me", (HttpContext http, ProfileService profile) => Json(profile.Me(Owner(http))));
        api.MapPatch("/settings", async (HttpContext http, ProfileService profile, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            var patch = new SettingsPatch
            {
                DisplayName = f.Str("displayName"),
                TimeZone = f.Str("timeZone"),
                PingIntervalMin = f.Get<int>("pingIntervalMin"),
                NormFactor = f.Opt<double>("normFactor"),
                MaxFactor = f.Opt<double>("maxFactor"),
                MaxUnansweredPings = f.Get<int>("maxUnansweredPings"),
                PingDuringBlocks = f.Get<bool>("pingDuringBlocks"),
                EveningReviewOffsetMin = f.Get<int>("eveningReviewOffsetMin"),
                RoundingMin = f.Get<int>("roundingMin"),
                PingsEnabled = f.Get<bool>("pingsEnabled"),
                AfterUnansweredPings = f.Get<UnansweredPingsPolicy>("afterUnansweredPings"),
                QuietHours = QuietHoursField(f),
            };
            return Json(await profile.UpdateSettingsAsync(Owner(http), patch, ct));
        });

        api.MapPut("/schedule/sleep", async (HttpContext http, ProfileService profile, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            var weekend = f.Object("weekend");
            var sleep = new SleepModel(
                f.Require<TimeOnly>("start"),
                f.Require<TimeOnly>("end"),
                weekend is null ? null : new SleepRange(weekend.Require<TimeOnly>("start"), weekend.Require<TimeOnly>("end")));
            return Json(await profile.SetSleepAsync(Owner(http), sleep, ct));
        });
        api.MapPost("/schedule/blocks", async (HttpContext http, ProfileService profile, CancellationToken ct) =>
            Json(await profile.AddBlockAsync(Owner(http), await ReadBlockAsync(http, ct), ct)));
        api.MapPut("/schedule/blocks/{id:guid}", async (Guid id, HttpContext http, ProfileService profile, CancellationToken ct) =>
            Json(await profile.UpdateBlockAsync(Owner(http), id, await ReadBlockAsync(http, ct), ct)));
        api.MapDelete("/schedule/blocks/{id:guid}", async (Guid id, HttpContext http, ProfileService profile, CancellationToken ct) =>
            Json(await profile.RemoveBlockAsync(Owner(http), id, ct)));
        api.MapPost("/categories", async (HttpContext http, ProfileService profile, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            var request = new CategoryRequest(f.Str("name"), f.Get<Domain.Categories.CategoryGroup>("group"), f.Str("parent"));
            return Json(await profile.AddCategoryAsync(Owner(http), request, ct));
        });
        api.MapPatch("/categories/{key}", async (string key, HttpContext http, ProfileService profile, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            var patch = new CategoryPatch { Name = f.Str("name"), Group = f.Get<Domain.Categories.CategoryGroup>("group"), Parent = f.OptStr("parent") };
            return Json(await profile.UpdateCategoryAsync(Owner(http), key, patch, ct));
        });
        api.MapDelete("/categories/{key}", async (string key, HttpContext http, ProfileService profile, CancellationToken ct) =>
            Json(await profile.RemoveCategoryAsync(Owner(http), key, ct)));
        api.MapPost("/onboarding/complete", async (HttpContext http, ProfileService profile, CancellationToken ct) =>
            Json(await profile.CompleteOnboardingAsync(Owner(http), ct)));

        api.MapGet("/day/{date}", async (string date, HttpContext http, DayService days, CancellationToken ct) =>
            Json(await days.GetAsync(Owner(http), ParseDate(date, "date"), ct)));
        api.MapGet("/summary", async (HttpContext http, DayService days, CancellationToken ct) =>
        {
            var user = Owner(http);
            var today = days.Today(user);
            var to = Query(http, "to") is { } rawTo ? ParseDate(rawTo, "to") : today;
            var from = Query(http, "from") is { } rawFrom ? ParseDate(rawFrom, "from") : to.AddDays(-6);
            return Json(await days.SummaryAsync(user, from, to, ct));
        });

        api.MapPost("/now/start", async (HttpContext http, EntryService entries, DayService days, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            var user = Owner(http);
            await entries.StartAsync(user, new StartRequest(f.Get<Guid>("activityId"), f.Str("title"), f.Get<DateTime>("at")), ct);
            return await DayAfterAsync(http, days, ct);
        });
        api.MapPost("/now/stop", async (HttpContext http, EntryService entries, DayService days, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            await entries.StopAsync(Owner(http), f.Get<DateTime>("at"), ct);
            return await DayAfterAsync(http, days, ct);
        });
        api.MapPost("/now/continue", async (HttpContext http, EntryService entries, DayService days, CancellationToken ct) =>
        {
            await entries.ContinueAsync(Owner(http), ct);
            return await DayAfterAsync(http, days, ct);
        });

        api.MapPost("/entries", async (HttpContext http, EntryService entries, DayService days, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            await entries.AddAsync(
                Owner(http),
                new AddEntryRequest(
                    f.Require<DateTime>("start"), f.Require<DateTime>("end"), f.Str("title"), f.Get<Guid>("activityId"),
                    f.Str("category"), f.Get<bool>("parallel") ?? false),
                ct);
            return await DayAfterAsync(http, days, ct);
        });
        api.MapPatch("/entries/{id:guid}", async (Guid id, HttpContext http, EntryService entries, DayService days, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            await entries.EditAsync(Owner(http), id, new EditEntryRequest(f.Get<DateTime>("start"), f.Get<DateTime>("end"), f.Str("title"), f.Str("category")), ct);
            return await DayAfterAsync(http, days, ct);
        });
        api.MapDelete("/entries/{id:guid}", async (Guid id, HttpContext http, EntryService entries, DayService days, CancellationToken ct) =>
        {
            await entries.DeleteAsync(Owner(http), id, ct);
            return await DayAfterAsync(http, days, ct);
        });
        api.MapPost("/confirm", async (HttpContext http, EntryService entries, DayService days, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            await entries.ConfirmAsync(
                Owner(http), f.Require<DateTime>("start"), f.Require<DateTime>("end"), f.Get<bool>("includeQuestionable") ?? false, ct);
            return await DayAfterAsync(http, days, ct);
        });
        api.MapPost("/undo", async (HttpContext http, EntryService entries, DayService days, CancellationToken ct) =>
        {
            var result = await entries.UndoAsync(Owner(http), ct);
            var day = await days.GetAsync(Owner(http), View(http), ct);
            return Json(new UndoResponse(day, result switch
            {
                UndoResult.Undone => "undone",
                UndoResult.Stale => "stale",
                _ => "nothing",
            }));
        });
        api.MapPost("/quiet", async (HttpContext http, EntryService entries, DayService days, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            await entries.SetQuietAsync(Owner(http), f.Require<int>("minutes"), ct);
            return await DayAfterAsync(http, days, ct);
        });

        api.MapGet("/activities", async (HttpContext http, ActivityService activities, CancellationToken ct) =>
            Json(await activities.ListAsync(Owner(http), ct)));
        api.MapPatch("/activities/{id:guid}", async (Guid id, HttpContext http, ActivityService activities, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            var patch = new ActivityPatch { Name = f.Str("name"), Category = f.OptStr("category"), TypicalMin = f.Opt<int>("typicalMin") };
            return Json(await activities.UpdateAsync(Owner(http), id, patch, ct));
        });
        api.MapPut("/activities/{id:guid}/rules/{part}", async (Guid id, string part, HttpContext http, ActivityService activities, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            return Json(await activities.SetRuleAsync(Owner(http), id, part, f.Str("category"), ct));
        });
        api.MapDelete("/activities/{id:guid}/rules/{part}", async (Guid id, string part, HttpContext http, ActivityService activities, CancellationToken ct) =>
            Json(await activities.RemoveRuleAsync(Owner(http), id, part, ct)));

        api.MapPost("/messages", async (HttpContext http, MessageService messages, DayService days, CancellationToken ct) =>
        {
            var user = Owner(http);
            IReadOnlyList<BotReply> replies;
            if (IsWav(http.Request))
            {
                // Запись голоса — тело запроса целиком; длиннее предела не читаем, сервис ответит «слишком длинная».
                replies = await messages.HandleVoiceAsync(user, await ReadBodyAsync(http.Request, messages.MaxVoiceBytes + 1, ct), ct);
            }
            else
            {
                var f = await Fields.ReadAsync(http.Request, ct);
                replies = await messages.HandleAsync(user, f.Str("text"), f.Str("button"), ct);
            }

            return Json(new MessagesResponse(
                replies.Select(r => new ReplyDto(r.Text, r.ReplacesSource, r.Image is not null, r.Buttons)).ToList(),
                await days.GetAsync(user, View(http), ct)));
        });

        api.MapGet("/push", async (HttpContext http, PushService push, CancellationToken ct) =>
            Json(await push.InfoAsync(Owner(http), ct)));
        api.MapPost("/push/subscriptions", async (HttpContext http, PushService push, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            var keys = f.Object("keys");
            await push.SubscribeAsync(Owner(http), f.Str("endpoint"), keys?.Str("p256dh"), keys?.Str("auth"), ct);
            return Results.NoContent();
        });
        api.MapDelete("/push/subscriptions", async (HttpContext http, PushService push, CancellationToken ct) =>
        {
            var f = await Fields.ReadAsync(http.Request, ct);
            await push.UnsubscribeAsync(Owner(http), f.Str("endpoint"), ct);
            return Results.NoContent();
        });
        api.MapPost("/push/test", async (HttpContext http, PushService push, CancellationToken ct) =>
        {
            await push.TestAsync(Owner(http), ct);
            return Results.NoContent();
        });

        api.MapFallback("/{**path}", (HttpContext http) => Error(StatusCodes.Status404NotFound, "not_found", "Такого адреса нет."))
            .WithMetadata(new AnonymousApi(), new WorksWhenDisabled());
        return api;
    }

    internal static async Task WriteErrorAsync(
        HttpContext http, int status, string code, string message, string? field, CancellationToken cancellationToken)
    {
        http.Response.StatusCode = status;
        await http.Response.WriteAsJsonAsync(new ApiError(code, message, field), ApiJson.Options, cancellationToken);
    }

    private static IResult Json(object value) => Results.Json(value, ApiJson.Options);

    private static IResult Error(int status, string code, string message, string? field = null) =>
        Results.Json(new ApiError(code, message, field), ApiJson.Options, statusCode: status);

    private static User Owner(HttpContext http) =>
        http.Items[OwnerKey] as User ?? throw new InvalidOperationException("Owner is not resolved.");

    private static string? Query(HttpContext http, string name) =>
        http.Request.Query[name].FirstOrDefault() is { Length: > 0 } value ? value : null;

    private static DateOnly ParseDate(string text, string field) =>
        DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : throw ClientException.Invalid(field, "Дата — в виде 2026-11-17.");

    /// <summary>День, который показывает клиент после изменения: <c>?view=</c>, по умолчанию — сегодня.</summary>
    private static DateOnly? View(HttpContext http) => Query(http, "view") is { } view ? ParseDate(view, "view") : null;

    private static async Task<IResult> DayAfterAsync(HttpContext http, DayService days, CancellationToken ct) =>
        Json(new DayResponse(await days.GetAsync(Owner(http), View(http), ct)));

    private static bool IsWav(HttpRequest request) =>
        request.ContentType?.Split(';')[0].Trim().ToLowerInvariant() is "audio/wav" or "audio/x-wav" or "audio/wave" or "audio/vnd.wave";

    /// <summary>Тело запроса, но не больше <paramref name="limit"/> байт (остальное не читается).</summary>
    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, int limit, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while (buffer.Length < limit
            && (read = await request.Body.ReadAsync(chunk.AsMemory(0, (int)Math.Min(chunk.Length, limit - buffer.Length)), ct)) > 0)
        {
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    private static async Task<BlockRequest> ReadBlockAsync(HttpContext http, CancellationToken ct)
    {
        var f = await Fields.ReadAsync(http.Request, ct);
        return new BlockRequest(
            f.Get<Domain.Schedule.ScheduleBlockKind>("kind") ?? throw ClientException.Invalid("kind", "Поле обязательно."),
            f.Str("title"),
            f.Str("category"),
            f.Ints("days"),
            f.Require<TimeOnly>("start"),
            f.Require<TimeOnly>("end"));
    }

    private static async Task<IResult> MetaAsync(
        HttpContext http, WebOptions options, WebAppInfo info, AssetsVersion assets, IClock clock,
        Application.Llm.IntentApplier intents, MessageService messages, OwnerService owners, PushService push, CancellationToken ct)
    {
        var authenticated = options.IsEnabled
            && SessionTokens.IsValid(options.AccessKey, http.Request.Cookies[SessionTokens.CookieName], clock.UtcNow);
        var onboarded = authenticated && (await owners.ResolveAsync(options.TelegramChatId, ct)).IsOnboarded;
        var pushKey = options.IsEnabled ? await push.PublicKeyAsync(ct) : null;
        return Json(new MetaResponse(
            info.Version, assets.Current, options.IsEnabled, authenticated, onboarded, intents.IsEnabled, messages.VoiceEnabled,
            (int)messages.MaxVoiceDuration.TotalSeconds, new PushInfo(options.IsEnabled, pushKey)));
    }

    private static async Task<IResult> LoginAsync(HttpContext http, WebOptions options, IClock clock, CancellationToken ct)
    {
        var f = await Fields.ReadAsync(http.Request, ct);
        if (!SessionTokens.KeyMatches(options.AccessKey, f.Str("key")))
        {
            return Error(StatusCodes.Status401Unauthorized, "unauthorized", "Неверный ключ доступа.");
        }

        var expires = clock.UtcNow + SessionTokens.Lifetime;
        http.Response.Cookies.Append(
            SessionTokens.CookieName,
            SessionTokens.Create(options.AccessKey, expires),
            CookieSettings(http, expires));
        return Results.NoContent();
    }

    private static IResult Logout(HttpContext http)
    {
        http.Response.Cookies.Delete(SessionTokens.CookieName, CookieSettings(http, null));
        return Results.NoContent();
    }

    private static CookieOptions CookieSettings(HttpContext http, DateTime? expires) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Lax,
        Secure = http.Request.IsHttps,
        Path = "/",
        IsEssential = true,
        MaxAge = expires is null ? null : SessionTokens.Lifetime,
        Expires = expires is { } e ? new DateTimeOffset(e) : null,
    };

    /// <summary>
    /// Общий фильтр: web выключен → 503; изменение без заголовка <c>X-TimeShift: 1</c> → 403 (CSRF); нет сессии → 401;
    /// владелец кладётся в <c>HttpContext.Items</c>; ожидаемые ошибки сервисов — в коды ответа.
    /// </summary>
    private sealed class ApiGuard : IEndpointFilter
    {
        public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
        {
            var http = context.HttpContext;
            var services = http.RequestServices;
            var options = services.GetRequiredService<WebOptions>();
            var metadata = http.GetEndpoint()?.Metadata;
            http.Response.Headers.CacheControl = "no-store";

            if (!options.IsEnabled && metadata?.GetMetadata<WorksWhenDisabled>() is null)
            {
                return Error(StatusCodes.Status503ServiceUnavailable, "web_disabled", "Веб-клиент не настроен: задайте Web:AccessKey.");
            }

            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
                && http.Request.Headers[CsrfHeader] != "1")
            {
                return Error(StatusCodes.Status403Forbidden, "csrf", "Не хватает заголовка X-TimeShift: 1.");
            }

            try
            {
                if (metadata?.GetMetadata<AnonymousApi>() is null)
                {
                    var clock = services.GetRequiredService<IClock>();
                    if (!SessionTokens.IsValid(options.AccessKey, http.Request.Cookies[SessionTokens.CookieName], clock.UtcNow))
                    {
                        return Error(StatusCodes.Status401Unauthorized, "unauthorized", "Нужно войти.");
                    }

                    http.Items[OwnerKey] = await services.GetRequiredService<OwnerService>()
                        .ResolveAsync(options.TelegramChatId, http.RequestAborted);
                }

                return await next(context);
            }
            catch (ClientException e)
            {
                return e.Kind switch
                {
                    ClientErrorKind.NotFound => Error(StatusCodes.Status404NotFound, "not_found", e.Message),
                    ClientErrorKind.Conflict => Error(StatusCodes.Status409Conflict, "conflict", e.Message),
                    _ => Error(StatusCodes.Status422UnprocessableEntity, "validation", e.Message, e.Field),
                };
            }
            catch (BadRequestException e)
            {
                return Error(StatusCodes.Status400BadRequest, "bad_request", e.Message);
            }
        }
    }
}
