namespace TimeShift.Application.Abstractions;

/// <summary>Одна единица работы на сообщение или на пинг: все изменения репозиториев сохраняются вместе.</summary>
public interface IUnitOfWork
{
    Task SaveChangesAsync(CancellationToken cancellationToken);
}
