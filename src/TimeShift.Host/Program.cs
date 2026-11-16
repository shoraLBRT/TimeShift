using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.ResponseCompression;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using TimeShift.Application.Conversations;
using TimeShift.Application.Pings;
using TimeShift.Application.Tracking;
using TimeShift.Host;
using TimeShift.Infrastructure;
using TimeShift.Infrastructure.Persistence;
using TimeShift.Telegram;
using TimeShift.Web;

var builder = WebApplication.CreateBuilder(args);

var version = typeof(Program).Assembly
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
    .FirstOrDefault()?.InformationalVersion ?? "unknown";

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<TrackingService>();
builder.Services.AddScoped<TimeShift.Application.Llm.IntentExtractor>();
builder.Services.AddScoped<TimeShift.Application.Llm.IntentApplier>();
builder.Services.AddScoped<TimeShift.Application.Questions.QuestionService>();
builder.Services.AddScoped<YellowReview>();
builder.Services.AddScoped<DailyReview>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<PingService>();
builder.Services.AddScoped<TimeShift.Application.Abstractions.INotifier, TimeShift.Application.Notifications.NotificationFanout>();
builder.Services.AddTelegramAdapter(builder.Configuration);
builder.Services.AddTimeShiftWeb(builder.Configuration, version);

// За обратным прокси (docker, Caddy/nginx): настоящие адрес и схема приходят в X-Forwarded-*.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
    options.MimeTypes = ResponseCompressionDefaults.MimeTypes.Concat(["application/manifest+json", "image/svg+xml"]);
});
TimeShift.Speech.DependencyInjection.AddSpeech(builder.Services, builder.Configuration);
TimeShift.Rendering.DependencyInjection.AddRendering(builder.Services);
if (builder.Services.Any(d => d.ServiceType == typeof(TimeShift.Application.Abstractions.INotificationChannel)))
{
    builder.Services.AddHostedService<PingWorker>();
}

var app = builder.Build();

if (app.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    var db = scope.ServiceProvider.GetRequiredService<TimeShiftDbContext>();
    await db.Database.MigrateAsync();
    app.Logger.LogInformation("Database migrations applied ({Provider})", db.Database.ProviderName);
}

if (!app.Services.GetRequiredService<TelegramOptions>().IsEnabled)
{
    app.Logger.LogWarning("Telegram adapter is disabled: Telegram:BotToken is not set");
}

app.Logger.LogInformation("TimeShift {Version}", version);
if (!app.Services.GetRequiredService<WebOptions>().IsEnabled)
{
    app.Logger.LogWarning("Web client is disabled: Web:AccessKey is not set");
}

app.UseForwardedHeaders();
app.UseResponseCompression();

// Клиент (PWA) лежит в wwwroot: каждый файл проверяется заново, обновление ассетов — через assetsVersion.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseDefaultFiles();
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = context => context.Context.Response.Headers.CacheControl = "no-cache",
});

app.UseRateLimiter();
app.MapTimeShiftApi();
app.MapGet("/health", () => Results.Ok(new { status = "ok", version }));

app.Run();

/// <summary>Нужен интеграционным тестам: <c>WebApplicationFactory&lt;Program&gt;</c>.</summary>
public partial class Program;
