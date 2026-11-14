using SkiaSharp;
using TimeShift.Application.Abstractions;
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

    private static readonly SKColor Background = new(0xFA, 0xFA, 0xF7);

    private static readonly SKColor Ink = new(0x22, 0x22, 0x22);

    private static readonly SKColor Muted = new(0x88, 0x88, 0x88);

    private static readonly SKColor Future = new(0xEC, 0xEC, 0xE8);

    private static readonly SKColor NowLine = new(0xD3, 0x2F, 0x2F);

    private static readonly Lazy<SKTypeface> Font = new(FindFont);

    /// <summary>Цвет категории (по ключу, а не только группе — сон и дорога различаются).</summary>
    public static SKColor ColorOf(string? categoryKey) => categoryKey switch
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
        _ => new SKColor(0x9E, 0x9E, 0x9E),
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
            DrawPiece(canvas, piece, BarLeft, BarRight, Y(piece.StartUtc), Y(piece.EndUtc), label: true, day.TimeZone);
        }

        foreach (var piece in day.Parallel)
        {
            DrawPiece(canvas, piece, ParallelLeft, ParallelRight, Y(piece.StartUtc), Y(piece.EndUtc), label: false, day.TimeZone);
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
        const int left = 70;
        const int right = 700;
        const int top = 110;
        const int rowHeight = 170;
        var height = top + (rowHeight * week.Days.Count) + 30;
        using var surface = SKSurface.Create(new SKImageInfo(Width, height));
        var canvas = surface.Canvas;
        canvas.Clear(Background);
        using var title = Paint(Ink);
        canvas.DrawText(week.Title, 24, 52, SKTextAlign.Left, Font30(), title);

        // Ось часов сверху.
        using var hours = Paint(Muted);
        for (var h = 0; h <= 24; h += 6)
        {
            var x = left + ((right - left) * h / 24f);
            canvas.DrawText($"{h % 24:00}", x, top - 14, SKTextAlign.Center, Font16(), hours);
        }

        using var dayLabel = Paint(Ink);
        var row = 0;
        foreach (var (day, start, pieces) in week.Days)
        {
            var y0 = top + (row * rowHeight) + 10;
            var y1 = y0 + rowHeight - 40;
            canvas.DrawText(day.ToString("ddd d", System.Globalization.CultureInfo.GetCultureInfo("ru-RU")), 10, y0 + 24, SKTextAlign.Left, Font20(), dayLabel);
            float X(DateTime utc) => left + (float)((utc - start).TotalMinutes / (24 * 60)) * (right - left);
            using (var empty = new SKPaint { Color = Future })
            {
                canvas.DrawRect(new SKRect(left, y0 + 34, right, y1), empty);
            }

            foreach (var piece in pieces)
            {
                DrawHorizontal(canvas, piece, X(piece.StartUtc), X(piece.EndUtc), y0 + 34, y1);
            }

            row++;
        }

        return Encode(surface);
    }

    private static void DrawPiece(SKCanvas canvas, TimelinePiece piece, float left, float right, float top, float bottom, bool label, TimeZoneInfo tz)
    {
        if (bottom - top < 0.5f)
        {
            return;
        }

        var rect = new SKRect(left, top, right, bottom);
        Fill(canvas, piece, rect);
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

    private static void DrawHorizontal(SKCanvas canvas, TimelinePiece piece, float x0, float x1, float top, float bottom)
    {
        if (x1 - x0 < 0.5f)
        {
            return;
        }

        Fill(canvas, piece, new SKRect(x0, top, x1, bottom));
    }

    /// <summary>Заливка по статусу: ✅ сплошная, ⚪ бледная, 🟡 штриховка цветом категории; будущее — серым.</summary>
    private static void Fill(SKCanvas canvas, TimelinePiece piece, SKRect rect)
    {
        var color = ColorOf(piece.CategoryKey);
        if (piece.IsFuture)
        {
            using var future = new SKPaint { Color = Future.WithAlpha(255) };
            canvas.DrawRect(rect, future);
            return;
        }

        switch (piece.Status)
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
