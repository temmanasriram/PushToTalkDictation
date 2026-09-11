# Architecture

## Components

| Type | File | Responsibility |
|---|---|---|
| `Program` | `Program.cs` | Config binding, DI container, single-instance mutex, global exception handlers |
| `TrayApplicationContext` | `Tray/` | The only UI. `NotifyIcon` + context menu, no window |
| `DictationController` | root | Orchestration, state machine, the threading contract |
| `GlobalKeyboardHook` | `Input/` | `WH_KEYBOARD_LL` install/remove, raw key events, watchdog |
| `HotkeyCombo` | `Input/` | Parses `"Ctrl+Shift+Space"` into modifiers + trigger key |
| `HotkeyWatcher` | `Input/` | Raw keys → `HoldStarted` / `HoldEnded` |
| `AudioRecorder` | `Audio/` | WASAPI shared-mode capture, downmix, resample |
| `AudioClip` | `Audio/` | The recording, as float PCM / WAV bytes / temp file |
| `ISpeechToTextEngine` | `Stt/` | **The extension point.** Three implementations ship |
| `ModelPathResolver` | `Stt/` | Finds model assets across the dev, published and per-user layouts |
| `TextInjector` | `Injection/` | `SendInput` + `KEYEVENTF_UNICODE`, clipboard fast path |
| `FileLoggerProvider` | `Diagnostics/` | Queued, non-blocking append-only log |

Every arrow between these is an interface or an event. Nothing reaches across two layers.

## Data flow

```mermaid
sequenceDiagram
    participant U as User
    participant H as GlobalKeyboardHook<br/>(UI thread)
    participant W as HotkeyWatcher<br/>(UI thread)
    participant C as DictationController
    participant R as AudioRecorder<br/>(thread pool)
    participant Q as Channel&lt;AudioClip&gt;
    participant E as ISpeechToTextEngine<br/>(single consumer)
    participant I as TextInjector

    U->>H: Ctrl+Shift+Space down
    H->>W: KeyEvent (returns in µs)
    W->>C: HoldStarted
    C-->>R: Task.Run(StartAsync)
    R->>R: open WASAPI, buffer mono floats

    U->>H: key up
    H->>W: KeyEvent
    W->>C: HoldEnded(cancelled: false)
    C-->>R: Task.Run(StopAsync)
    R->>R: flush, resample to 16 kHz
    R-->>C: AudioClip
    C->>C: RMS + duration gate
    C->>Q: TryWrite(clip)

    Q->>E: TranscribeAsync(clip)
    E-->>Q: text
    Q->>I: InjectAsync(text)
    I->>U: keystrokes into focused window
```

## Threading contract

This is the part to preserve when changing things.

**UI / message-pump thread** owns three things and nothing else may touch them:
the `NotifyIcon`, the `System.Windows.Forms.Timer` instances inside `HotkeyWatcher` and
`GlobalKeyboardHook`, and hook install/remove. `GlobalKeyboardHook` asserts this with
`EnsureOwnerThread()` — a low-level hook is delivered on the message loop of the thread that
installed it, so installing from elsewhere produces a hook that never fires.

**The hook callback** runs on that same thread, synchronously, inside the OS input path for
the entire desktop. It reads key state, updates flags, raises an event, returns. It must not
allocate heavily, must not log to disk synchronously (the logger queues), and must not wait
on anything. Exceptions are caught inside the callback because letting one escape into
unmanaged code tears down the process.

**Thread pool** does the rest. `DictationController.OnHoldStarted` / `OnHoldEnded` return
immediately after dispatching with `Task.Run`, so opening the capture device (tens of
milliseconds) never stalls the user's keyboard.

**`_captureGate` (SemaphoreSlim(1,1))** serialises start against stop. A very fast tap can
deliver `HoldEnded` while `StartAsync` is still opening the device; the gate makes stop wait
for start rather than racing it.

**The engine session gate.** `SpeechToTextEngineBase` serialises load, decode and unload
behind one `SemaphoreSlim(1,1)`. That is what makes releasing the model safe — an unload
cannot free the native session while inference is running, and a decode cannot start against
a session being freed — and it means engines need no decode lock of their own.

**One consumer.** Clips go into an unbounded `Channel<AudioClip>` with `SingleReader = true`.
`ConsumeAsync` is the only thing that calls the engine and the injector. Two consequences
that matter: transcription is serialised (so a single native model session is never re-entered),
and text is injected in the order it was spoken even if you start a second utterance while
the first is still decoding.

**UI updates from the pipeline** go through `SynchronizationContext.Post`, captured in the
`TrayApplicationContext` constructor.

## Hold state machine

`HotkeyWatcher` has two modes, chosen by whether `Hotkey.Combo` contains a non-modifier key.

**Trigger-key mode** (`Ctrl+Shift+Space`, the default):

```
             trigger down && modifiers exactly match
  Idle ─────────────────────────────────────────────▶ Holding
    ◀───────────────────────────────────────────────
       trigger up  │  a required modifier released
```

Auto-repeat is filtered with a `_triggerDown` flag. The trigger key is swallowed
(`e.Handled = true`) when `SuppressTriggerKey` is set, so the host app never sees the space.
Modifiers are never swallowed.

**Modifiers-only mode** (`Ctrl+Shift`):

```
  Idle ──modifiers match──▶ Arming ──held ModifiersOnlyHoldMs──▶ Holding
    ◀──modifiers change────    ◀── any other key pressed ────────
```

The arming delay and the cancel-on-other-key rule exist because bare `Ctrl+Shift` collides
with the Windows keyboard-layout switcher and fires on every `Ctrl+Shift+<anything>` shortcut.
This mode is supported but is not the default for that reason.

`ModifiersSatisfied` requires an **exact** match, not a subset — holding `Ctrl+Shift+Alt`
does not satisfy a `Ctrl+Shift` combo. That prevents the hotkey firing inside larger shortcuts.

One subtlety: `GetAsyncKeyState` still reports a modifier as down while the hook callback for
its own key-up is executing, so `CurrentModifiers()` takes the event being processed and
clears that bit explicitly.

## Audio path

WASAPI shared mode hands over the device's own mix format — typically 32-bit float, 48 kHz,
stereo. Two stages:

1. **On the capture callback:** downmix to mono at the native rate by averaging channels.
   Cheap, no per-buffer allocation beyond growing a `List<float>`. Encodings handled:
   IEEE float 32, PCM 16, PCM 32.
2. **At Stop, on the thread pool:** resample the whole buffer to 16 kHz with NAudio's
   `WdlResamplingSampleProvider` (pure managed — no Media Foundation dependency).

Doing the rate conversion once at the end rather than streaming costs a millisecond or two
for a normal utterance and keeps the audio callback trivial.

The recorder waits up to 500 ms for `RecordingStopped` after `StopRecording()`, because WASAPI
flushes its last buffers asynchronously and skipping the wait clips the end of the utterance.

Capture buffer is 50 ms with event sync, which bounds how much tail latency the release adds.

## Silence gate

Before a clip reaches the engine, `DictationController.IsWorthTranscribing` drops it if it is
shorter than `Audio.MinClipMs` or quieter than `Audio.SilenceRmsThreshold`. This exists
because Whisper-family models hallucinate confidently on near-silence — an accidental tap
produces "Thank you." or `[BLANK_AUDIO]` rather than nothing. `SpeechToTextEngineBase.Normalize`
strips the bracketed artefacts as a second line of defence.

## Injection path

`KEYEVENTF_UNICODE` sends the literal UTF-16 code unit rather than a scan code the target
re-maps, so output is independent of the user's keyboard layout and spacing and punctuation
survive verbatim. Details in [win32-notes.md](win32-notes.md).

Above `Injection.ClipboardThreshold` characters the injector pastes instead of typing —
a 400-character `SendInput` burst is visibly slow and some editors drop characters under it.
The previous clipboard contents are saved and restored.

## State model

```
Inactive ──SetActive(true)──▶ Idle ──hold──▶ Recording ──release──▶ Transcribing ──▶ Idle
```

`DictationState` drives only the tray icon and tooltip. `Inactive` means hooks are uninstalled
— genuinely uninstalled, not just ignored, which is what "Off" in the menu promises.

`Inactive` also releases the model by default (`SpeechToText.UnloadOnInactive`), which takes
the process from roughly 310 MB to 75 MB. The unload waits for the clip queue to drain first:
switching off means "stop listening", not "throw away what I just said". Switching back on
reloads it in about a second once the files are in the OS cache, and a clip that arrives while
the model is gone reloads it transparently. Set `UnloadOnInactive: false` to keep it resident
so toggling on is instant. The tray menu shows which state the model is in.

## Why these choices

| Decision | Reason |
|---|---|
| `ApplicationContext`, not a hidden `Form` | Still pumps messages (required by the hook) without a window handle, taskbar entry, or Alt+Tab entry. |
| Engines resolved through a factory, all three registered | Only the configured one is ever constructed, and adding a fourth is two lines. |
| Unbounded channel rather than "drop if busy" | Speaking a second sentence while the first decodes is normal use, not an error. |
| Icons drawn at runtime | No binary assets in the repo. Swap `TrayIcons.Build` for `new Icon("app.ico")` when you have artwork. |
| Own file logger instead of Serilog/NLog | One dependency-free file, and full control over the non-blocking write path the hook callback depends on. |
| Model released when switched off, not on a timer | "Off" is an explicit statement that the user is done for now, so it is the one moment a 260 MB release is unambiguously wanted. Idle timers guess, and guess wrong mid-conversation. |
| Settings bound once at startup, no hot reload | A config change mid-utterance has no safe meaning. Restart is honest. |
| Models probed at runtime, copied only on `publish` | A 200 MB copy on every incremental build is a tax paid hundreds of times to help once. Probing lets one `appsettings.json` serve the dev, published and installed layouts unchanged. |
