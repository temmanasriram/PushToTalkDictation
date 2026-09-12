namespace PushToTalkDictation.Configuration;

/// <summary>Root of appsettings.json. Bound once at startup and treated as immutable.</summary>
public sealed class AppSettings
{
    public HotkeySettings Hotkey { get; set; } = new();
    public AudioSettings Audio { get; set; } = new();
    public SpeechToTextSettings SpeechToText { get; set; } = new();
    public InjectionSettings Injection { get; set; } = new();
    public PreviewSettings Preview { get; set; } = new();
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

    /// <summary>
    /// Transcribe and type in segments while the hotkey is still held, instead of waiting
    /// for the release.
    ///
    /// Two reasons to want it. Text appears as you speak rather than in one burst at the
    /// end; and it keeps each clip short enough for the model to handle - Moonshine starts
    /// quietly dropping words past about 50 seconds of audio and degenerates into repeating
    /// itself past about 75, because it was trained on utterances of a few seconds.
    ///
    /// Off by default: it changes when text lands, and it types into the focused window
    /// while you are still holding the keys.
    /// </summary>
    public bool ChunkLongDictation { get; set; }

    /// <summary>
    /// Segment length in seconds when <see cref="ChunkLongDictation"/> is on. The split is
    /// nudged to the quietest moment near the boundary so a word is not cut in half, so
    /// segments are approximately, not exactly, this long.
    /// </summary>
    public int ChunkSeconds { get; set; } = 20;
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
    /// Release the model from memory when dictation is switched off, reloading it on the
    /// next activation. Trades a reload — around a second once the files are in the OS
    /// cache — for the few hundred MB the model holds while idle.
    /// </summary>
    public bool UnloadOnInactive { get; set; } = true;

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

    /// <summary>
    /// Type into the window that was focused when the hold started, restoring it first if
    /// focus moved in the meantime (opening the tray menu is enough to move it). Without
    /// this, text lands wherever focus happens to be when transcription finishes.
    /// </summary>
    public bool RestoreTargetWindow { get; set; } = true;
}

public sealed class PreviewSettings
{
    /// <summary>
    /// Show a small floating window with what is being recognised, while the hotkey is
    /// still held.
    ///
    /// This is the safe half of live dictation. A streaming recogniser revises what it has
    /// already emitted, and reflecting that in the target window would mean sending
    /// backspaces into someone else's document - destructive if the caret moved or the app
    /// autocompleted. The overlay is ours to rewrite freely, so the preview can change as
    /// often as it likes while only finished text is ever typed.
    /// </summary>
    public bool ShowOverlay { get; set; }

    /// <summary>How often the in-progress audio is re-recognised for the preview.</summary>
    public int RefreshMs { get; set; } = 800;

    /// <summary>
    /// How much of the most recent audio the preview re-recognises. Bounds the cost: the
    /// work per refresh is proportional to this, not to how long the hold has run.
    /// </summary>
    public int MaxSeconds { get; set; } = 12;
}

public sealed class LoggingSettings
{
    public string MinimumLevel { get; set; } = "Information";
    public string FilePath { get; set; } = "logs/app.log";
}
