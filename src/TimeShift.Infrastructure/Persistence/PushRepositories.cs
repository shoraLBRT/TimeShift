using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TimeShift.Application.Abstractions;
using TimeShift.Domain.Push;

namespace TimeShift.Infrastructure.Persistence;

/// <summary>Секрет, созданный сервером (ключи VAPID).</summary>
public sealed class ServerKey
{
    public string Name { get; set; } = string.Empty;

    public string Value { get; set; } = string.Empty;
}

internal sealed class PushSubscriptionRepository(TimeShiftDbContext db) : IPushSubscriptionRepository
{
    public async Task<IReadOnlyList<WebPushSubscription>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        await db.PushSubscriptions.Where(s => s.UserId == userId).OrderBy(s => s.CreatedAtUtc).ToListAsync(cancellationToken);

    public Task<WebPushSubscription?> FindByEndpointAsync(string endpoint, CancellationToken cancellationToken) =>
        db.PushSubscriptions.FirstOrDefaultAsync(s => s.Endpoint == endpoint, cancellationToken);

    public void Add(WebPushSubscription subscription) => db.PushSubscriptions.Add(subscription);

    public void Remove(WebPushSubscription subscription) => db.PushSubscriptions.Remove(subscription);
}

internal sealed class ServerKeyRepository(TimeShiftDbContext db) : IServerKeyRepository
{
    public async Task<string?> GetAsync(string name, CancellationToken cancellationToken) =>
        (await db.ServerKeys.FirstOrDefaultAsync(k => k.Name == name, cancellationToken))?.Value;

    public void Set(string name, string value) => db.ServerKeys.Add(new ServerKey { Name = name, Value = value });
}

internal sealed class PushSubscriptionConfiguration : IEntityTypeConfiguration<WebPushSubscription>
{
    public void Configure(EntityTypeBuilder<WebPushSubscription> builder)
    {
        builder.ToTable("push_subscriptions");
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(s => s.UserId).HasColumnName("user_id");
        builder.HasOne<Domain.Users.User>().WithMany().HasForeignKey(s => s.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(s => s.Endpoint).HasColumnName("endpoint").HasMaxLength(2048).IsRequired();
        builder.HasIndex(s => s.Endpoint).IsUnique();
        builder.Property(s => s.P256dh).HasColumnName("p256dh").HasMaxLength(256).IsRequired();
        builder.Property(s => s.Auth).HasColumnName("auth").HasMaxLength(256).IsRequired();
        builder.Property(s => s.CreatedAtUtc).HasColumnName("created_at");
        builder.Property(s => s.LastOkAtUtc).HasColumnName("last_ok_at");
    }
}

internal sealed class ServerKeyConfiguration : IEntityTypeConfiguration<ServerKey>
{
    public void Configure(EntityTypeBuilder<ServerKey> builder)
    {
        builder.ToTable("server_keys");
        builder.HasKey(k => k.Name);
        builder.Property(k => k.Name).HasColumnName("name").HasMaxLength(64);
        builder.Property(k => k.Value).HasColumnName("value").HasMaxLength(512).IsRequired();
    }
}
