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
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<PingService>();
builder.Services.AddTelegramAdapter(builder.Configuration);
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

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
