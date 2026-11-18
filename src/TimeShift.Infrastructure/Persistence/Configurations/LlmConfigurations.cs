using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TimeShift.Application.Llm;
using TimeShift.Domain.Facts;
using TimeShift.Domain.Users;

namespace TimeShift.Infrastructure.Persistence.Configurations;

internal sealed class UserFactConfiguration : IEntityTypeConfiguration<UserFact>
{
    public void Configure(EntityTypeBuilder<UserFact> builder)
    {
        builder.ToTable("user_facts");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(f => f.UserId).HasColumnName("user_id");
        builder.HasOne<User>().WithMany().HasForeignKey(f => f.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(f => f.Key).HasColumnName("key").HasMaxLength(200).IsRequired();
        builder.HasIndex(f => new { f.UserId, f.Key }).IsUnique();
        builder.Property(f => f.Minutes).HasColumnName("minutes");
        builder.Property(f => f.Value).HasColumnName("value").HasMaxLength(64);
    }
}

internal sealed class PendingPeriodConfiguration : IEntityTypeConfiguration<PendingPeriod>
{
    public void Configure(EntityTypeBuilder<PendingPeriod> builder)
    {
        builder.ToTable("pending_periods");
        builder.HasKey(p => p.UserId);
        builder.Property(p => p.UserId).HasColumnName("user_id").ValueGeneratedNever();
        builder.HasOne<User>().WithOne().HasForeignKey<PendingPeriod>(p => p.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(p => p.CreatedAtUtc).HasColumnName("created_at");
        builder.Property(p => p.Text).HasColumnName("text").IsRequired();
        builder.Property(p => p.ResponseJson).HasColumnName("response_json").IsRequired();
        builder.Property(p => p.IntentIndex).HasColumnName("intent_index");
        builder.Property(p => p.FactKey).HasColumnName("fact_key").HasMaxLength(200);
        builder.Property(p => p.Question).HasColumnName("question").HasMaxLength(500);
        builder.Ignore(p => p.IsWaitingForFact);
    }
}

internal sealed class MessageLogConfiguration : IEntityTypeConfiguration<MessageLogEntry>
{
    public void Configure(EntityTypeBuilder<MessageLogEntry> builder)
    {
        builder.ToTable("messages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(m => m.UserId).HasColumnName("user_id");
        builder.HasOne<User>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(m => new { m.UserId, m.AtUtc });
        builder.Property(m => m.AtUtc).HasColumnName("at");
        builder.Property(m => m.Text).HasColumnName("text").IsRequired();
        builder.Property(m => m.Status).HasColumnName("status").HasMaxLength(32).IsRequired();
        builder.Property(m => m.Response).HasColumnName("llm_response");
        builder.Property(m => m.PromptTokens).HasColumnName("prompt_tokens");
        builder.Property(m => m.CompletionTokens).HasColumnName("completion_tokens");
    }
}
