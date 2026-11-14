using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TimeShift.Domain.Schedule;

namespace TimeShift.Infrastructure.Persistence.Configurations;

internal sealed class ScheduleBlockConfiguration : IEntityTypeConfiguration<ScheduleBlock>
{
    public void Configure(EntityTypeBuilder<ScheduleBlock> builder)
    {
        builder.ToTable("schedule_blocks");

        builder.HasKey(b => b.Id);
        builder.Property(b => b.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property<Guid>("UserId").HasColumnName("user_id");
        builder.HasIndex("UserId");
        builder.Property(b => b.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(16);
        builder.Property(b => b.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(b => b.CategoryKey).HasColumnName("category_key").HasMaxLength(64);
        builder.Property(b => b.ActivityId).HasColumnName("activity_id");
        builder.Property(b => b.Start).HasColumnName("start_time");
        builder.Property(b => b.End).HasColumnName("end_time");

        // Дни недели — битовая маска (бит = (int)DayOfWeek), spec §7: days_of_week.
        builder.Property(b => b.Days)
            .HasColumnName("days_of_week")
            .HasConversion(
                days => ToMask(days),
                mask => FromMask(mask),
                new ValueComparer<IReadOnlySet<DayOfWeek>>(
                    (a, b) => ToMask(a!) == ToMask(b!),
                    days => ToMask(days),
                    days => FromMask(ToMask(days))));

        builder.Ignore(b => b.CrossesMidnight);
        builder.Ignore(b => b.Length);
    }

    private static int ToMask(IReadOnlySet<DayOfWeek> days) => days.Aggregate(0, (mask, d) => mask | (1 << (int)d));

    private static IReadOnlySet<DayOfWeek> FromMask(int mask) =>
        Enum.GetValues<DayOfWeek>().Where(d => (mask & (1 << (int)d)) != 0).ToHashSet();
}
