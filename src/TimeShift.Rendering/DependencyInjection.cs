using Microsoft.Extensions.DependencyInjection;
using TimeShift.Application.Abstractions;

namespace TimeShift.Rendering;

public static class DependencyInjection
{
    /// <summary>Картинки лент (SkiaSharp).</summary>
    public static IServiceCollection AddRendering(this IServiceCollection services) =>
        services.AddSingleton<IDayRenderer, SkiaDayRenderer>();
}
