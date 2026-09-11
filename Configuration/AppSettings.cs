namespace PushToTalkDictation.Configuration;

/// <summary>Root of appsettings.json. Bound once at startup and treated as immutable.</summary>
public sealed class AppSettings
{
    public HotkeySettings Hotkey { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public SpeechToTextSettings SpeechToText { get; set; } = new();
    public InjectionSettings Injection { get; set; } = new();
    public LoggingSettings Logging { get; set; } = new();

    /// <summary>Whether the app starts in the "Active" state.</summary>
    public bool StartActive { get; set; } = true;
}

public sealed class HotkeySettings
{
    public string Combo { get; set; } = "Ctrl+Shift+Space";
    public bool SuppressTriggerKey { get; set; } = true;
    public int ModifiersOnlyHoldMs { get; set; } = 250;
    public int MaxRecordingSeconds { get; set; } = 120;
}

public sealed class AudioSettings
{
    /// <summary>WASAPI endpoint id, or null/empty for the default capture device.</summary>
    public string? DeviceId { get; set; }

    public int TargetSampleRate { get; set; } = 16000;
    public int MinClipMs { get; set; } = 250;
    public float SilenceRmsThreshold { get; set; } = 0.0025f;
}

public enum SttEngineKind
{
    SherpaOnnx,
    WhisperNet,
    Http
}

public sealed class SpeechToTextSettings
{
    public SttEngineKind Engine { get; set; } = SttEngineKind.SherpaOnnx;
    public bool WarmUpOnStart { get; set; } = true;

    /// <summary>
    /// Optional root for the relative model paths below. Null means "probe the standard
    /// locations" — see <see cref="Stt.ModelPathResolver"/> for the order.
    /// </summary>
    public string? ModelRoot { get; set; }

    public SherpaOnnxSettings SherpaOnnx { get; set; } = new();
    public WhisperNetSettings WhisperNet { get; set; } = new();
    public HttpSttSettings Http { get; set; } = new();
}

public enum SherpaModelKind
{
    Moonshine,
    Transducer,
    Whisper,
    SenseVoice
}

public sealed class SherpaOnnxSettings
{
    public SherpaModelKind ModelKind { get; set; } = SherpaModelKind.Moonshine;
    public string ModelDirectory { get; set; } = "models/sherpa-onnx-moonshine-base-en-int8";
    public int NumThreads { get; set; } = 4;
    public string Provider { get; set; } = "cpu";
    public string DecodingMethod { get; set; } = "greedy_search";

    public string Tokens { get; set; } = "tokens.txt";

    public string MoonshinePreprocessor { get; set; } = "preprocess.onnx";
    public string MoonshineEncoder { get; set; } = "encode.int8.onnx";
    public string MoonshineUncachedDecoder { get; set; } = "uncached_decode.int8.onnx";
    public string MoonshineCachedDecoder { get; set; } = "cached_decode.int8.onnx";

    public string TransducerEncoder { get; set; } = "encoder.int8.onnx";
    public string TransducerDecoder { get; set; } = "decoder.int8.onnx";
    public string TransducerJoiner { get; set; } = "joiner.int8.onnx";
    public string TransducerModelType { get; set; } = "nemo_transducer";

    public string WhisperEncoder { get; set; } = "encoder.int8.onnx";
    public string WhisperDecoder { get; set; } = "decoder.int8.onnx";

    public string SenseVoiceModel { get; set; } = "model.int8.onnx";
}

public sealed class WhisperNetSettings
{
    public string ModelPath { get; set; } = "models/ggml-base.en-q5_1.bin";
    public string Language { get; set; } = "en";
    public int NumThreads { get; set; } = 4;
    public bool NoContext { get; set; } = true;
}

public sealed class HttpSttSettings
{
    public string Endpoint { get; set; } = "http://127.0.0.1:8000/v1/audio/transcriptions";
    public string Model { get; set; } = "Systran/faster-distil-whisper-small.en";
    public string Language { get; set; } = "en";
    public int TimeoutSeconds { get; set; } = 30;
    public string? ApiKey { get; set; }
}

public sealed class InjectionSettings
{
    public int KeystrokeDelayMs { get; set; }
    public int ClipboardThreshold { get; set; } = 240;
    public int WaitForModifierReleaseMs { get; set; } = 400;
    public bool AppendTrailingSpace { get; set; } = true;
}

public sealed class LoggingSettings
{
    public string MinimumLevel { get; set; } = "Information";
    public string FilePath { get; set; } = "logs/app.log";
}
