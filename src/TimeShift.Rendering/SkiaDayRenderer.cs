using SkiaSharp;
using TimeShift.Application.Abstractions;
using TimeShift.Application.Tracking;
using TimeShift.Domain.Categories;
using TimeShift.Domain.Schedule;
using TimeShift.Domain.Timelines;

namespace TimeShift.Rendering;

/// <summary>
/// Картинки лент на SkiaSharp, под экран телефона. Цвет — группа категории; статус — заливка: ✅ сплошная,
/// ⚪ бледная, 🟡 штриховка; будущее — серым; красная линия — «сейчас».
/// </summary>
internal sealed class SkiaDayRenderer : IDayRenderer
{
    public const int Width = 720;

    public const int Height = 1400;

    private const int Top = 90;

    private const int Bottom = 1370;

    private const int BarLeft = 92;

    private const int BarRight = 560;

    private const int ParallelLeft = 576;

    private const int ParallelRight = 640;

    /// <summary>Неделя: левое поле, начало полос дней, правый край, верх первой полосы, шаг полос и строк категорий.</summary>
    public const int WeekLeft = 24;

    public const int WeekStripLeft = 96;

    public const int WeekRight = 696;

    public const int WeekTop = 110;

    public const int WeekRow = 52;

    public const int CategoryRow = 62;

    private const int ChangeColumn = 170;

    private static readonly SKColor StatusColor = new(0x54, 0x6E, 0x7A);

    private static readonly SKColor Background = new(0xFA, 0xFA, 0xF7);

    private static readonly SKColor Ink = new(0x22, 0x22, 0x22);

    private static readonly SKColor Muted = new(0x88, 0x88, 0x88);

    private static readonly SKColor Future = new(0xEC, 0xEC, 0xE8);

    private static readonly SKColor NowLine = new(0xD3, 0x2F, 0x2F);

    private static readonly Lazy<SKTypeface> Font = new(FindFont);

    /// <summary>Цвет категории (по ключу, а не только группе — сон и дорога различаются).</summary>
    public static SKColor ColorOf(string? categoryKey, CategorySet? categories = null) => categoryKey switch
    {
        DefaultCategories.Sleep => new SKColor(0x3F, 0x51, 0xB5),
        DefaultCategories.Commute => new SKColor(0x78, 0x90, 0x9C),
        DefaultCategories.Household => new SKColor(0x42, 0xA5, 0xF5),
        DefaultCategories.Work => new SKColor(0x2E, 0x7D, 0x32),
        DefaultCategories.Study => new SKColor(0x55, 0x8B, 0x2F),
        DefaultCategories.Projects => new SKColor(0x00, 0x89, 0x7B),
        DefaultCategories.Sport => new SKColor(0x7C, 0xB3, 0x42),
        DefaultCategories.Family or DefaultCategories.Friends => new SKColor(0x8E, 0x24, 0xAA),
        DefaultCategories.MindfulRest => new SKColor(0x26, 0xA6, 0x9A),
        { } key when key.StartsWith(DefaultCategories.Wasted, StringComparison.Ordinal) => new SKColor(0xEF, 0x6C, 0x00),
        { } key when categories?.Find(key) is { } own => OwnColor(own),
        _ => new SKColor(0x9E, 0x9E, 0x9E),
    };

    private static SKColor OwnColor(Category own) => own.ParentKey is { } parent && CategorySet.IsSystem(parent)
        ? ColorOf(parent)
        : own.Group switch
        {
            CategoryGroup.Useful => new SKColor(0x43, 0xA0, 0x47),
            CategoryGroup.Necessary => new SKColor(0x5C, 0x9C, 0xD6),
            CategoryGroup.People => new SKColor(0xAB, 0x47, 0xBC),
            CategoryGroup.Rest => new SKColor(0x4D, 0xB6, 0xAC),
            _ => new SKColor(0xF5, 0x7C, 0x00),
        };

    public byte[]? RenderDay(DayImage day)
    {
        using var surface = SKSurface.Create(new SKImageInfo(Width, Height));
        var canvas = surface.Canvas;
        canvas.Clear(Background);
        using var title = Paint(Ink);
        canvas.DrawText(day.Title, 24, 52, SKTextAlign.Left, Font30(), title);

        var span = (day.DayEndUtc - day.DayStartUtc).TotalMinutes;
        float Y(DateTime utc) => Top + (float)((utc - day.DayStartUtc).TotalMinutes / span) * (Bottom - Top);

        // Будущее и фон дня.
        using (var future = new SKPaint { Color = Future })
        {
            canvas.DrawRect(new SKRect(BarLeft, Top, BarRight, Bottom), future);
        }

        foreach (var piece in day.Pieces)
        {
            DrawPiece(canvas, piece, BarLeft, BarRight, Y(piece.StartUtc), Y(piece.EndUtc), label: true, day.TimeZone, day.Categories);
        }

        foreach (var piece in day.Parallel)
        {
            DrawPiece(canvas, piece, ParallelLeft, ParallelRight, Y(piece.StartUtc), Y(piece.EndUtc), label: false, day.TimeZone, day.Categories);
        }

        // Часы слева: каждые 2 часа местного времени.
        using var hours = Paint(Muted);
        using var grid = new SKPaint { Color = new SKColor(0, 0, 0, 30), StrokeWidth = 1 };
        var first = LocalTime.ToLocal(day.DayStartUtc, day.TimeZone);
        for (var h = 0; h <= 24; h += 2)
        {
            var utc = LocalTime.ToUtc(first.Date.AddHours(h), day.TimeZone);
            if (utc < day.DayStartUtc || utc > day.DayEndUtc)
            {
                continue;
            }

            var y = Y(utc);
            canvas.DrawLine(BarLeft - 6, y, BarRight, y, grid);
            canvas.DrawText($"{h % 24:00}:00", BarLeft - 12, y + 6, SKTextAlign.Right, Font18(), hours);
        }

        if (day.NowUtc > day.DayStartUtc && day.NowUtc < day.DayEndUtc)
        {
            using var now = new SKPaint { Color = NowLine, StrokeWidth = 3, IsAntialias = true };
            var y = Y(day.NowUtc);
            canvas.DrawLine(BarLeft - 6, y, ParallelRight, y, now);
        }

        if (day.Parallel.Count > 0)
        {
            using var small = Paint(Muted);
            canvas.DrawText("парал.", (ParallelLeft + ParallelRight) / 2f, Top - 10, SKTextAlign.Center, Font16(), small);
        }

        return Encode(surface);
    }

    public byte[]? RenderWeek(WeekImage week)
    {
        var stats = week.Stats;
        var categoriesTop = WeekTop + (WeekRow * week.Days.Count) + 26;
        var statusTop = categoriesTop + (CategoryRow * stats.Categories.Count) + 14;
        var height = statusTop + 86 + (stats.Parallel.Count > 0 ? 34 : 0) + (stats.IsCompared ? 30 : 0) + 14;
        using var surface = SKSurface.Create(new SKImageInfo(Width, height));
        var canvas = surface.Canvas;
        canvas.Clear(Background);
        using var ink = Paint(Ink);
        using var muted = Paint(Muted);
        using var track = new SKPaint { Color = Future };
        canvas.DrawText(week.Title, WeekLeft, 52, SKTextAlign.Left, Font30(), ink);

        // Полоса на день: слева полночь, справа следующая полночь.
        for (var h = 0; h <= 24; h += 6)
        {
            var x = WeekStripLeft + ((WeekRight - WeekStripLeft) * h / 24f);
            canvas.DrawText($"{h:00}", x, WeekTop - 10, h == 0 ? SKTextAlign.Left : h == 24 ? SKTextAlign.Right : SKTextAlign.Center, Font16(), muted);
        }

        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        for (var row = 0; row < week.Days.Count; row++)
        {
            var day = week.Days[row];
            float y0 = WeekTop + (row * WeekRow);
            var y1 = y0 + WeekRow - 12;
            canvas.DrawText(day.Day.ToString("ddd d", ru), WeekLeft, y0 + 27, SKTextAlign.Left, Font20(), day.Pieces.Count > 0 ? ink : muted);
            canvas.DrawRect(new SKRect(WeekStripLeft, y0, WeekRight, y1), track);
            var span = (day.EndUtc - day.StartUtc).TotalMinutes;
            float X(DateTime utc) => WeekStripLeft + (float)((utc - day.StartUtc).TotalMinutes / span) * (WeekRight - WeekStripLeft);
            foreach (var piece in day.Pieces)
            {
                if (X(piece.EndUtc) - X(piece.StartUtc) >= 0.5f)
                {
                    Fill(canvas, ColorOf(piece.CategoryKey, week.Categories), piece.Status, piece.IsFuture, new SKRect(X(piece.StartUtc), y0, X(piece.EndUtc), y1));
                }
            }
        }

        // Категории: название, время, разница с прошлой неделей и полоса в долях от самой большой.
        var longest = stats.Categories.Count > 0 ? stats.Categories.Max(c => c.Total.Ticks) : 0;
        for (var i = 0; i < stats.Categories.Count; i++)
        {
            var category = stats.Categories[i];
            float y = categoriesTop + (i * CategoryRow);
            canvas.DrawText(category.Name, WeekLeft, y + 24, SKTextAlign.Left, Font22(), ink);
            if (category.Change is { } change)
            {
                canvas.DrawText(TrackingReplies.Change(change), WeekRight, y + 24, SKTextAlign.Right, Font20(), muted);
            }

            canvas.DrawText(TrackingReplies.Duration(category.Total), WeekRight - (stats.IsCompared ? ChangeColumn : 0), y + 24, SKTextAlign.Right, Font22(), ink);
            canvas.DrawRect(new SKRect(WeekLeft, y + 34, WeekRight, y + 48), track);
            if (category.Total > TimeSpan.Zero && longest > 0)
            {
                var width = Math.Max(3f, (WeekRight - WeekLeft) * (float)category.Total.Ticks / longest);
                using var bar = new SKPaint { Color = ColorOf(category.Key, week.Categories) };
                canvas.DrawRect(new SKRect(WeekLeft, y + 34, WeekLeft + width, y + 48), bar);
            }
        }

        // Доли статусов одной полосой в тех же заливках, что и лента.
        var (confirmed, assumed, questionable) = stats.Shares();
        float left = WeekLeft;
        foreach (var (status, time) in new[]
                 {
                     (SegmentStatus.Confirmed, stats.Confirmed), (SegmentStatus.Assumed, stats.Assumed),
                     (SegmentStatus.Questionable, stats.Questionable),
                 })
        {
            var right = stats.Total > TimeSpan.Zero ? left + ((WeekRight - WeekLeft) * (float)time.Ticks / stats.Total.Ticks) : left;
            if (right - left >= 0.5f)
            {
                Fill(canvas, StatusColor, status, isFuture: false, new SKRect(left, statusTop, right, statusTop + 30));
            }

            left = right;
        }

        var legend = statusTop + 62;
        canvas.DrawText($"подтверждено {confirmed}%", WeekLeft, legend, SKTextAlign.Left, Font20(), ink);
        canvas.DrawText($"предполагаю {assumed}%", (WeekLeft + WeekRight) / 2f, legend, SKTextAlign.Center, Font20(), ink);
        canvas.DrawText($"под вопросом {questionable}%", WeekRight, legend, SKTextAlign.Right, Font20(), ink);

        var line = legend + 24;
        if (stats.Parallel.Count > 0)
        {
            line += 10;
            canvas.DrawText($"Параллельно, сверх суток: {TrackingReplies.Duration(stats.ParallelSum)}", WeekLeft, line, SKTextAlign.Left, Font20(), muted);
            line += 24;
        }

        if (stats.IsCompared)
        {
            line += 6;
            canvas.DrawText("+ / − — к тем же дням прошлой недели", WeekLeft, line, SKTextAlign.Left, Font16(), muted);
        }

        return Encode(surface);
    }

    private static void DrawPiece(SKCanvas canvas, TimelinePiece piece, float left, float right, float top, float bottom, bool label, TimeZoneInfo tz, CategorySet? categories)
    {
        if (bottom - top < 0.5f)
        {
            return;
        }

        var rect = new SKRect(left, top, right, bottom);
        Fill(canvas, ColorOf(piece.CategoryKey, categories), piece.Status, piece.IsFuture, rect);
        if (!label || bottom - top < 24 || piece.IsFuture)
        {
            return;
        }

        var size = bottom - top >= 40 ? 20 : 16;
        using var text = Paint(piece.Status == SegmentStatus.Confirmed && !piece.IsFuture ? SKColors.White : Ink);
        var caption = $"{LocalTime.ToLocal(piece.StartUtc, tz):HH:mm}–{LocalTime.ToLocal(piece.EndUtc, tz):HH:mm} {piece.Title}";
        using var font = new SKFont(Font.Value, size);
        canvas.Save();
        canvas.ClipRect(rect);
        canvas.DrawText(caption, left + 10, top + Math.Min((bottom - top) / 2 + 7, 28), SKTextAlign.Left, font, text);
        canvas.Restore();
    }

    /// <summary>Заливка по статусу: ✅ сплошная, ⚪ бледная, 🟡 штриховка цветом категории; будущее — серым.</summary>
    private static void Fill(SKCanvas canvas, SKColor color, SegmentStatus status, bool isFuture, SKRect rect)
    {
        if (isFuture)
        {
            using var future = new SKPaint { Color = Future.WithAlpha(255) };
            canvas.DrawRect(rect, future);
            return;
        }

        switch (status)
        {
            case SegmentStatus.Confirmed:
            {
                using var paint = new SKPaint { Color = color };
                canvas.DrawRect(rect, paint);
                break;
            }

            case SegmentStatus.Assumed:
            {
                using var paint = new SKPaint { Color = color.WithAlpha(110) };
                canvas.DrawRect(rect, paint);
                break;
            }

            default:
            {
                using var light = new SKPaint { Color = new SKColor(0xFF, 0xF4, 0xCC) };
                canvas.DrawRect(rect, light);
                using var stripes = new SKPaint { Color = color.WithAlpha(150), StrokeWidth = 4, IsAntialias = true };
                canvas.Save();
                canvas.ClipRect(rect);
                for (var x = rect.Left - rect.Height; x < rect.Right; x += 14)
                {
                    canvas.DrawLine(x, rect.Bottom, x + rect.Height, rect.Top, stripes);
                }

                canvas.Restore();
                break;
            }
        }

        using var border = new SKPaint { Color = Background, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
        canvas.DrawRect(rect, border);
    }

    private static SKPaint Paint(SKColor color) => new() { Color = color, IsAntialias = true };

    private static SKFont Font30() => new(Font.Value, 30) { Embolden = true };

    private static SKFont Font22() => new(Font.Value, 22);

    private static SKFont Font20() => new(Font.Value, 20);

    private static SKFont Font18() => new(Font.Value, 18);

    private static SKFont Font16() => new(Font.Value, 16);

    private static byte[] Encode(SKSurface surface)
    {
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Шрифт с кириллицей: системный по символу, иначе DejaVu из пакета ОС (Docker), иначе встроенный.</summary>
    private static SKTypeface FindFont()
    {
        string[] files =
        [
            "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
            "/usr/share/fonts/dejavu/DejaVuSans.ttf",
            @"C:\Windows\Fonts\segoeui.ttf",
            @"C:\Windows\Fonts\arial.ttf",
        ];
        foreach (var file in files)
        {
            if (File.Exists(file) && SKTypeface.FromFile(file) is { } fromFile)
            {
                return fromFile;
            }
        }

        return SKFontManager.Default.MatchCharacter('Ж') ?? SKTypeface.Default;
    }
}
