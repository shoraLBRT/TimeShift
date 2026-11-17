using TimeShift.Application.Tracking;
using TimeShift.Domain.Timelines;
using TimeShift.Domain.Users;

namespace TimeShift.Application.Clients;

/// <summary>Кусок построенной ленты вместе с хранимым отрезком, из которого он получился.</summary>
/// <param name="Segment">Хранимый отрезок: закрытый основной дорожки или идущий (для его продления); <c>null</c> — вычисленный кусок.</param>
/// <param name="IsOpen">Кусок — продление идущего занятия.</param>
internal sealed record MappedPiece(TimelinePiece Piece, Segment? Segment, bool IsOpen);

/// <summary>
/// Лента окна с привязкой кусков к хранимым отрезкам. <see cref="TimelineBuilder"/> сливает соседей с одинаковым
/// содержимым и не помнит Id отрезков, поэтому здесь куски режутся по границам хранимых отрезков — так что один кусок
/// соответствует ровно одному отрезку, а клиент может править и удалять его по Id.
/// </summary>
internal static class DayBuilder
{
    public static IReadOnlyList<MappedPiece> Build(
        User user, Timeline timeline, CurrentActivity? current, TimeRange window, DateTime nowMinute)
    {
        var closed = timeline.Segments.Where(s => !s.IsOpen).ToList();
        var pieces = TimelineBuilder.Build(new TimelineRequest
        {
            Window = window,
            NowUtc = nowMinute,
            Template = user.Template(),
            Segments = closed,
            OpenActivity = current?.ToOpenActivity(),
            Policy = user.Settings.Extension,
        });

        var cuts = closed
            .SelectMany(s => new[] { s.StartUtc, s.EndUtc!.Value })
            .Where(t => t > window.StartUtc && t < window.EndUtc)
            .Distinct()
            .Order()
            .ToList();

        var result = new List<MappedPiece>(pieces.Count + cuts.Count);
        foreach (var piece in pieces)
        {
            var start = piece.StartUtc;
            foreach (var cut in cuts.Where(c => c > piece.StartUtc && c < piece.EndUtc).Append(piece.EndUtc))
            {
                result.Add(Map(piece with { StartUtc = start, EndUtc = cut }, closed, current));
                start = cut;
            }
        }

        return result;
    }

    private static MappedPiece Map(TimelinePiece piece, List<Segment> closed, CurrentActivity? current)
    {
        // Отрезки главнее всего остального в ленте: внутри отрезка кусок — именно он.
        var stored = closed.FirstOrDefault(s => s.StartUtc <= piece.StartUtc && piece.EndUtc <= s.EndUtc);
        if (stored is null && piece.Source == SegmentSource.Extension && current is not null)
        {
            return new MappedPiece(piece, current.Segment, true);
        }

        return new MappedPiece(piece, stored, false);
    }
}
