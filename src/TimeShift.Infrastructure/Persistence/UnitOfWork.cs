using TimeShift.Application.Abstractions;

namespace TimeShift.Infrastructure.Persistence;

internal sealed class UnitOfWork(TimeShiftDbContext db) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
