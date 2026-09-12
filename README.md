# Push-to-Talk Dictation

A background Windows tray app (.NET 8, C#). Hold a hotkey, speak, release — the text is typed
into whatever window has focus. Everything runs locally; no audio leaves the machine.

```
hold hotkey ──▶ WASAPI capture ──▶ 16 kHz mono float PCM ──▶ Channel<AudioClip>
                                                                    │
      focused window  ◀── SendInput (Unicode)  ◀── ISpeechToTextEngine
```

## Quick start

```powershell
dotnet restore
.\scripts\download-models.ps1        # Moonshine Base (en), ~200 MB, into .\models
dotnet build -c Release
.\bin\Release\net8.0-windows\win-x64\PushToTalkDictation.exe
```

A microphone icon appears in the tray. Hold **Ctrl+Shift+Space**, speak, release.
Grey = off, blue = ready, red = recording, amber = transcribing.

Settings are in the tray menu under *Settings…* — the hotkey, microphone, typing behaviour
and the rest, applied immediately. Changes are written to `appsettings.user.json`, leaving the
commented defaults in `appsettings.json` untouched.

Switching **Off** in the tray menu uninstalls the keyboard hook and releases the model,
taking the process from ~310 MB to ~75 MB; switching **On** reloads it in about a second.
Set `SpeechToText.UnloadOnInactive: false` to keep it resident instead.

Full usage instructions are in the [user guide](docs/user-guide.md).

The model stays in `.\models` and is found from there — it is not copied into `bin\` on every
build. To ship, `dotnet publish -c Release` produces a folder with the model beside the
executable that can be zipped and moved to another machine as a unit. Details in
[docs/getting-started.md](docs/getting-started.md#shipping-it).

## Documentation

Everything lives in [`docs/`](docs/README.md):

| Doc | Covers |
|---|---|
| [**user-guide.md**](docs/user-guide.md) | **Using the app** — install, the hotkey, every setting, limits |
| [getting-started.md](docs/getting-started.md) | Prerequisites, first build, debugging in VS / VS Code |
| [architecture.md](docs/architecture.md) | Components, data flow, threading contract, design rationale |
| [configuration.md](docs/configuration.md) | Every `appsettings.json` key |
| [models.md](docs/models.md) | Choosing and swapping the speech model |
| [extending.md](docs/extending.md) | Adding an STT engine; replacing the recorder or injector |
| [win32-notes.md](docs/win32-notes.md) | Hook and `SendInput` internals — read before touching either |
| [troubleshooting.md](docs/troubleshooting.md) | Symptom → cause → fix |

## Source layout

| Path | What |
|---|---|
| `Program.cs` | Composition root — config, DI, single-instance guard, exception handlers |
| `DictationController.cs` | The pipeline and its threading contract |
| `Input/` | `WH_KEYBOARD_LL` hook, combo parser, hold-to-talk state machine |
| `Audio/` | WASAPI capture, resampling, the `AudioClip` type |
| `Stt/` | `ISpeechToTextEngine` + sherpa-onnx, Whisper.net and HTTP implementations; `ModelPathResolver` locates model assets |
| `Injection/` | `SendInput` + `KEYEVENTF_UNICODE`, clipboard fast path |
| `Tray/` | `NotifyIcon`, context menu, runtime-generated icons |
| `Interop/` | The entire P/Invoke surface, in one file |
| `Diagnostics/` | Queued non-blocking file logger |

## Default engine

**Moonshine Base (en)** via sherpa-onnx. Every Whisper variant pads its input to a fixed
30-second window, so a 1.5-second utterance costs what a 30-second one does; Moonshine's
compute scales with actual clip length, which is the whole game for push-to-talk.
Full reasoning and the alternatives in [docs/models.md](docs/models.md).

## Dependencies

| Package | Why |
|---|---|
| `NAudio` 2.2.1 | WASAPI capture and the WDL resampler |
| `org.k2fsa.sherpa.onnx` 1.13.5 | Default offline engine (ONNX Runtime, no Python) |
| `Whisper.net` + `Whisper.net.Runtime` 1.9.1 | whisper.cpp bindings |
| `Microsoft.Extensions.*` 8.x | Configuration, DI, logging, `IHttpClientFactory` |

## Licence

[MIT](LICENSE). Dependency licences and attribution are in
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md); all are permissive (MIT / Apache-2.0).

No speech model is distributed here — `scripts/download-models.ps1` fetches one on your
machine, and each model carries its own licence.

## A note on what this does

To work at all, this app installs a global keyboard hook and synthesises keystrokes. At the
Win32 level that is the same machinery a keylogger uses, so some endpoint security products
flag it. Nothing is transmitted anywhere: audio is captured, transcribed locally and
discarded, and the only file written is the log. The code is short and the whole input path
lives in `Input/` and `Injection/` if you want to check that yourself.
Logs: `%LOCALAPPDATA%\PushToTalkDictation\logs\app.log` (tray menu → *Open log folder*).
Set `Logging.MinimumLevel` to `Debug` for per-clip decode times and real-time factors.
