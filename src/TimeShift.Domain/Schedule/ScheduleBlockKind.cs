namespace TimeShift.Domain.Schedule;

/// <summary>Вид блока шаблона недели (spec §7: schedule_blocks.kind).</summary>
public enum ScheduleBlockKind
{
    Sleep,
    Fixed,
    Custom,
}
