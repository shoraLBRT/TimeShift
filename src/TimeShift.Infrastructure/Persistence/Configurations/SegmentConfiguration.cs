using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TimeShift.Domain.Activities;
using TimeShift.Domain.Timelines;

namespace TimeShift.Infrastructure.Persistence.Configurations;

internal sealed class SegmentConfiguration : IEntityTypeConfiguration<Segment>
{
    public void Configure(EntityTypeBuilder<Segment> builder)
    {
        builder.ToTable("segments");

        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property<Guid>("UserId").HasColumnName("user_id");
        builder.HasOne<Domain.Users.User>().WithMany().HasForeignKey("UserId").OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex("UserId", nameof(Segment.StartUtc));

        builder.Property(s => s.StartUtc).HasColumnName("start_utc");
        builder.Property(s => s.EndUtc).HasColumnName("end_utc");
        builder.Property(s => s.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(s => s.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.Source).HasColumnName("source").HasConversion<string>().HasMaxLength(16);
        builder.Property(s => s.ActivityId).HasColumnName("activity_id");
        builder.Property(s => s.CategoryKey).HasColumnName("category_key").HasMaxLength(64);
        builder.Property(s => s.ElapsedBefore).HasColumnName("elapsed_before");
        builder.Property(s => s.IsParallel).HasColumnName("is_parallel");

        builder.Ignore(s => s.IsOpen);
    }
}

internal sealed class ActivityConfiguration : IEntityTypeConfiguration<Activity>
{
    public void Configure(EntityTypeBuilder<Activity> builder)
    {
        builder.ToTable("activities");

        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(a => a.UserId).HasColumnName("user_id");
        builder.HasOne<Domain.Users.User>().WithMany().HasForeignKey(a => a.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(a => a.Name).HasColumnName("name").HasMaxLength(200).IsRequired();
        builder.Property(a => a.NormalizedName).HasColumnName("normalized_name").HasMaxLength(200).IsRequired();
        builder.HasIndex(a => new { a.UserId, a.NormalizedName }).IsUnique();
        builder.Property(a => a.CategoryKey).HasColumnName("category_key").HasMaxLength(64);
        builder.Property(a => a.TypicalDuration).HasColumnName("typical_duration");
    }
}

internal sealed class TimelineChangeConfiguration : IEntityTypeConfiguration<TimelineChangeRecord>
{
    public void Configure(EntityTypeBuilder<TimelineChangeRecord> builder)
    {
        builder.ToTable("timeline_changes");

        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(c => c.UserId).HasColumnName("user_id");
        builder.HasOne<Domain.Users.User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(c => new { c.UserId, c.AtUtc });
        builder.Property(c => c.AtUtc).HasColumnName("at");
        builder.Property(c => c.BeforeJson).HasColumnName("before_json").IsRequired();
        builder.Property(c => c.AfterJson).HasColumnName("after_json").IsRequired();
    }
}
