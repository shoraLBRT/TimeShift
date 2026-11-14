using Microsoft.EntityFrameworkCore;
using TimeShift.Application.Conversations;
using TimeShift.Application.Pings;
using TimeShift.Application.Tracking;
using TimeShift.Host;
using TimeShift.Infrastructure;
using TimeShift.Infrastructure.Persistence;
using TimeShift.Telegram;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddScoped<TrackingService>();
builder.Services.AddScoped<TimeShift.Application.Llm.IntentExtractor>();
builder.Services.AddScoped<TimeShift.Application.Llm.IntentApplier>();
builder.Services.AddScoped<TimeShift.Application.Questions.QuestionService>();
builder.Services.AddScoped<YellowReview>();
builder.Services.AddScoped<DailyReview>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<PingService>();
builder.Services.AddTelegramAdapter(builder.Configuration);
TimeShift.Speech.DependencyInjection.AddSpeech(builder.Services, builder.Configuration);
if (builder.Services.Any(d => d.ServiceType == typeof(TimeShift.Application.Abstractions.INotifier)))
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

var version = typeof(Program).Assembly
    .GetCustomAttributes(typeof(System.Reflection.AssemblyInformationalVersionAttribute), false)
    .OfType<System.Reflection.AssemblyInformationalVersionAttribute>()
    .FirstOrDefault()?.InformationalVersion ?? "unknown";
app.Logger.LogInformation("TimeShift {Version}", version);

app.MapGet("/health", () => Results.Ok(new { status = "ok", version }));

app.Run();
