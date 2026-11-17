namespace TimeShift.Application.Abstractions;

/// <summary>Куда писать ожидаемые сбои, которые не должны ронять работу (упал один канал уведомлений). Реализует хост.</summary>
public interface IFailureReporter
{
    void Report(Exception exception, string message);
}
