using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using PushToTalkDictation.Audio;
using PushToTalkDictation.Configuration;
using PushToTalkDictation.Injection;
using PushToTalkDictation.Input;
using PushToTalkDictation.Stt;

namespace PushToTalkDictation;

public enum DictationState
{
    Inactive,
    Idle,
    Recording,
    Transcribing
}

/// <summary>
/// The pipeline. Hotkey -> record -> queue -> transcribe -> inject.
///
/// Threading contract:
///   * Hotkey events arrive on the UI/message-pump thread and return immediately;
///     the actual start/stop is dispatched to the thread pool. The UI thread is never
///     blocked on audio or inference.
///   * Finished clips go into an unbounded channel drained by exactly one consumer,
///     so transcription is serialised and injection happens in the order the user
///     spoke — a second utterance started while the first is still decoding is
///     queued, not dropped and not interleaved.
/// </summary>
public sealed class DictationController : IAsyncDisposable
{
    private readonly HotkeyWatcher _hotkeys;
    private readonly GlobalKeyboardHook _hook;
    private readonly IAudioRecorder _recorder;
    private readonly ISpeechToTextEngine _engine;
    private readonly ITextInjector _injector;
    private readonly AppSettings _settings;
    private readonly ILogger<DictationController> _logger;

    private readonly Channel<AudioClip> _queue = Channel.CreateUnbounded<AudioClip>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = false });

    private readonly CancellationTokenSource _shutdown = new();
    private readonly SemaphoreSlim _captureGate = new(1, 1);
    private readonly Task _consumer;

    private DictationState _state = DictationState.Inactive;
    private int _pendingClips;

    public event EventHandler<DictationState>? StateChanged;
    public event EventHandler<string>? TranscriptionProduced;
    public event EventHandler<Exception>? Failed;

    public bool IsActive { get; private set; }

    public string EngineName => _engine.Name;

    /// <summary>Whether the model currently occupies memory.</summary>
    public bool IsModelLoaded => _engine.IsReady;

    public HotkeyCombo Combo => _hotkeys.Combo;

    public DictationState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(this, value);
        }
    }

    public DictationController(
        HotkeyWatcher hotkeys,
        GlobalKeyboardHook hook,
        IAudioRecorder recorder,
        ISpeechToTextEngine engine,
        ITextInjector injector,
        AppSettings settings,
        ILogger<DictationController> logger)
    {
        _hotkeys = hotkeys;
        _hook = hook;
        _recorder = recorder;
        _engine = engine;
        _injector = injector;
        _settings = settings;
        _logger = logger;

        _hotkeys.HoldStarted += OnHoldStarted;
        _hotkeys.HoldEnded += OnHoldEnded;

        _consumer = Task.Run(() => ConsumeAsync(_shutdown.Token));
    }

    // ----------------------------------------------------------- on/off state

    public void SetActive(bool active)
    {
        if (IsActive == active) return;
        IsActive = active;

        if (active)
        {
            _hook.Install();
            _hotkeys.Enable();
            State = DictationState.Idle;

            // Reloads the model if switching off released it; a no-op when it is still
            // loaded, and skipped entirely when WarmUpOnStart is false - in which case
            // the first utterance pays the load instead.
            _ = WarmUpAsync();

            _logger.LogInformation("Dictation active. Hold {Combo} to talk.", _hotkeys.Combo);
        }
        else
        {
            _hotkeys.Disable();
            _hook.Uninstall();
            State = DictationState.Inactive;
            _ = Task.Run(() => _recorder.CancelAsync());
            _logger.LogInformation("Dictation inactive. Keyboard hooks removed.");

            if (_settings.SpeechToText.UnloadOnInactive)
                _ = UnloadWhenDrainedAsync();
        }
    }

    /// <summary>
    /// Releases the model once the queue is empty. Clips already spoken are still
    /// transcribed and typed first - switching off means "stop listening", not "discard
    /// what I just said" - so this waits for them rather than cancelling them.
    /// </summary>
    private async Task UnloadWhenDrainedAsync()
    {
        try
        {
            while (Volatile.Read(ref _pendingClips) > 0)
                await Task.Delay(100, _shutdown.Token).ConfigureAwait(false);

            // Switched back on while the queue drained - leave the model where it is.
            if (IsActive) return;

            await _engine.UnloadAsync().ConfigureAwait(false);
            _logger.LogInformation("Model released while inactive; it reloads on the next activation.");
        }
        catch (OperationCanceledException)
        {
            // Shutting down. DisposeAsync frees the engine anyway.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to release the model.");
        }
    }

    /// <summary>Loads the model ahead of the first utterance so the first hit is not slow.</summary>
    public Task WarmUpAsync() => Task.Run(async () =>
    {
        if (!_settings.SpeechToText.WarmUpOnStart) return;

        // Nothing to warm up for while switched off, if being switched off is what
        // releases the model - otherwise starting inactive would still hold it resident.
        if (!IsActive && _settings.SpeechToText.UnloadOnInactive) return;

        try
        {
            await _engine.InitializeAsync(_shutdown.Token).ConfigureAwait(false);
            _logger.LogInformation("STT engine '{Engine}' ready.", _engine.Name);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to warm up the STT engine.");
            Failed?.Invoke(this, ex);
        }
    });

    // --------------------------------------------------------- capture events

    private void OnHoldStarted(object? sender, EventArgs e)
    {
        // Returns immediately: the hook callback must not wait on audio device setup.
        _ = Task.Run(async () =>
        {
            await _captureGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                await _recorder.StartAsync(_shutdown.Token).ConfigureAwait(false);
                State = DictationState.Recording;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not start recording.");
                Failed?.Invoke(this, ex);
                State = DictationState.Idle;
            }
            finally
            {
                _captureGate.Release();
            }
        });
    }

    private void OnHoldEnded(object? sender, bool cancelled)
    {
        _ = Task.Run(async () =>
        {
            await _captureGate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
            try
            {
                if (cancelled)
                {
                    await _recorder.CancelAsync().ConfigureAwait(false);
                    return;
                }

                var clip = await _recorder.StopAsync(_shutdown.Token).ConfigureAwait(false);

                if (!IsWorthTranscribing(clip))
                {
                    _logger.LogDebug("Discarded clip ({Ms:F0} ms, RMS {Rms:F4}).",
                        clip.Duration.TotalMilliseconds, clip.Rms);
                    return;
                }

                Interlocked.Increment(ref _pendingClips);
                _queue.Writer.TryWrite(clip);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not finish recording.");
                Failed?.Invoke(this, ex);
            }
            finally
            {
                _captureGate.Release();
                UpdateIdleState();
            }
        });
    }

    private bool IsWorthTranscribing(AudioClip clip)
    {
        if (clip.Samples.Length == 0) return false;
        if (clip.Duration.TotalMilliseconds < _settings.Audio.MinClipMs) return false;
        if (clip.Rms < _settings.Audio.SilenceRmsThreshold) return false;
        return true;
    }

    // ---------------------------------------------------------- consumer loop

    private async Task ConsumeAsync(CancellationToken ct)
    {
        try
        {
            await foreach (var clip in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    State = DictationState.Transcribing;

                    var text = await _engine.TranscribeAsync(clip, ct).ConfigureAwait(false);

                    if (string.IsNullOrWhiteSpace(text))
                    {
                        _logger.LogDebug("Engine returned no text for a {Ms:F0} ms clip.",
                            clip.Duration.TotalMilliseconds);
                        continue;
                    }

                    if (_settings.Injection.AppendTrailingSpace && !text.EndsWith(' '))
                        text += " ";

                    _logger.LogInformation("Transcribed {Chars} characters from {Ms:F0} ms of audio.",
                        text.Length, clip.Duration.TotalMilliseconds);

                    TranscriptionProduced?.Invoke(this, text);

                    await _injector.InjectAsync(text, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Transcription or injection failed.");
                    Failed?.Invoke(this, ex);
                }
                finally
                {
                    Interlocked.Decrement(ref _pendingClips);
                    UpdateIdleState();
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private void UpdateIdleState()
    {
        if (!IsActive)
        {
            State = DictationState.Inactive;
            return;
        }

        if (_recorder.IsRecording)
        {
            State = DictationState.Recording;
            return;
        }

        State = Volatile.Read(ref _pendingClips) > 0
            ? DictationState.Transcribing
            : DictationState.Idle;
    }

    public async ValueTask DisposeAsync()
    {
        _hotkeys.HoldStarted -= OnHoldStarted;
        _hotkeys.HoldEnded -= OnHoldEnded;

        _queue.Writer.TryComplete();
        _shutdown.Cancel();

        try { await _consumer.ConfigureAwait(false); }
        catch (OperationCanceledException) { /* expected */ }

        await _engine.DisposeAsync().ConfigureAwait(false);
        _recorder.Dispose();
        _captureGate.Dispose();
        _shutdown.Dispose();
    }
}
