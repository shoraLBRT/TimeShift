using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TimeShift.Application.Questions;
using TimeShift.Domain.Users;

namespace TimeShift.Infrastructure.Persistence;

internal sealed class QuestionRepository(TimeShiftDbContext db) : IQuestionRepository
{
    public async Task<PendingQuestion?> FindAsync(Guid userId, QuestionKind kind, Guid subjectId, CancellationToken cancellationToken) =>
        Local(q => q.UserId == userId && q.Kind == kind && q.SubjectId == subjectId)
        ?? Alive(await db.Questions.FirstOrDefaultAsync(q => q.UserId == userId && q.Kind == kind && q.SubjectId == subjectId, cancellationToken));

    public async Task<PendingQuestion?> GetAsync(Guid userId, Guid questionId, CancellationToken cancellationToken) =>
        Alive(await db.Questions.FirstOrDefaultAsync(q => q.UserId == userId && q.Id == questionId, cancellationToken));

    public async Task<PendingQuestion?> OpenAsync(Guid userId, CancellationToken cancellationToken) =>
        Local(q => q.UserId == userId && q.AskedAtUtc != null && q.ResolvedAtUtc == null)
        ?? await db.Questions.FirstOrDefaultAsync(q => q.UserId == userId && q.AskedAtUtc != null && q.ResolvedAtUtc == null, cancellationToken);

    public async Task<PendingQuestion?> NextAsync(Guid userId, CancellationToken cancellationToken) =>
        Local(q => q.UserId == userId && q.AskedAtUtc == null && q.ResolvedAtUtc == null)
        ?? await db.Questions
            .Where(q => q.UserId == userId && q.AskedAtUtc == null && q.ResolvedAtUtc == null)
            .OrderBy(q => q.CreatedAtUtc)
            .FirstOrDefaultAsync(cancellationToken);

    public void Add(PendingQuestion question) => db.Questions.Add(question);

    public void Remove(PendingQuestion question) => db.Questions.Remove(question);

    /// <summary>Запрос возвращает и забытое в этой единице работы (оно ещё в базе) — такого вопроса уже нет.</summary>
    private PendingQuestion? Alive(PendingQuestion? question) =>
        question is not null && db.Entry(question).State == EntityState.Deleted ? null : question;

    /// <summary>Добавленные в этой единице работы ещё не в базе — ищем и среди них.</summary>
    private PendingQuestion? Local(Func<PendingQuestion, bool> predicate) =>
        db.Questions.Local.Where(q => db.Entry(q).State == EntityState.Added).FirstOrDefault(predicate);
}

internal sealed class PendingQuestionConfiguration : IEntityTypeConfiguration<PendingQuestion>
{
    public void Configure(EntityTypeBuilder<PendingQuestion> builder)
    {
        builder.ToTable("pending_questions");
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).HasColumnName("id").ValueGeneratedNever();
        builder.Property(q => q.UserId).HasColumnName("user_id");
        builder.HasOne<User>().WithMany().HasForeignKey(q => q.UserId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(q => q.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(32);
        builder.Property(q => q.SubjectId).HasColumnName("subject_id");
        builder.HasIndex(q => new { q.UserId, q.Kind, q.SubjectId });
        builder.Property(q => q.CreatedAtUtc).HasColumnName("created_at");
        builder.Property(q => q.AskedAtUtc).HasColumnName("asked_at");
        builder.Property(q => q.ResolvedAtUtc).HasColumnName("resolved_at");
    }
}
