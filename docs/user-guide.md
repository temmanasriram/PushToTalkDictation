# User guide

Hold a key, speak, let go — your words are typed into whatever window you were using.
Nothing is sent anywhere: the speech model runs on your own machine.

This guide is for using the app. To change how it works internally, see
[architecture.md](architecture.md) and [extending.md](extending.md).

> This page and [user-guide.html](user-guide.html) are the same guide. The HTML copy is the
> one that ships beside the executable and opens from the tray menu's **User guide** item;
> this Markdown copy is here to read on GitHub. **Edit both together.**

---

## 1. Install

### What you need

| | |
|---|---|
| **Windows** | 10 (1809 or newer) or 11, **64-bit** |
| **.NET 8 Desktop Runtime** | `winget install Microsoft.DotNet.DesktopRuntime.8` — not needed if you publish self-contained (below) |
| **A microphone** | Settings → System → Sound → Input |
| **Disk** | ~280 MB for the speech model, ~30 MB for the app |
| **RAM** | ~310 MB while listening, ~75 MB while switched off |

You also need the **.NET 8 SDK** to build it (`winget install Microsoft.DotNet.SDK.8`),
since there is no pre-built download.

### Build a portable install

This is the way to set it up for everyday use. The result is a single folder you can put
anywhere — including a USB stick or another machine.

```powershell
cd C:\source\STTService

dotnet restore
.\scripts\download-models.ps1     # ~280 MB, a few minutes
dotnet publish -c Release
```

Your install is now in:

```
bin\Release\net8.0-windows\win-x64\publish\
```

Copy that folder wherever you want it — `C:\Apps\PushToTalkDictation\` is a reasonable
choice. It holds the app, its settings file, this guide and the speech model together, so it
works from any location.

> **Download the model before publishing.** Publishing with no model prints a build warning
> and produces an app that cannot transcribe anything.

**No .NET on the target machine?** Publish self-contained instead — about 460 MB instead of
300 MB, but it needs nothing installed:

```powershell
dotnet publish -c Release --self-contained
```

### Or run it straight from the build folder

For quick testing, skip publishing:

```powershell
dotnet build -c Release
.\bin\Release\net8.0-windows\win-x64\PushToTalkDictation.exe
```

The model stays in `.\models` and is found from there. Fine for trying it out, but the
folder is not movable — use `dotnet publish` for a real install.

---

## 2. Run it

Double-click **`PushToTalkDictation.exe`**.

Nothing appears to happen — that is correct. There is no window. Look for a **microphone
icon in your system tray**, next to the clock. You may need to click the `^` arrow to see
hidden icons; drag it out if you want it always visible.

The icon colour is the whole status display:

| Colour | Meaning |
|---|---|
| **Grey** | Off — not listening, no keyboard hook installed |
| **Blue** | Ready — hold the hotkey to talk |
| **Red** | Recording right now |
| **Amber** | Transcribing what you just said |

Only one copy runs at a time. Launching it again shows *"Push-to-Talk Dictation is already
running"* — find the existing icon in your tray.

### Start it automatically at login

Press <kbd>Win</kbd>+<kbd>R</kbd>, type `shell:startup`, press Enter, and drop a shortcut to
the published `.exe` into the folder that opens.

It works from anywhere: the app finds its settings and model relative to its own location,
never the working directory.

---

## 3. The shortcut

### Hold <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Space</kbd>, speak, release.

That is the whole interaction. It is *hold-to-talk*, like a walkie-talkie — not a toggle.
Keep holding while you speak; the moment you let go, transcription starts and the text is
typed.

1. Click into wherever you want the text — a document, a chat box, a terminal.
2. Hold <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Space</kbd>. The icon turns **red**.
3. Speak normally.
4. Release. The icon turns **amber**, then your words appear.

For a short sentence the wait is a fraction of a second.

### Things worth knowing

**The text goes to the window you were speaking into.** Transcription finishes a moment after
you let go, and if something takes focus in between — opening the tray menu to check the status
is enough — the app puts focus back before typing. The one case it cannot fix is a window
running as administrator, which Windows will not let it restore; there the text still follows
focus. `Injection.RestoreTargetWindow` turns the behaviour off.

**Speak immediately after pressing and the first word can be clipped** — but only on your
very first dictation after launching. Opening the microphone takes about 0.7 s the first
time and under 0.1 s after that. If the first attempt of a session loses a word, hold the
key for a beat before speaking, or dictate one throwaway phrase first.

**You get a trailing space**, so consecutive dictations do not run together. Turn it off
with `Injection.AppendTrailingSpace`.

**Long passages are pasted, not typed.** At 240 characters or more the app pastes via the
clipboard, because typing that much is visibly slow. Your previous clipboard contents are
put back afterwards.

**An accidental tap produces nothing.** Anything under 250 ms, or too quiet to be speech,
is discarded before it ever reaches the model.

**A stuck key cannot record forever** — recording stops on its own after 2 minutes.

**Don't hold it for a whole minute.** The default model handles up to about 45 seconds well.
Past roughly 50 seconds it quietly starts dropping words, and past 75 it repeats itself — with
no warning either way. Dictate a sentence or a paragraph at a time and release; you can start
the next one while the last is still being transcribed, and they arrive in order. If you want
to dictate in long stretches, turn on **Type while I speak** (below).

**The space is swallowed**, so the window you are typing into never receives it.
<kbd>Ctrl</kbd> and <kbd>Shift</kbd> are always passed through, so every other shortcut on
your system keeps working.

### Type while I speak

Settings → **Audio** → *Type while I speak*. Off by default.

With it on, the app transcribes and types every 20 seconds (configurable) while you are still
holding the key, instead of waiting for the release. Two reasons to use it:

- **Text appears as you talk**, rather than in one burst at the end.
- **It keeps long dictation accurate.** Each segment stays short enough for the model, which is
  what stops the word-dropping and repetition described above.

A 70-second dictation that would otherwise come back as gibberish arrives as five chunks, each
about two seconds behind your voice.

The app cuts at the quietest moment near each boundary, so segments land in natural pauses and
come out roughly — not exactly — the length you set. It is not perfect: a boundary that finds
no real pause can garble a word. Leave it off if you dictate in short bursts, where it buys
nothing.

### Punctuation

**Punctuation is unreliable with the default model.** Two separate things to know:

- **Spoken punctuation is not obeyed.** Saying *"Hello comma this is a test period"* types
  `Hello comma this is a test period` — the words themselves, not the marks.
- **What punctuation you do get is inconsistent.** Moonshine Base adds full stops and capitals
  on its own sometimes and not others. The identical recording, transcribed twice in testing,
  gave `...the lazy dog push to talk...` once and `...the lazy dog. Push to talk...` the next
  time. Expect to tidy punctuation by hand.

If you need it to be dependable, switch to a punctuating model — the Whisper-family engines and
SenseVoice are built to produce punctuation and casing. See [models.md](models.md).

There are no spoken editing commands in any case (no "delete that", no "new paragraph"); the
app types what was recognised and nothing else.

---

## 4. The tray menu

**Left-click or right-click** the icon:

| Item | What it does |
|---|---|
| *Push-to-Talk Dictation 1.2.0* | The version you are running — quote it if you report a problem |
| **Toggle Active (On/Off)** | Turns dictation on or off — see below |
| *Ready - hold Ctrl+Shift+Space* | Current status, the same information as the icon colour |
| *Engine: …* | Which speech engine is in use |
| *Model: …* | Whether the model is currently in memory |
| *Hold: …* | Your current hotkey |
| **Settings…** | Opens the settings window |
| **User guide** | Opens this guide (the HTML copy) in your browser |
| **Open settings file…** | Opens `appsettings.json` in your text editor |
| **Open log folder…** | Opens the folder containing `app.log` |
| **Exit** | Quits completely |

The greyed-out lines are information, not buttons.

### Turning it off, and what that saves

**Off is genuinely off.** The keyboard hook is removed from Windows entirely — the app is
not watching your typing at all, and the hotkey does nothing.

It also **releases the speech model from memory**, dropping the app from roughly 310 MB to
75 MB. Switching back on reloads it in about a second.

| State | Memory |
|---|---|
| On, ready to dictate | ~310 MB |
| Off | ~75 MB |

Prefer instant switching over getting the memory back? Set
`SpeechToText.UnloadOnInactive` to `false` and the model stays loaded.

Anything you already said still gets typed — switching off does not discard an utterance
that is mid-transcription.

---

## 5. Settings

### The settings window

**Tray icon → Settings…**

Five tabs — **Hotkey**, **Audio**, **Speech**, **Typing**, **General**. Every field has a line
of explanation under it, so you should not need this guide open while you use it.

Press **Save** and the change takes effect immediately. **One exception:** changing the
*speech engine* needs a restart, and the window says so when you do it.

The hotkey box checks itself as you type — it shows *"Reads as Ctrl+Alt+D"* when the combo is
valid and tells you what is wrong when it is not, so you cannot save something that leaves you
without a working shortcut. The microphone is a dropdown of your actual input devices rather
than a device ID you have to look up.

### Where your changes go

Into **`appsettings.user.json`**, beside the program, and only the values you actually changed:

```jsonc
{
  "Hotkey": { "Combo": "Ctrl+Alt+D" },
  "Injection": { "AppendTrailingSpace": false }
}
```

The shipped `appsettings.json` is never rewritten, so its comments explaining every option
stay intact and you can always see what the defaults were. **Deleting
`appsettings.user.json` puts everything back to the defaults** — and if you revert every
change in the window, it deletes the file for you.

### Editing the file by hand

Still fine, and necessary for the handful of keys the window does not cover — model file
names and the HTTP endpoint. **Advanced…** in the settings window opens `appsettings.json`,
as does tray → **Open settings file…**. It accepts `//` comments and trailing commas.

A hand edit needs a restart: only changes made through the window apply live.

If the app stops appearing in your tray after a hand edit, you almost certainly have a JSON
syntax error — a missing comma or brace. Run the `.exe` from a PowerShell window and it will
tell you.

### The settings you are most likely to want

| Setting | Default | Change it when |
|---|---|---|
| `Hotkey.Combo` | `"Ctrl+Shift+Space"` | The default clashes with an app you use |
| `SpeechToText.UnloadOnInactive` | `true` | You want instant on/off instead of the memory back |
| `Injection.AppendTrailingSpace` | `true` | You do not want an automatic trailing space |
| `Injection.RestoreTargetWindow` | `true` | You would rather the text always go to whatever has focus when it is ready |
| `Audio.ChunkLongDictation` | `false` | You dictate in long stretches and want text to appear as you speak |
| `Audio.ChunkSeconds` | `20` | Tuning how often segments are transcribed |
| `Injection.ClipboardThreshold` | `240` | Set `0` if you use a clipboard manager, or if a terminal ignores <kbd>Ctrl</kbd>+<kbd>V</kbd> |
| `Audio.SilenceRmsThreshold` | `0.0025` | Raise to `0.005` if stray taps produce phantom text; lower to `0.001` if quiet speech is dropped |
| `Audio.DeviceId` | `null` | You have several microphones and it picked the wrong one |
| `StartActive` | `true` | You want it to start switched **off** every time |
| `Hotkey.MaxRecordingSeconds` | `120` | You dictate for longer than two minutes at a stretch |
| `Logging.MinimumLevel` | `"Information"` | Set `"Debug"` while diagnosing anything |

All of these are in the settings window. Every key, including the ones the window does not
expose, is documented in [configuration.md](configuration.md).

### Changing the hotkey

Settings window → **Hotkey** tab → *Hold-to-talk combo*. Write modifiers and one ordinary key
joined by `+`:

- Modifiers: `Ctrl`, `Shift`, `Alt`, `Win`
- The ordinary key: any key name — `Space`, `F9`, `D`, `NumPad0`, `Oem3` (backtick)

| Example | Notes |
|---|---|
| `"Ctrl+Shift+Space"` | The default |
| `"Ctrl+Alt+D"` | Usually free of conflicts |
| `"F9"` | A single key, no modifiers — the fastest to reach |
| `"Ctrl+Shift"` | Modifiers only: hold both for 250 ms to start. **Not recommended** — it collides with the Windows keyboard-layout switcher |

Modifiers must match **exactly** — a `Ctrl+Shift` hotkey will not fire while you are also
holding <kbd>Alt</kbd>. That is deliberate, so your other shortcuts keep working.

Worth avoiding: <kbd>Ctrl</kbd>+<kbd>Shift</kbd>+<kbd>Space</kbd> is a non-breaking space
in Word and a parameter hint in Visual Studio. The app swallows the keystroke so those apps
never see it, but if you dictate heavily into them, pick something quieter.

### Changing a setting without editing the file

Any setting can be overridden with an environment variable — `PTTD_` in front, `__` between
levels. Useful for a one-off:

```powershell
$env:PTTD_Logging__MinimumLevel = "Debug"
$env:PTTD_StartActive = "false"
.\PushToTalkDictation.exe
```

These win over the file.

### Using a different speech model

The default is **Moonshine Base (English)**, chosen because it is the fastest option for
short dictation. If you need better accuracy on technical vocabulary or a strong accent:

```powershell
.\scripts\download-models.ps1 -Model parakeet
```

…then point the settings at it. [models.md](models.md) covers the full list and the
trade-offs.

---

## 6. Logs

**Tray icon → Open log folder…**, or:

```
%LOCALAPPDATA%\PushToTalkDictation\logs\app.log
```

This is the first place to look when something does not work — it tells you which stage
failed: hearing you, transcribing, or typing. Set `Logging.MinimumLevel` to `"Debug"` first
and restart; you then get the microphone it chose, timings for every phrase, and a line for
every clip discarded as silence.

Once the file passes 4 MB, the next launch archives it and starts a fresh one.

---

## 7. What it cannot do

Not bugs — consequences of how Windows works:

**It cannot type into programs running as administrator.** Task Manager, an elevated
terminal, anything started with *Run as administrator* — Windows silently blocks input from
a normal program into an elevated one. Test in Notepad to confirm this is what you are
hitting. Running the app itself as administrator fixes it, at the cost of a UAC prompt every
launch.

**It does nothing on the lock screen, the UAC prompt, or the sign-in screen.** Those are
protected by design and cannot be reached.

**No live captions.** Text appears when you release the key, not as you speak.

**No spoken commands.** It transcribes; it does not take instructions.

**Your antivirus may flag it.** A program that watches the keyboard and synthesises
keystrokes has the same shape as a keylogger — because at the Windows level, that is exactly
the machinery it uses. Everything stays on your machine and no audio ever leaves it, but you
may need to add an exclusion. On a work machine, expect to involve whoever manages it.

---

## 8. If something is wrong

| Symptom | Most likely cause |
|---|---|
| No tray icon at all | It failed to start. Check the log, or run the `.exe` from PowerShell to see the error — usually a typo in `appsettings.json`. |
| Icon is grey | Switched off. Click it → **Toggle Active**. Starts grey every time? Set `StartActive: true`. |
| Icon is blue but the hotkey does nothing | Another app owns that key combination — try `"Ctrl+Alt+D"`. Or the window you are typing into is elevated (see above). |
| Recording works, no text appears | The target window is elevated — Windows blocks both the typing and the focus restore for an administrator window. |
| Text appears but triggers menus and shortcuts | A modifier key was still held when typing began. Release the keys deliberately, or raise `Injection.WaitForModifierReleaseMs`. |
| Phantom text from an accidental tap | Raise `Audio.SilenceRmsThreshold` to `0.005`. |
| Quiet speech is ignored | Lower `Audio.SilenceRmsThreshold` to `0.001`. |
| First word of the first dictation is missing | The microphone takes ~0.7 s to open the first time. Pause briefly before speaking. |
| Characters dropped in one specific app | Set `Injection.KeystrokeDelayMs` to `1` or `2`. |
| Slow on every phrase | The model is too large for the machine — see [models.md](models.md). |
| Wrong microphone | Set `Audio.DeviceId`, or fix your Windows default input device. |
| First phrase after switching On is slow, every time | Expected — the model reloads. Set `UnloadOnInactive: false` to keep it in memory. |

Symptom-by-symptom detail, with the log lines to look for, is in
[troubleshooting.md](troubleshooting.md).

---

## 9. Uninstall

There is no installer, so there is nothing to uninstall:

1. **Exit** from the tray menu.
2. Delete the application folder.
3. Delete `%LOCALAPPDATA%\PushToTalkDictation` (logs only).
4. Remove the shortcut from `shell:startup` if you made one.

Nothing is written to the registry.
