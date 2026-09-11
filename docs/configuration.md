# Configuration

Most settings can be changed in the **settings window** — tray menu → *Settings…* — and take
effect immediately. This document is the full reference, including the keys the window does
not expose.

## The two layers

| File | What it is |
|---|---|
| `appsettings.json` | The documented defaults, shipped beside the executable. Commented; not written to by the app. |
| `appsettings.user.json` | Only the values you changed, written by the settings window. Layered on top. |

Environment variables (below) still win over both. Deleting `appsettings.user.json` returns
everything to the shipped defaults, and the settings window deletes it for you if you revert
every change.

Editing `appsettings.json` by hand still works — the tray's *Open settings file* item opens
it, and the settings window's *Advanced…* button does the same. A hand edit needs a restart;
only changes made through the window apply live.

## What applies without a restart

Everything the window exposes except the **speech engine**, which is a singleton holding a
loaded native model. The window says so when you change it. Editing model file names or paths
in `appsettings.json` also needs a restart, as does `Logging.FilePath`.

The file allows `//` comments and trailing commas (the .NET JSON configuration provider is
configured for both).

Any key can also be set as an environment variable with the `PTTD_` prefix and `__` as the
separator:

```powershell
$env:PTTD_SpeechToText__Engine = "WhisperNet"
$env:PTTD_Logging__MinimumLevel = "Debug"
```

Environment variables win over the file — handy for debugging without dirtying the repo.

---

## Hotkey

| Key | Default | Meaning |
|---|---|---|
| `Combo` | `"Ctrl+Shift+Space"` | Modifiers plus at most one ordinary key. |
| `SuppressTriggerKey` | `true` | Swallow the trigger key so the focused app never receives it. Modifiers are never swallowed. |
| `ModifiersOnlyHoldMs` | `250` | Modifiers-only combos must be held this long before recording arms. Ignored when the combo has a trigger key. |
| `MaxRecordingSeconds` | `120` | Hard stop, so a stuck key can't record forever. |

**Combo syntax.** `+`-separated. Modifier names: `Ctrl`/`Control`, `Shift`, `Alt`/`Menu`,
`Win`/`Windows`/`Meta`/`Super`. The ordinary key is any `System.Windows.Forms.Keys` name —
`Space`, `F9`, `D`, `Oem3`, `NumPad0` — or a single letter/digit.

Valid examples:

```jsonc
"Combo": "Ctrl+Shift+Space"   // default
"Combo": "Ctrl+Alt+D"
"Combo": "F9"                 // no modifiers at all
"Combo": "Ctrl+Shift"         // modifiers-only mode
"Combo": "Win+Oem3"           // Win + backtick
```

Modifiers must match **exactly**: a `Ctrl+Shift` combo does not fire while Alt is also held.

Picking a combo: avoid anything the target apps use. `Ctrl+Shift+Space` is a non-breaking
space in Word and a parameter hint in Visual Studio — with `SuppressTriggerKey: true` the app
swallows it, but if you dictate heavily into those, `Ctrl+Alt+D` or a spare function key is
quieter. `Ctrl+Shift` alone additionally collides with the Windows keyboard-layout switcher.

---

## Audio

| Key | Default | Meaning |
|---|---|---|
| `DeviceId` | `null` | WASAPI endpoint id. `null`/empty = default capture device. |
| `TargetSampleRate` | `16000` | What the clip is resampled to. Every supported model wants 16 kHz; don't change it. |
| `MinClipMs` | `250` | Clips shorter than this are discarded before inference. |
| `SilenceRmsThreshold` | `0.0025` | Clips quieter than this RMS are discarded. |

To find an endpoint id:

```powershell
Get-PnpDevice -Class AudioEndpoint | Where-Object Status -eq OK | Format-Table FriendlyName, InstanceId
```

Or run the app at `Debug` level and read the `Capture started on '<name>'` line in the log to
confirm which device it picked, then set `DeviceId` only if it picked wrong.

**Tuning the silence gate.** If accidental taps produce phantom text, raise
`SilenceRmsThreshold` (try `0.005`). If quiet speech is being dropped, lower it (`0.001`).
The log at `Debug` prints `Discarded clip (<ms>, RMS <value>)` for every rejection, so read
one real rejection before guessing.

---

## SpeechToText

| Key | Default | Meaning |
|---|---|---|
| `Engine` | `"SherpaOnnx"` | `SherpaOnnx` \| `WhisperNet` \| `Http` |
| `WarmUpOnStart` | `true` | Load the model at launch instead of on first use. Removes a 1–3 s stall on the first utterance. |
| `UnloadOnInactive` | `true` | Release the model from memory while dictation is switched off. |
| `ModelRoot` | `null` | Root that the relative model paths below resolve against. `null` = probe the standard locations. |

### Memory while idle

The model is the app's entire memory footprint. Measured with Moonshine Base:

| State | Working set | Private bytes |
|---|---|---|
| Model loaded | ~313 MB | ~272 MB |
| Model released | ~75 MB | ~33 MB |
| Never loaded (fresh process) | ~48 MB | ~11 MB |

With `UnloadOnInactive: true` (the default), switching **Off** in the tray releases it and
switching **On** loads it again — about a second once the files are in the OS cache, measured
across repeated toggles. The tray menu shows which state it's in. Three details worth knowing:

- The unload waits for the clip queue to drain, so anything you already said still gets typed.
- A clip that arrives while the model is gone reloads it transparently; you never lose an
  utterance to the model being unloaded.
- Starting with `StartActive: false` doesn't load the model at all, rather than loading it
  and immediately releasing it.

Roughly 20 MB of the released state is one-time ONNX Runtime initialisation that doesn't come
back — the native library stays loaded once used. Repeated toggling is stable, not leaky.

Set `UnloadOnInactive: false` to keep the model resident so toggling on is instant. That is
the right choice if you toggle frequently, or if you have RAM to spare and care only about
latency.

### Where models are looked up

`ModelDirectory` and `ModelPath` may be absolute — in which case they are used exactly as
written — or relative, in which case they are tried against these roots **in order**, and the
first hit wins:

| # | Root | Serves |
|---|---|---|
| 1 | `SpeechToText.ModelRoot`, if set | An installer, or a model store shared between builds |
| 2 | The executable's own directory | A published install — `dotnet publish` copies `models\` here |
| 3 | `%LOCALAPPDATA%\PushToTalkDictation` | A per-user model, writable without elevation, surviving reinstalls |
| 4 | Each directory above the executable, up to five levels | Dev builds — reaches the repo-root `models\` that `download-models.ps1` writes to |

This is why the same `appsettings.json` works from `dotnet build`, from a published folder and
from an installed copy without being edited. Models are **not** copied into the build output on
every build — they are hundreds of megabytes, and that would double every incremental build.

If nothing matches, the log and the error balloon list every path that was tried, so a
misconfigured model is never a guessing game. Setting `ModelRoot` skips the search:

```jsonc
"SpeechToText": {
  "ModelRoot": "D:/shared/stt-models",
  "SherpaOnnx": { "ModelDirectory": "sherpa-onnx-moonshine-base-en-int8" }
}
```

A relative `ModelRoot` is itself relative to the executable, so an installer can point at a
sibling folder without knowing the install path.

### SpeechToText.SherpaOnnx

| Key | Default | Meaning |
|---|---|---|
| `ModelKind` | `"Moonshine"` | `Moonshine` \| `Transducer` \| `Whisper` \| `SenseVoice` |
| `ModelDirectory` | `"models/sherpa-onnx-moonshine-base-en-int8"` | Absolute, or relative to one of the roots above. |
| `NumThreads` | `4` | Set to your **physical** core count. |
| `Provider` | `"cpu"` | `cpu` \| `cuda` \| `directml` (needs the matching native runtime package). |
| `DecodingMethod` | `"greedy_search"` | `greedy_search` is right for dictation. |
| `Tokens` | `"tokens.txt"` | |
| `Moonshine*` | see file | The four ONNX files in a Moonshine archive. |
| `Transducer*` | see file | Encoder/decoder/joiner + `TransducerModelType` (`nemo_transducer`). |

Hyperthreads usually *hurt* ONNX Runtime throughput on this workload — 4 physical cores beat
8 logical ones here more often than not. Measure with `Logging.MinimumLevel: "Debug"`, which
prints decode time and real-time factor per clip.

### SpeechToText.WhisperNet

| Key | Default | Meaning |
|---|---|---|
| `ModelPath` | `"models/ggml-base.en-q5_1.bin"` | Any whisper.cpp GGML file. Absolute, or relative to one of the roots above. |
| `Language` | `"en"` | |
| `NumThreads` | `4` | |
| `NoContext` | `true` | Don't carry context between utterances. Leave on — carrying it is what causes runaway repetition on short clips. |

### SpeechToText.Http

| Key | Default | Meaning |
|---|---|---|
| `Endpoint` | `http://127.0.0.1:8000/v1/audio/transcriptions` | OpenAI-compatible transcription endpoint. |
| `Model` | `"Systran/faster-distil-whisper-small.en"` | Passed as the `model` form field. |
| `Language` | `"en"` | |
| `TimeoutSeconds` | `30` | |
| `ApiKey` | `null` | Sent as `Authorization: Bearer` when set. |

Works unmodified against faster-whisper-server / Speaches, whisper.cpp's `server` example,
and anything else returning `{"text": "..."}`.

---

## Injection

| Key | Default | Meaning |
|---|---|---|
| `KeystrokeDelayMs` | `0` | Per-batch delay. Raise to `1`–`2` only if a specific target drops characters. |
| `ClipboardThreshold` | `240` | Paste via clipboard above this length. `0` = always type. |
| `WaitForModifierReleaseMs` | `400` | Wait for physical Ctrl/Shift/Alt/Win to come up before typing. |
| `AppendTrailingSpace` | `true` | Append one space so consecutive dictations don't run together. |
| `RestoreTargetWindow` | `true` | Type into the window that was focused when you started speaking, restoring focus to it first if it moved. |

**Why `RestoreTargetWindow` exists:** text is typed a second or more after you stop speaking,
and `SendInput` goes wherever focus is at that moment — so anything that takes focus in the
meantime receives your transcript instead. Opening the tray menu to watch the status does
exactly that. With this on, the app remembers the window you were speaking into and restores it
first. It is best-effort: it cannot reach a higher-integrity window, and it does nothing if the
window has closed. Set it to `false` for the old always-type-into-the-foreground behaviour.

**Why the modifier wait matters:** if injection starts while you're still holding Ctrl, the
target app sees `Ctrl+<char>` and runs shortcuts instead of inserting text. The injector waits,
then sends modifier key-ups as a belt-and-braces.

**When to disable the clipboard path:** set `ClipboardThreshold: 0` if you use a clipboard
manager that would capture the transient contents, or if a target app ignores `Ctrl+V`
(some terminals use `Ctrl+Shift+V`). Typing is slower but always works.

---

## Logging

| Key | Default | Meaning |
|---|---|---|
| `MinimumLevel` | `"Information"` | `Trace` \| `Debug` \| `Information` \| `Warning` \| `Error` \| `Critical` |
| `FilePath` | `"logs/app.log"` | Relative paths resolve under `%LOCALAPPDATA%\PushToTalkDictation`. |

`Debug` adds per-clip decode timings, real-time factors, the chosen capture device and format,
and a line for every discarded clip. That is the level to be at when tuning anything.

The file rolls at 4 MB. Writes are queued on a background thread so logging never blocks the
hook callback or the audio thread.

---

## Top level

| Key | Default | Meaning |
|---|---|---|
| `StartActive` | `true` | Whether the keyboard hook is installed at launch. Set `false` if you want to opt in from the tray each session. |
