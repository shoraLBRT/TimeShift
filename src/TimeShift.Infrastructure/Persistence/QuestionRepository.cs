using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using TimeShift.Application.Questions;
using TimeShift.Domain.Users;

namespace TimeShift.Infrastructure.Persistence;

internal sealed class QuestionRepository(TimeShiftDbContext db) : IQuestionRepository
{
    public async Task<PendingQuestion?> FindAsync(Guid userId, QuestionKind kind, Guid subjectId, CancellationToken cancellationToken) =>
        (await WhereAsync(q => q.UserId == userId && q.Kind == kind && q.SubjectId == subjectId, cancellationToken)).FirstOrDefault();

    public async Task<PendingQuestion?> GetAsync(Guid userId, Guid questionId, CancellationToken cancellationToken) =>
        (await WhereAsync(q => q.UserId == userId && q.Id == questionId, cancellationToken)).FirstOrDefault();

    public async Task<PendingQuestion?> OpenAsync(Guid userId, CancellationToken cancellationToken) =>
        (await WhereAsync(q => q.UserId == userId && q.AskedAtUtc != null && q.ResolvedAtUtc == null, cancellationToken)).FirstOrDefault();

    public async Task<PendingQuestion?> NextAsync(Guid userId, CancellationToken cancellationToken) =>
        (await WhereAsync(q => q.UserId == userId && q.AskedAtUtc == null && q.ResolvedAtUtc == null, cancellationToken))
        .OrderBy(q => q.CreatedAtUtc)
        .FirstOrDefault();

    public void Add(PendingQuestion question) => db.Questions.Add(question);

    public void Remove(PendingQuestion question) => db.Questions.Remove(question);

    /// <summary>
    /// Вопросы под условие — как их вернула бы база после сохранения (#65). Запрос отвечает по сохранённому: вопрос,
    /// заданный или закрытый в этой единице работы, для него всё ещё «не задан» или «открыт», добавленного в ней нет,
    /// забытый — есть. Поэтому запрос только подтягивает строки в контекст, а ответ собирается из контекста тем же
    /// условием: всё, что отличается от базы, в нём уже лежит.
    /// </summary>
    private async Task<List<PendingQuestion>> WhereAsync(Expression<Func<PendingQuestion, bool>> filter, CancellationToken cancellationToken)
    {
        await db.Questions.Where(filter).LoadAsync(cancellationToken);
        var matches = filter.Compile();
        return db.ChangeTracker.Entries<PendingQuestion>()
            .Where(e => e.State is not (EntityState.Deleted or EntityState.Detached) && matches(e.Entity))
            .Select(e => e.Entity)
            .ToList();
    }
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
