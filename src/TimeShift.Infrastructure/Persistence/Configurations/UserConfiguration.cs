using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TimeShift.Domain.Users;

namespace TimeShift.Infrastructure.Persistence.Configurations;

internal sealed class UserConfiguration : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");

        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(u => u.TelegramChatId).HasColumnName("telegram_chat_id");
        builder.HasIndex(u => u.TelegramChatId).IsUnique();
        builder.Property(u => u.DisplayName).HasColumnName("display_name").HasMaxLength(200).IsRequired();
        builder.Property(u => u.TimeZone).HasColumnName("timezone").HasMaxLength(64).IsRequired();
        builder.Property(u => u.CreatedAtUtc).HasColumnName("created_at");
        builder.Property(u => u.IsOnboarded).HasColumnName("is_onboarded");
        builder.Property(u => u.DialogState).HasColumnName("dialog_state").HasMaxLength(64);
        builder.Ignore(u => u.TimeZoneInfo);
        builder.Property(u => u.LastSeenAtUtc).HasColumnName("last_seen_at");
        builder.Property(u => u.LastPingAtUtc).HasColumnName("last_ping_at");
        builder.Property(u => u.LastPingMessageRef).HasColumnName("last_ping_message_ref").HasMaxLength(64);
        builder.Property(u => u.UnansweredPings).HasColumnName("unanswered_pings");
        builder.Property(u => u.QuietUntilUtc).HasColumnName("quiet_until");
        builder.Property(u => u.YellowReviewedUntilUtc).HasColumnName("yellow_reviewed_until");
        builder.Property(u => u.LastEveningReview).HasColumnName("last_evening_review");
        builder.Property(u => u.LastMorningCheck).HasColumnName("last_morning_check");

        builder.OwnsOne(u => u.Settings, settings =>
        {
            settings.ToTable("user_settings");
            settings.WithOwner().HasForeignKey(s => s.UserId);
            settings.HasKey(s => s.UserId);
            settings.Property(s => s.UserId).HasColumnName("user_id");
            settings.Property(s => s.PingIntervalMin).HasColumnName("ping_interval_min");
            settings.Property(s => s.NormFactor).HasColumnName("norm_factor");
            settings.Property(s => s.MaxFactor).HasColumnName("max_factor");
            settings.Property(s => s.MaxUnansweredPings).HasColumnName("max_unanswered_pings");
            settings.Property(s => s.PingDuringBlocks).HasColumnName("ping_during_blocks");
            settings.Property(s => s.EveningReviewOffsetMin).HasColumnName("evening_review_offset_min");
            settings.Property(s => s.RoundingMin).HasColumnName("rounding_min");
            settings.Ignore(s => s.Extension);
        });
        builder.Navigation(u => u.Settings).IsRequired();

        builder.HasMany(u => u.Blocks).WithOne().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(u => u.Blocks).UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();

        // Свои категории — часть агрегата: набор нужен везде, где есть пользователь (имена, итоги, выбор).
        builder.Ignore(u => u.Categories);
        builder.HasMany(u => u.OwnCategories).WithOne().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(u => u.OwnCategories).HasField("_categories").UsePropertyAccessMode(PropertyAccessMode.Field).AutoInclude();
    }
}

/// <summary>Свои категории пользователя (spec §7: categories). Системные заданы в коде и в таблицу не попадают.</summary>
internal sealed class UserCategoryConfiguration : IEntityTypeConfiguration<TimeShift.Domain.Categories.UserCategory>
{
    public void Configure(EntityTypeBuilder<TimeShift.Domain.Categories.UserCategory> builder)
    {
        builder.ToTable("categories");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property<Guid>("UserId").HasColumnName("user_id");
        builder.Property(c => c.Key).HasColumnName("key").HasMaxLength(64).IsRequired();
        builder.HasIndex("UserId", nameof(TimeShift.Domain.Categories.UserCategory.Key)).IsUnique();
        builder.Property(c => c.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(c => c.Group).HasColumnName("group").HasConversion<string>().HasMaxLength(16);
        builder.Property(c => c.ParentKey).HasColumnName("parent_key").HasMaxLength(64);
        builder.Property(c => c.CreatedAtUtc).HasColumnName("created_at");
        builder.Property(c => c.ArchivedAtUtc).HasColumnName("archived_at");
        builder.Ignore(c => c.IsArchived);
    }
}
