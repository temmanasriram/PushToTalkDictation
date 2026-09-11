# Troubleshooting

**Start here every time:** tray menu → *Open log folder*, and set `Logging.MinimumLevel` to
`"Debug"` in `appsettings.json` first. The log identifies which of the three stages failed —
capture, decode, injection — and that determines everything below.

A healthy `Debug` cycle looks like:

```
INF HotkeyWatcher: Hotkey watcher enabled for Ctrl+Shift+Space.
INF GlobalKeyboardHook: Global keyboard hook installed.
INF SherpaOnnxSpeechToTextEngine: Loaded Moonshine model from '...' in 640 ms.
DBG AudioRecorder: Capture started on 'Microphone (Realtek)' at 48000 Hz / 2 ch / IeeeFloat.
DBG SherpaOnnxSpeechToTextEngine: Decoded 2.35s of audio in 310 ms (RTF 0.13).
INF DictationController: Transcribed 47 characters from 2350 ms of audio.
```

Whichever of those lines is missing tells you where to look.

---

## Nothing happens at all

**No tray icon.** The app failed during startup. Check the log; if it's empty, the failure was
before logging came up — run the `.exe` from a PowerShell window and read the message box.
Most common cause: `appsettings.json` has a JSON syntax error.

**"Already running" message box.** The single-instance mutex. An earlier instance is still
alive — find it in the tray, or `Stop-Process -Name PushToTalkDictation`.

**Tray icon is grey.** The app is in the Off state. Click the icon → *Toggle Active*. If it
starts grey every time, set `"StartActive": true`.

**Icon is blue, but holding the hotkey does nothing.** In order of likelihood:

1. *Another app owns the combo.* Try a different `Hotkey.Combo` — `Ctrl+Alt+D` is usually free.
2. *The foreground window is elevated.* A non-elevated process cannot hook keys inside a
   higher-integrity window. Test in Notepad first; if it works there, this is your answer —
   see [win32-notes.md](win32-notes.md#uipi-and-integrity-levels).
3. *The hook was detached.* Look for `Keyboard hook was detached by the OS; re-installing.`
   in the log. If that repeats, something in the callback path is blocking — a breakpoint, or
   a modification that made it slow.
4. *You're in modifiers-only mode and another key is being pressed.* `Ctrl+Shift` cancels the
   moment any ordinary key is pressed. That's deliberate; use a trigger-key combo.

---

## Capture stage

**`Capture started on ...` never appears.** No default recording device, or the microphone is
blocked. Check Settings → Privacy → Microphone → *Let desktop apps access your microphone*.
Then Settings → System → Sound → Input, and confirm a device is set as default.

**Wrong microphone.** The log line names the device that was chosen. If it's wrong, set
`Audio.DeviceId` — see [configuration.md](configuration.md#audio).

**`Unsupported capture format` in the log.** The device is handing over something the recorder
doesn't convert (float32, PCM16 and PCM32 are handled). Change the format in Sound Control
Panel → device → Advanced, to 16-bit or 24-bit, 48000 Hz, or add the case to
`AudioRecorder.OnDataAvailable`.

**`Discarded clip (180 ms, RMS 0.0009)`.** The silence gate fired. Either you released too
fast, or the mic is too quiet — raise the input level in Sound settings, or lower
`Audio.SilenceRmsThreshold`. Read an actual rejection line before adjusting the number.

**The last word is missing.** The tail flush wasn't caught. Raise the 500 ms wait in
`AudioRecorder.StopCoreAsync`, or increase the capture buffer from 50 ms.

---

## Decode stage

**`Model directory '...' was not found`.** The message lists **every** path that was tried,
in order — read it before changing anything. Usually the model just isn't downloaded yet: run
`.\scripts\download-models.ps1`, which puts it in the repo's `models\` where a dev build finds
it.

If the model lives somewhere else, point at it rather than moving it:

```jsonc
"SpeechToText": { "ModelRoot": "D:/shared/stt-models" }
```

The full search order is in
[configuration.md](configuration.md#where-models-are-looked-up). Note that models are
deliberately *not* copied into `bin\` on every build — `dotnet publish` is what places them
next to the executable.

**`Model file 'x.onnx' is missing from '...'`.** A different problem: the directory *was*
found, but an expected file inside it isn't there. Either the archive extracted partially, or
it uses different names — override them under `SpeechToText.SherpaOnnx` (`MoonshineEncoder`,
`TransducerEncoder`, …) rather than renaming files.

**`Unable to load DLL 'sherpa-onnx-c-api'` or similar.** The native runtime package didn't
restore for the right RID. Confirm `<RuntimeIdentifier>win-x64</RuntimeIdentifier>` in the
csproj, then `dotnet restore --force`. On x86 or ARM64 you need the matching runtime package.

**First utterance is slow, later ones are fast.** The model loaded lazily. Set
`SpeechToText.WarmUpOnStart: true`.

**Every utterance is slow.** Check RTF in the log. If it's above ~1.0, the model is too big
for the machine — see [models.md](models.md). If RTF is high specifically on *short* clips,
you're on a Whisper model paying its 30-second window; switch to Moonshine.

**Phantom text on an empty tap** — "Thank you.", "[BLANK_AUDIO]", "(silence)". A Whisper
hallucination. Raise `Audio.SilenceRmsThreshold`, or switch to Moonshine or Parakeet, which
don't do this.

**Repeated or looping words.** Whisper carrying context between utterances. Confirm
`SpeechToText.WhisperNet.NoContext: true`.

**The HTTP engine returns nothing.** The log prints the status code and the first 500 bytes of
the response body. `curl` the same endpoint by hand to separate a server problem from a client
one.

---

## Injection stage

**Log says `Transcribed N characters` but no text appears.**

1. *Higher-integrity target.* The usual cause. Test in Notepad. See
   [win32-notes.md](win32-notes.md#uipi-and-integrity-levels).
2. *`SendInput delivered 0/N events` in the log.* Same thing, now confirmed — the OS refused
   the injection.
3. *Focus moved.* Text goes wherever focus is *when injection runs*, not when you started
   speaking. Clicking elsewhere while transcribing sends it there.
4. *The clipboard path failed silently in an app that ignores `Ctrl+V`* — some terminals use
   `Ctrl+Shift+V`. Set `Injection.ClipboardThreshold: 0` to always type instead.

**Text appears but triggers shortcuts / opens menus.** A modifier was still held when injection
started. Raise `Injection.WaitForModifierReleaseMs`. If it persists, the target app may be
latching modifier state — dictate a short phrase and release the keys deliberately before the
text lands.

**Characters missing or out of order in one specific app.** Set `Injection.KeystrokeDelayMs`
to `1` or `2`. Some editors with heavy input processing can't keep up with a zero-delay burst.

**Clipboard contents replaced.** The clipboard fast path restores the previous contents, but
only what `IDataObject` could round-trip; some rich formats don't survive. Set
`ClipboardThreshold: 0` to disable the path entirely.

**Accented characters or emoji come out wrong.** Shouldn't happen —
`KEYEVENTF_UNICODE` is layout-independent. If it does, the target app is doing its own key
translation; the clipboard path is the workaround.

---

## Build and dev issues

**`NETSDK1083: The specified RuntimeIdentifier 'win-x64' is not recognized`.** Older SDK.
Install .NET 8 SDK and check `dotnet --list-sdks`.

**WinForms types not found.** The csproj needs `<UseWindowsForms>true</UseWindowsForms>` and
a `net8.0-windows` target. Both are set; a broken IDE cache is more likely — delete `bin`
and `obj`, reload.

**Whisper.net or sherpa-onnx API mismatch after a package bump.** These are the two places
the managed code touches third-party APIs that move:
`Stt/WhisperNetSpeechToTextEngine.cs` (the `CreateBuilder()` fluent chain) and
`Stt/SherpaOnnxSpeechToTextEngine.cs` (the `config.ModelConfig.*` field names). Pin the
versions in the csproj if you don't want to track them.

**Debugging freezes the keyboard.** You put a breakpoint in the hook callback. See the warning
in [getting-started.md](getting-started.md#working-in-visual-studio) — this is expected
behaviour, not a bug. Wait out the timeout or kill the debugger.

**Antivirus quarantines the build output.** A global keyboard hook plus synthesised keystrokes
is, at the Win32 level, exactly a keylogger. Add an exclusion for the project folder on your
dev box; expect to sign the binary before deploying anywhere managed.
