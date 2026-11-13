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
        });
        builder.Navigation(u => u.Settings).IsRequired();
    }
}
