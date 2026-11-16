using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TimeShift.Application.Clients;
using TimeShift.Application.Push;

namespace TimeShift.Web;

public static class WebExtensions
{
    /// <summary>Имя политики ограничения попыток входа.</summary>
    public const string LoginRateLimit = "login";

    /// <summary>Попыток входа в минуту с одного адреса.</summary>
    public const int LoginAttemptsPerMinute = 5;

    /// <summary>
    /// Регистрирует адаптер web-клиента: настройки (<c>Web</c>), версию ассетов, ограничитель попыток входа и сервисы
    /// клиента из Application. После <c>app.UseRateLimiter()</c> маршруты подключаются через <see cref="MapTimeShiftApi"/>.
    /// </summary>
    public static IServiceCollection AddTimeShiftWeb(this IServiceCollection services, IConfiguration configuration, string version)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton(WebOptions.From(configuration));
        services.AddSingleton(new WebAppInfo(version));
        services.AddSingleton<AssetsVersion>();

        services.AddScoped<OwnerService>();
        services.AddScoped<DayService>();
        services.AddScoped<EntryService>();
        services.AddScoped<ProfileService>();
        services.AddScoped<ActivityService>();
        services.AddScoped<MessageService>();

        // Web Push: ключи из Push:* или созданные сервером; канал уведомлений — только когда web-клиент включён.
        var push = configuration.GetSection("Push");
        services.AddSingleton(new PushOptions(
            push["PublicKey"], push["PrivateKey"], string.IsNullOrWhiteSpace(push["Subject"]) ? PushOptions.DefaultSubject : push["Subject"]!.Trim()));
        services.AddScoped<VapidKeyProvider>();
        services.AddScoped<PushService>();
        if (WebOptions.From(configuration).IsEnabled)
        {
            services.AddScoped<TimeShift.Application.Abstractions.INotificationChannel, WebPushChannel>();
        }


        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy(LoginRateLimit, context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = LoginAttemptsPerMinute,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true,
                }));
            options.OnRejected = async (context, cancellationToken) =>
            {
                if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }

                await ApiEndpoints.WriteErrorAsync(
                    context.HttpContext, StatusCodes.Status429TooManyRequests, "too_many_requests",
                    "Слишком много попыток входа — подожди минуту.", null, cancellationToken);
            };
        });
        return services;
    }
}
