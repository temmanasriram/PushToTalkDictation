# Choosing a speech model

## The fact that decides everything

Every Whisper variant — including Distil-Whisper and every quantised GGML build — pads its
input to a **fixed 30-second mel window**. A 1.5-second *"open the config file"* costs roughly
the same compute as a 30-second monologue.

For push-to-talk, where nearly every clip is 1–6 seconds, that padding *is* the latency floor.
Quantisation shrinks the model and the RAM; it does not remove the window.

**Moonshine** was designed specifically to fix this. It has no fixed window, so compute scales
with the actual length of the utterance. That is the entire reason it is this app's default —
not benchmark WER, which is close.

Whisper's second dictation-specific problem is hallucination on near-silence: tap the hotkey
without speaking and it will confidently emit `[BLANK_AUDIO]`, `(silence)`, or a stray
*"Thank you."* The app mitigates this with an RMS gate and an artefact regex
(`SpeechToTextEngineBase.Normalize`), but the model-level fix is to not use Whisper here.

## Recommendation

| Rank | Model | Size (int8) | `Engine` / `ModelKind` | Verdict |
|---|---|---|---|---|
| **1** | **Moonshine Base (en)**, ~245M | ~200 MB | `SherpaOnnx` / `Moonshine` | **Default.** Matches or beats Whisper large-v3 on English benchmarks at ~6× smaller, with compute proportional to clip length. Lowest perceived delay of anything here. |
| 2 | Moonshine Tiny (en) | ~80 MB | `SherpaOnnx` / `Moonshine` | Same architecture, smaller. Weak CPU or tight RAM. Noticeably worse on proper nouns and technical terms. |
| 3 | NVIDIA Parakeet TDT 0.6B v2/v3 | ~650 MB | `SherpaOnnx` / `Transducer` | Best raw accuracy of the CPU-viable options, Apache-2.0. The right pick for medical/technical vocabulary or a strong regional accent. Roughly 2–3× Moonshine Base's latency — still under a second for short clips. |
| 4 | `ggml-base.en-q5_1` | ~60 MB | `WhisperNet` | Only if you're already committed to whisper.cpp. Tiny download, very low RAM, but pays the 30-second window on every utterance. |
| 5 | `distil-small.en` | ~330 MB | `WhisperNet` / `Http` | The smallest Distil-Whisper that exists. Good WER, still Whisper-shaped latency. |

## Corrections to the table this project started from

Worth recording, because both errors circulate widely:

- **`distil-whisper-base.en` does not exist.** The published Distil-Whisper checkpoints are
  `distil-small.en` (166M), `distil-medium.en`, `distil-large-v2` and `distil-large-v3`.
  The smallest is `distil-small.en` at roughly 330 MB in fp16 — not a 150 MB "base".
- **`ggml-medium-q4_0` is slower than usually quoted.** On a standard desktop CPU it is
  closer to 3–6 s for a 5-second clip, not 1.5–3 s. Parakeet TDT delivers the same accuracy
  win at a fraction of that, which is why it takes medium's slot above.

The rest of the conventional advice holds: quantised GGML for low RAM, bigger models only
when the vocabulary genuinely demands it.

## Downloading

```powershell
.\scripts\download-models.ps1                          # moonshine-base (default)
.\scripts\download-models.ps1 -Model moonshine-tiny
.\scripts\download-models.ps1 -Model parakeet
.\scripts\download-models.ps1 -Model whisper-base-q5
.\scripts\download-models.ps1 -Model distil-whisper-small
```

The script prints the exact `appsettings.json` keys to change when it finishes.

Models land in the repo's `models\` folder and are found from there without any build-time
copy — relative model paths are probed against the executable's directory,
`%LOCALAPPDATA%\PushToTalkDictation`, and the directories above the executable.
`dotnet publish` copies `models\` next to the published executable, so a shipped folder is
movable as a unit. Full order in [configuration.md](configuration.md#where-models-are-looked-up).

To add a model to an install that's already published, download straight into it:

```powershell
.\scripts\download-models.ps1 -Destination 'C:\Apps\PushToTalkDictation\models'
```

sherpa-onnx archives come from the `asr-models` release tag on
`github.com/k2-fsa/sherpa-onnx`; GGML files come from Hugging Face. Windows 10+ ships bsdtar,
which reads `.tar.bz2` natively, so no extra tooling is needed.

## Switching by hand

```jsonc
// Moonshine — the default
"SpeechToText": {
  "Engine": "SherpaOnnx",
  "SherpaOnnx": {
    "ModelKind": "Moonshine",
    "ModelDirectory": "models/sherpa-onnx-moonshine-base-en-int8"
  }
}

// Parakeet TDT
"SpeechToText": {
  "Engine": "SherpaOnnx",
  "SherpaOnnx": {
    "ModelKind": "Transducer",
    "ModelDirectory": "models/sherpa-onnx-nemo-parakeet-tdt-0.6b-v2-int8",
    "TransducerModelType": "nemo_transducer"
  }
}

// whisper.cpp
"SpeechToText": {
  "Engine": "WhisperNet",
  "WhisperNet": { "ModelPath": "models/ggml-base.en-q5_1.bin" }
}

// local faster-whisper server
"SpeechToText": {
  "Engine": "Http",
  "Http": {
    "Endpoint": "http://127.0.0.1:8000/v1/audio/transcriptions",
    "Model": "Systran/faster-distil-whisper-small.en"
  }
}
```

A sherpa-onnx model directory must contain `tokens.txt` plus the ONNX files the kind expects.
Moonshine archives ship `preprocess.onnx`, `encode.int8.onnx`, `uncached_decode.int8.onnx`,
`cached_decode.int8.onnx`. Transducer archives ship `encoder/decoder/joiner.int8.onnx`.
If an archive uses different names, override them in `SpeechToText.SherpaOnnx` rather than
renaming files — the settings exist for exactly that.

## Measuring, rather than guessing

Set `Logging.MinimumLevel` to `Debug`. Every decode logs:

```
Decoded 2.35s of audio in 310 ms (RTF 0.13).
```

**RTF** (real-time factor) is decode time ÷ audio duration. Below ~0.3 feels instant for
dictation; above ~1.0 feels sluggish. Watch how RTF changes with clip length — for Moonshine
it stays roughly flat, for any Whisper it climbs sharply as clips get *shorter*, which is the
30-second window showing up in the numbers.

`NumThreads` is the other dial. Start at your physical core count and try one step down.

## GPU

`Provider: "cuda"` or `"directml"` for sherpa-onnx, or the CUDA/Vulkan runtime packages for
Whisper.net. Worth it only for Parakeet or a Whisper medium/large; Moonshine Base on a modern
CPU is already fast enough that GPU init cost dominates. Swapping the provider means adding
the matching native runtime NuGet package — the managed code does not change.

## Sources

- [Local STT models 2026: Moonshine vs Parakeet vs Whisper](https://www.onresonant.com/resources/local-stt-models-2026)
- [Best open-source STT model in 2026, with benchmarks](https://northflank.com/blog/best-open-source-speech-to-text-stt-model-in-2026-benchmarks)
- [Whisper.net on NuGet](https://www.nuget.org/packages/Whisper.net/)
- [org.k2fsa.sherpa.onnx on NuGet](https://www.nuget.org/packages/org.k2fsa.sherpa.onnx)
- [sherpa-onnx NeMo transducer models](https://k2-fsa.github.io/sherpa/onnx/pretrained_models/offline-transducer/nemo-transducer-models.html)
- [Distil-Whisper model list](https://huggingface.co/distil-whisper)
