using Microsoft.Extensions.DependencyInjection;
using PushToTalkDictation.Configuration;

namespace PushToTalkDictation.Stt;

/// <summary>
/// Single place that maps configuration to an implementation. Adding your own
/// engine is two lines: register the type in Program.cs and add a case here.
/// </summary>
public static class SpeechToTextEngineFactory
{
    public static ISpeechToTextEngine Create(IServiceProvider services)
    {
        var settings = services.GetRequiredService<AppSettings>();

        return settings.SpeechToText.Engine switch
        {
            SttEngineKind.SherpaOnnx => services.GetRequiredService<SherpaOnnxSpeechToTextEngine>(),
            SttEngineKind.WhisperNet => services.GetRequiredService<WhisperNetSpeechToTextEngine>(),
            SttEngineKind.Http => services.GetRequiredService<HttpSpeechToTextEngine>(),
            _ => throw new NotSupportedException($"Unknown STT engine '{settings.SpeechToText.Engine}'.")
        };
    }
}
