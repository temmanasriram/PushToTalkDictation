# Documentation

Push-to-Talk Dictation — a background Windows tray app that records while you hold a hotkey,
transcribes locally, and types the result into the focused window.

| Doc | Read it when |
|---|---|
| [**user-guide.md**](user-guide.md) | **You just want to use it** — install, the hotkey, settings, limits |
| [getting-started.md](getting-started.md) | Setting up the dev box, first build, debugging in VS / VS Code |
| [architecture.md](architecture.md) | You need the component map, the data flow, or the threading contract |
| [configuration.md](configuration.md) | You want to change behaviour without touching code |
| [models.md](models.md) | Choosing or swapping the speech model |
| [extending.md](extending.md) | Adding an STT engine, or replacing the recorder / injector |
| [win32-notes.md](win32-notes.md) | You're touching the keyboard hook or `SendInput` |
| [troubleshooting.md](troubleshooting.md) | Something doesn't work |

## Thirty-second summary

```
hold hotkey ──▶ WASAPI capture ──▶ 16 kHz mono float PCM ──▶ Channel<AudioClip>
                                                                    │
      focused window  ◀── SendInput (Unicode)  ◀── ISpeechToTextEngine
```

Four replaceable pieces behind interfaces: `GlobalKeyboardHook` (input),
`IAudioRecorder` (capture), `ISpeechToTextEngine` (recognition), `ITextInjector` (output).
`DictationController` wires them together and owns the threading rules.

## Ground rules the code depends on

1. **The hook callback never blocks.** It does bookkeeping and returns. Windows detaches a
   low-level hook whose callback overruns `LowLevelHooksTimeout`.
2. **Everything expensive runs on the thread pool.** Device open, capture, resampling,
   inference, injection.
3. **Injected keystrokes are tagged** with a `dwExtraInfo` signature so the app's own hook
   ignores them.
4. **One consumer drains the clip queue**, so text is injected in the order it was spoken.

## Current status

Implemented and complete: tray + menu, on/off state, configurable hold-to-talk hotkey,
WASAPI capture with resampling, three STT engines, Unicode injection with a clipboard fast
path, file logging, single-instance guard.

Not implemented: an in-app settings UI (edit `appsettings.json` and restart), streaming /
partial results, per-application profiles, custom vocabulary biasing. See
[extending.md](extending.md#ideas-not-yet-built) for notes on each.
