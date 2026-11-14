using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TimeShift.Application.Abstractions;

namespace TimeShift.Speech;

public static class DependencyInjection
{
    /// <summary>Распознавание голоса (секция <c>Speech</c>); без модели и без AutoDownload — выключено.</summary>
    public static IServiceCollection AddSpeech(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(SpeechOptions.From(configuration));
        services.AddSingleton<ISpeechToText, WhisperSpeechToText>();
        return services;
    }
}
