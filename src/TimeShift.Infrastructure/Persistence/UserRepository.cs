using Microsoft.EntityFrameworkCore;
using TimeShift.Application.Abstractions;
using TimeShift.Domain.Users;

namespace TimeShift.Infrastructure.Persistence;

internal sealed class UserRepository(TimeShiftDbContext db) : IUserRepository
{
    public Task<User?> FindByTelegramChatIdAsync(long chatId, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId, cancellationToken);

    public Task<User?> FindByIdAsync(Guid userId, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);

    public Task<User?> FindWithoutTelegramAsync(CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.TelegramChatId == null, cancellationToken);

    public Task<int> CountAsync(CancellationToken cancellationToken) => db.Users.CountAsync(cancellationToken);

    public Task<User?> FindFirstAsync(CancellationToken cancellationToken) =>
        db.Users.OrderBy(u => u.CreatedAtUtc).FirstOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<User>> ListOnboardedAsync(CancellationToken cancellationToken) =>
        await db.Users.Where(u => u.IsOnboarded).ToListAsync(cancellationToken);

    public void Add(User user) => db.Users.Add(user);
}
