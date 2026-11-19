using TimeShift.Domain.Timelines;

namespace TimeShift.Application.Clients;

/// <summary>Отрезок в хранилище, за которым стоит кусок ленты. <c>EndLocal</c> = <c>null</c> — занятие идёт.</summary>
public sealed record PieceSegment(Guid Id, DateTime StartLocal, DateTime? EndLocal);

/// <summary>
/// Кусок ленты дня. <c>StartMin</c>/<c>EndMin</c> — минуты от местного начала дня. Один кусок — ровно один хранимый
/// отрезок (<c>Segment</c>); <c>null</c> — вычисленный кусок (блок шаблона, фон).
/// </summary>
public sealed record DayPiece(
    int StartMin,
    int EndMin,
    DateTime StartUtc,
    DateTime EndUtc,
    string Title,
    string? Category,
    SegmentStatus Status,
    SegmentSource Source,
    bool Future,
    bool Open,
    Guid? ActivityId,
    PieceSegment? Segment);

/// <summary>Идущее занятие, какой бы день ни показывали.</summary>
public sealed record CurrentModel(
    Guid SegmentId,
    Guid? ActivityId,
    string Title,
    string? Category,
    DateTime StartUtc,
    DateTime StartLocal,
    int ElapsedMin,
    int? TypicalMin);

public sealed record CategoryMinutes(string? Category, int Minutes);

public sealed record StatusMinutes(int ConfirmedMin, int AssumedMin, int QuestionableMin);

public sealed record AttentionModel(bool PingPending, DateTime? QuietUntilUtc);

/// <summary>День целиком (основная дорожка покрывает <c>[0, DayMinutes)</c>); итоги считают только прошедшее.</summary>
public sealed record DayModel(
    DateOnly Date,
    string TimeZone,
    bool IsToday,
    DateTime StartUtc,
    DateTime EndUtc,
    int DayMinutes,
    DateTime NowUtc,
    int? NowMin,
    IReadOnlyList<DayPiece> Pieces,
    IReadOnlyList<DayPiece> Parallel,
    CurrentModel? Current,
    IReadOnlyList<CategoryMinutes> Totals,
    StatusMinutes Status,
    int UnknownMin,
    int ParallelMin,
    AttentionModel Attention);

/// <summary>Итоги дня в периоде. <paramref name="UnknownMin"/> — неизвестное время (фон), в <paramref name="Totals"/> не входит (#47).</summary>
public sealed record SummaryDay(DateOnly Date, IReadOnlyList<CategoryMinutes> Totals, StatusMinutes Status, int UnknownMin, int ParallelMin);

public sealed record SummaryModel(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<SummaryDay> Days,
    IReadOnlyList<CategoryMinutes> Totals,
    StatusMinutes Status,
    int UnknownMin);
