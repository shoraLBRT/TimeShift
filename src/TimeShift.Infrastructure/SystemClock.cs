using TimeShift.Application.Abstractions;

namespace TimeShift.Infrastructure;

internal sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
