# Getting started

## Prerequisites

| Requirement | Notes |
|---|---|
| Windows 10 1809+ or Windows 11, **x64** | The project pins `win-x64`; the native ONNX Runtime and whisper.cpp binaries are architecture-specific. |
| .NET 8 SDK | `winget install Microsoft.DotNet.SDK.8` |
| A microphone set as the default recording device | Settings → System → Sound → Input |
| ~250 MB disk for the model | Not in source control; `models/` is gitignored. |

Visual Studio 2022 needs the **.NET desktop development** workload.
VS Code needs the **C# Dev Kit** extension (`ms-dotnettools.csdevkit`).

## First build

```powershell
cd C:\source\STTService

dotnet restore
.\scripts\download-models.ps1        # Moonshine Base (en), ~200 MB, into .\models
dotnet build -c Release

.\bin\Release\net8.0-windows\win-x64\PushToTalkDictation.exe
```

A microphone icon appears in the system tray. Hold **Ctrl+Shift+Space**, say something,
release. The text is typed into whatever window had focus.

Tray icon colours: grey = inactive, blue = ready, red = recording, amber = transcribing.

### Verifying it end to end

1. Open Notepad and click into it.
2. Hold the hotkey for ~2 seconds while speaking a full sentence.
3. Release. Text should appear within a few hundred milliseconds.

If nothing appears, open the log first — tray menu → **Open log folder** — and set
`Logging.MinimumLevel` to `Debug` in `appsettings.json`. The log tells you which stage failed:
capture, decode, or injection. Then see [troubleshooting.md](troubleshooting.md).

## Working in Visual Studio

Open `PushToTalkDictation.sln`. F5 runs it under the debugger.

> **Do not put a breakpoint inside `GlobalKeyboardHook.HookCallback`.**
>
> A low-level keyboard hook callback runs inside the input pipeline for the *whole desktop*.
> Stopping in it freezes keyboard input system-wide until Windows hits `LowLevelHooksTimeout`
> (default 300 ms) and silently detaches the hook — and you will be unable to type into the
> debugger while it's stopped. Log from there instead; the log writer is queued and
> non-blocking specifically so this is safe.
>
> The same applies to any breakpoint hit while the hook is installed on a single-monitor
> machine. If you need to step through hook logic, either run with `"StartActive": false`
> and toggle it on only after the breakpoint is armed elsewhere, or debug over Remote Desktop
> from a second machine.

Safe places to break: `DictationController.OnHoldEnded`, `ConsumeAsync`, any engine's
`RecognizeAsync`, `TextInjector.InjectAsync` (break *before* the `SendInput` call, not between
batches).

## Working in VS Code

`.vscode/launch.json` and `.vscode/tasks.json` are included. F5 builds and launches.

The C# Dev Kit will ask which project to run — pick `PushToTalkDictation`. Because the app has
no window, "it launched and nothing happened" is the expected result; look at the tray.

Useful terminal loop while iterating on non-hook code:

```powershell
dotnet build && .\bin\Debug\net8.0-windows\win-x64\PushToTalkDictation.exe
```

The single-instance mutex means a second launch shows a message box instead of starting.
Exit the running instance from the tray first.

## Faster iteration without speaking into the mic every time

`appsettings.json` → `"SpeechToText": { "Engine": "Http" }` and point it at a local
faster-whisper server. You can then restart the C# app freely without paying the model
load each time, and swap models server-side.

To test injection alone, call `ITextInjector.InjectAsync("hello world ")` from a temporary
menu item in `TrayApplicationContext` — no mic, no model.

## Shipping it

`dotnet publish` is the packaging step. It produces a folder that can be zipped, copied to
another machine and run in place:

```powershell
dotnet publish -c Release
# -> bin\Release\net8.0-windows\win-x64\publish\
```

The publish target copies `models\` next to the executable, so the folder carries its own
model — that's the difference from `dotnet build`, which deliberately leaves the model where
it is so incremental builds don't copy 200 MB every time. Download the model *before*
publishing; publishing with an empty `models\` folder emits a build warning rather than
failing quietly.

The result still needs the .NET 8 desktop runtime on the target machine. For a machine with
no .NET installed, publish self-contained:

```powershell
dotnet publish -c Release --self-contained -p:PublishSingleFile=false
```

To publish the binaries alone and deliver the model separately (an installer, a shared
network folder, a per-machine cache), use `-p:PublishModels=false` and set
`SpeechToText.ModelRoot` — or drop the model into `%LOCALAPPDATA%\PushToTalkDictation\models\`,
which is probed automatically. See
[configuration.md](configuration.md#where-models-are-looked-up).

## Running at login

Put a shortcut to the published `.exe` in `shell:startup` (Win+R → `shell:startup`).

Use a Release build. The working directory of a startup shortcut is not guaranteed, but every
path in the app resolves against `AppContext.BaseDirectory` rather than the CWD, so a shortcut
from anywhere works.

## Project layout

```
PushToTalkDictation.sln
PushToTalkDictation.csproj
app.manifest              DPI awareness + execution level
appsettings.json          all runtime configuration
Program.cs                composition root: config, DI, exception handlers
DictationController.cs    the pipeline and its threading contract
Configuration/            strongly-typed settings
Diagnostics/              queued file logger
Input/                    WH_KEYBOARD_LL hook, combo parser, hold state machine
Audio/                    WASAPI capture, resampling, the AudioClip type
Stt/                      ISpeechToTextEngine + three implementations
Injection/                SendInput text injection
Tray/                     NotifyIcon, context menu, generated icons
scripts/                  model download
models/                   gitignored; models land here
docs/                     this folder
```
