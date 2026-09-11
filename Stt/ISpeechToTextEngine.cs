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

    /// <summary>
    /// Releases the model and the memory it holds, leaving the engine reusable -
    /// the next <see cref="InitializeAsync"/> or <see cref="TranscribeAsync"/> loads it
    /// again. A no-op when nothing is loaded, and it waits for any decode already in
    /// flight rather than pulling the session out from under it.
    /// </summary>
    Task UnloadAsync();
}

/// <summary>
/// Shared plumbing: idempotent, race-free initialisation plus lazy init on first use.
/// </summary>
public abstract class SpeechToTextEngineBase : ISpeechToTextEngine
{
    /// <summary>
    /// Serialises everything that touches the native session: loading, decoding and
    /// unloading. Holding one gate across the decode - rather than a separate one per
    /// engine - is what makes <see cref="UnloadAsync"/> safe: an unload cannot free the
    /// session while inference is running, and a decode cannot start against a session
    /// that is being freed. Derived engines therefore no longer need a decode gate of
    /// their own, including third-party ones.
    /// </summary>
    private readonly SemaphoreSlim _session = new(1, 1);
    private volatile bool _ready;

    public abstract string Name { get; }

    public bool IsReady => _ready;

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        if (_ready) return;

        await _session.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _session.Release();
        }
    }

    public async Task<string> TranscribeAsync(AudioClip clip, CancellationToken ct = default)
    {
        if (clip.Samples.Length == 0) return string.Empty;

        await _session.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Loads on demand: the model may never have been loaded, or may have been
            // unloaded while dictation was switched off.
            await EnsureLoadedAsync(ct).ConfigureAwait(false);
            var text = await RecognizeAsync(clip, ct).ConfigureAwait(false);
            return Normalize(text);
        }
        finally
        {
            _session.Release();
        }
    }

    public async Task UnloadAsync()
    {
        if (!_ready) return;

        // No cancellation token: releasing memory is not something a caller should be
        // able to abandon halfway. The wait is bounded by the decode in flight, if any.
        await _session.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!_ready) return;
            _ready = false;
            await UnloadCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _session.Release();
        }
    }

    /// <summary>Caller must hold <see cref="_session"/>.</summary>
    private async Task EnsureLoadedAsync(CancellationToken ct)
    {
        if (_ready) return;
        await LoadAsync(ct).ConfigureAwait(false);
        _ready = true;
    }

    /// <summary>Load the model. Called again after an unload, so it must be repeatable.</summary>
    protected abstract Task LoadAsync(CancellationToken ct);

    /// <summary>
    /// Run inference. The model is guaranteed to be loaded, and calls are serialised,
    /// so a non-re-entrant native session needs no extra locking.
    /// </summary>
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

    /// <summary>
    /// Free the model and nothing else. Must leave the engine able to load again, so
    /// release the native session here but keep anything the engine needs for its whole
    /// lifetime. Called with the session gate held.
    /// </summary>
    protected virtual ValueTask UnloadCoreAsync() => ValueTask.CompletedTask;

    /// <summary>Final teardown. The engine is not reused afterwards.</summary>
    protected virtual ValueTask DisposeCoreAsync() => UnloadCoreAsync();

    public async ValueTask DisposeAsync()
    {
        await DisposeCoreAsync().ConfigureAwait(false);
        _ready = false;
        _session.Dispose();
        GC.SuppressFinalize(this);
    }
}
