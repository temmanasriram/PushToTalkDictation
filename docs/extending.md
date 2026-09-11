# Extending

## Adding a speech-to-text engine

The contract is one interface. `SpeechToTextEngineBase` gives you race-free lazy loading,
lazy init on first use, serialised decoding, unload/reload, and artefact stripping — derive
from it rather than implementing `ISpeechToTextEngine` directly unless you need different
lifecycle behaviour.

```csharp
using PushToTalkDictation.Audio;
using PushToTalkDictation.Stt;

public sealed class MyEngine : SpeechToTextEngineBase
{
    private readonly ILogger<MyEngine> _logger;
    private MyNativeSession? _session;

    public override string Name => "my-engine";

    public MyEngine(AppSettings settings, ILogger<MyEngine> logger) => _logger = logger;

    // Called under the session gate, and again after an unload, so make it repeatable.
    // Throw to surface a balloon tip.
    protected override Task LoadAsync(CancellationToken ct) => Task.Run(() =>
    {
        _session = MyNativeSession.Load(/* path from settings */);
    }, ct);

    // The model is guaranteed loaded. Do the work off the calling thread.
    protected override Task<string> RecognizeAsync(AudioClip clip, CancellationToken ct)
        => Task.Run(() => _session!.Transcribe(clip.Samples, clip.SampleRate), ct);

    // Free the model but stay reusable: called when dictation is switched off, and
    // LoadAsync may follow later. Anything the engine needs for its whole lifetime
    // (config, loggers, a HttpClient) must survive this.
    protected override ValueTask UnloadCoreAsync()
    {
        _session?.Dispose();
        _session = null;
        return ValueTask.CompletedTask;
    }
}
```

Then register it — two edits:

```csharp
// Program.cs, in BuildServices()
services.AddSingleton<MyEngine>();
```

```csharp
// Stt/SpeechToTextEngineFactory.cs
SttEngineKind.Mine => services.GetRequiredService<MyEngine>(),
```

…plus a value on the `SttEngineKind` enum in `Configuration/AppSettings.cs` and a settings
class if it needs options. That's the whole extension surface.

### What `AudioClip` gives you

Whatever shape your engine wants:

| Member | Shape |
|---|---|
| `clip.Samples` | `float[]`, mono, 16 kHz, normalised −1..1 — what sherpa-onnx and Whisper.net take |
| `clip.SampleRate` | `int` |
| `clip.Duration` | `TimeSpan` |
| `clip.ToWavBytes()` | 16-bit PCM WAV with headers, in memory — for HTTP uploads |
| `clip.WriteTempWavAsync()` | a temp file path — for engines that only accept files. **You delete it.** |
| `clip.Rms` | for your own gating |

### Contract requirements

- **Thread-safe.** Called from the single pipeline consumer today, but don't assume that.
- **Don't block.** The consumer is a background task, but blocking it stalls the queue and
  the tray state. Wrap native blocking calls in `Task.Run`.
- **Return `string.Empty`, don't throw, for "nothing recognised."** Throwing surfaces an error
  balloon to the user, which is right for a missing model file and wrong for a quiet clip.
- **No locking needed for a non-re-entrant native session.** `SpeechToTextEngineBase` holds a
  single gate across load, decode *and* unload, so `RecognizeAsync` is never called
  concurrently and the model can never be freed mid-decode. Don't add your own decode
  semaphore; it would only be redundant.
- **`LoadAsync` must be repeatable.** With `SpeechToText.UnloadOnInactive` set (the default),
  the model is released when dictation is switched off and loaded again on the next
  activation, so `LoadAsync` runs more than once over a process lifetime. Dispose any
  previous session at the top of it rather than assuming a clean slate.

### Two concrete cases from the original brief

**A local whisper.cpp DLL wrapper.** If you're binding `whisper.dll` yourself rather than
using Whisper.net, `LoadAsync` calls `whisper_init_from_file_with_params`, `RecognizeAsync`
calls `whisper_full` over `clip.Samples` (whisper.cpp wants exactly mono f32 16 kHz — which
is what `AudioClip` already is, no conversion needed) and concatenates `whisper_full_get_segment_text`.
Keep the context pointer in a field and free it in `UnloadCoreAsync`. You need no lock of
your own — the base class already serialises decodes against each other and against unload,
which is what a non-re-entrant `whisper_context` requires.

**A local faster-whisper Python endpoint.** Already shipped as `HttpSpeechToTextEngine` —
point `SpeechToText.Http.Endpoint` at it. If your server isn't OpenAI-compatible, the only
methods to change are the multipart field names in `RecognizeAsync` and `ExtractText`.

## Replacing other pieces

Everything else is behind an interface too:

| Interface | Swap it to… |
|---|---|
| `IAudioRecorder` | use a different capture API, add VAD-based auto-stop, or feed test audio from a file |
| `ITextInjector` | inject via UI Automation instead of `SendInput`, or write to the clipboard only. `InjectAsync` receives the window that had focus when the user started speaking, so an implementation can target it directly |

Register your implementation in `Program.cs` in place of the existing one. `DictationController`
knows nothing about either concrete type.

Feeding a WAV file through `IAudioRecorder` is the easiest way to test engines
deterministically — no microphone, identical input every run.

## Adding a setting to the settings window

Four edits, and the compiler finds three of them for you:

1. The property on the relevant class in `Configuration/AppSettings.cs`.
2. A line in `SettingsStore.Save` (so it is persisted when changed) and one in
   `SettingsStore.CopyInto` (so Save applies it to the live instance). Both are explicit
   lists rather than reflection, so a missing line is a silent no-op - add both.
3. A control and a `Row(...)` call in the matching tab of `Tray/SettingsForm.cs`, plus the
   read/write pair in `ReadFromDraft` and `WriteToDraft`.
4. If it cannot take effect without a restart, add it to the comparison at the top of
   `SettingsService.Apply` so the UI can say so.

Whether a setting applies live comes down to whether its consumer re-reads it. Anything read
per use - the whole of `Injection`, most of `Audio` - works with no extra code, because
`CopyInto` mutates the nested settings objects those components already hold a reference to.
Anything captured in a constructor needs a reconfigure path, as `HotkeyWatcher.Reconfigure`
does for the combo and the timers.

## Ideas not yet built

Notes on the obvious next features, and where they'd go.

**Streaming / partial results.** Show text as you speak rather than on release. Moonshine and
Zipformer both support streaming recognisers in sherpa-onnx (`OnlineRecognizer` rather than
`OfflineRecognizer`). The hard part is injection, not recognition: you'd have to erase and
retype as hypotheses are revised, which is destructive in an arbitrary target window. A
preview overlay window is the safer design.

**Per-application profiles.** `Win32.GetForegroundWindow` + `GetWindowThreadProcessId` are
already declared in `Interop/Win32.cs` for this. Capture the foreground process at
`HoldStarted`, key a settings lookup off it, and you can have different vocabulary,
capitalisation, or trailing-space behaviour in a terminal versus a mail client.

**Custom vocabulary / biasing.** sherpa-onnx transducer models support hotword boosting via
`OfflineRecognizerConfig.HotwordsFile` and `HotwordsScore`. That's the cheapest real accuracy
win for names, product terms and jargon — worth doing before reaching for a bigger model.

**Punctuation and formatting commands.** "new line", "comma", "period" → post-process in
`DictationController.ConsumeAsync` between transcription and injection. Keep it out of the
engines; it's a text transform, not recognition.

**Dictionary of replacements.** Same place — a simple map applied after `Normalize`, to fix
the handful of terms your model reliably gets wrong.
