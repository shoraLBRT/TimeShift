using Microsoft.EntityFrameworkCore;
using TimeShift.Application.Abstractions;
using TimeShift.Domain.Users;

namespace TimeShift.Infrastructure.Persistence;

internal sealed class UserRepository(TimeShiftDbContext db) : IUserRepository
{
    public Task<User?> FindByTelegramChatIdAsync(long chatId, CancellationToken cancellationToken) =>
        db.Users.FirstOrDefaultAsync(u => u.TelegramChatId == chatId, cancellationToken);

    public void Add(User user) => db.Users.Add(user);

    public Task SaveChangesAsync(CancellationToken cancellationToken) => db.SaveChangesAsync(cancellationToken);
}
