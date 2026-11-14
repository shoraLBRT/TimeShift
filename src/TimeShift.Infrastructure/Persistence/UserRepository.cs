using Microsoft.EntityFrameworkCore;
using TimeShift.Application.Abstractions;
using TimeShift.Domain.Users;

namespace TimeShift.Infrastructure.Persistence;

internal sealed class UserRepository(TimeShiftDbContext db) : IUserRepository
{
    public Task<User?> FindByTelegramChatIdAsync(long chatId, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId, cancellationToken);

    public async Task<IReadOnlyList<User>> ListOnboardedAsync(CancellationToken cancellationToken) =>
        await db.Users.Where(u => u.IsOnboarded).ToListAsync(cancellationToken);

    public void Add(User user) => db.Users.Add(user);
}
