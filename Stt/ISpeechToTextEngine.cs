using PushToTalkDictation.Audio;

namespace PushToTalkDictation.Stt;

/// <summary>
/// The only contract the rest of the app knows about. Drop in whisper.cpp, a
/// sherpa-onnx model, a local HTTP server or your own native wrapper by
/// implementing this and registering it in <see cref="SpeechToTextEngineFactory"/>.
///
/// Implementations must be safe to call from any thread and must do their heavy
/// lifting off the caller's thread (the caller is a background pipeline, never the UI,
/// but blocking it stalls the dictation queue).
/// </summary>
public interface ISpeechToTextEngine : IAsyncDisposable
{
    /// <summary>Human-readable name shown in the tray menu and the log.</summary>
    string Name { get; }

    /// <summary>True once the model is loaded and the first call will not pay load cost.</summary>
    bool IsReady { get; }

    /// <summary>
    /// Loads the model. Safe to call more than once; concurrent callers share one load.
    /// Called at startup when WarmUpOnStart is set, otherwise lazily on first transcription.
    /// </summary>
    Task InitializeAsync(CancellationToken ct = default);

    /// <summary>
    /// Transcribes a mono 16 kHz clip. Returns the recognised text, trimmed;
    /// an empty string when nothing was recognised.
    /// </summary>
    Task<string> TranscribeAsync(AudioClip clip, CancellationToken ct = default);
}

/// <summary>
/// Shared plumbing: idempotent, race-free initialisation plus lazy init on first use.
/// </summary>
public abstract class SpeechToTextEngineBase : ISpeechToTextEngine
{
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private volatile bool _ready;

    public abstract string Name { get; }

    public bool IsReady => _ready;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_ready) return;

        await _initGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_ready) return;
            await LoadAsync(ct).ConfigureAwait(false);
            _ready = true;
        }
        finally
        {
            _initGate.Release();
        }
    }

    public async Task<string> TranscribeAsync(AudioClip clip, CancellationToken ct = default)
    {
        if (clip.Samples.Length == 0) return string.Empty;

        await InitializeAsync(ct).ConfigureAwait(false);
        var text = await RecognizeAsync(clip, ct).ConfigureAwait(false);
        return Normalize(text);
    }

    /// <summary>Load the model. Called at most once.</summary>
    protected abstract Task LoadAsync(CancellationToken ct);

    /// <summary>Run inference. The model is guaranteed to be loaded.</summary>
    protected abstract Task<string> RecognizeAsync(AudioClip clip, CancellationToken ct);

    /// <summary>
    /// Strips the artefacts every Whisper-family model emits on near-silent input
    /// ("[BLANK_AUDIO]", "(silence)", a lone "Thank you.") and collapses whitespace.
    /// </summary>
    protected static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var cleaned = text.Trim();

        // Bracketed non-speech annotations.
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[\[\(](?:BLANK_AUDIO|INAUDIBLE|MUSIC|SILENCE|NOISE|APPLAUSE|LAUGHTER|BLANK)[\]\)]",
            string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ").Trim();

        return cleaned;
    }

    protected virtual ValueTask DisposeCoreAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync().ConfigureAwait(false);
        _initGate.Dispose();
        GC.SuppressFinalize(this);
    }
}
