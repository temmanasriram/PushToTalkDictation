# Win32 notes

The platform behaviour this app depends on, and the reasoning behind each defensive measure in
`Interop/Win32.cs`, `Input/GlobalKeyboardHook.cs` and `Injection/TextInjector.cs`. Most of it
is expensive to rediscover.

## WH_KEYBOARD_LL

```csharp
SetWindowsHookExW(WH_KEYBOARD_LL, proc, GetModuleHandleW(null), 0);
```

**The callback runs on the installing thread's message loop.** A low-level keyboard hook is
not called on the thread that generated the input; Windows marshals it to the thread that
installed the hook, which must therefore be pumping messages. Install it from a thread with no
message loop and the hook exists but never fires. `GlobalKeyboardHook.EnsureOwnerThread()`
asserts this; `ApplicationContext` provides the pump without needing a window.

**The callback runs inside the input path for the entire desktop.** Every keystroke anyone
types anywhere goes through it, synchronously, before the target app sees it. This is why the
callback only reads key state, sets flags and returns.

**Windows silently detaches slow hooks.** If a callback exceeds `LowLevelHooksTimeout`
(`HKEY_CURRENT_USER\Control Panel\Desktop`, default 300 ms), the OS removes the hook without
notification, error, or any event you can subscribe to. The app just stops responding to the
hotkey. Hence:

- the callback never blocks, never does synchronous I/O, never waits on a lock;
- `FileLoggerProvider` queues writes to a background thread rather than touching the disk inline;
- a 5-second watchdog timer re-installs the hook if `_hookHandle` is set but the hook has gone.

**Never let an exception escape into unmanaged code.** The callback is invoked by the OS
through a function pointer; an exception crossing that boundary terminates the process. The
whole body is wrapped in try/catch.

**Keep the delegate alive.** `_proc` is a field, not a local. The OS holds a raw function
pointer; if the managed delegate is collected the process faults on the next keystroke. This
is the classic way to get a crash that reproduces only after a GC, i.e. minutes into a session.

**Returning 1 swallows the key.** `CallNextHookEx` passes it on; returning `1` consumes it
so no other hook and no application sees it. The app swallows only the trigger key, only
while the combo is active, and only when `SuppressTriggerKey` is set. Swallowing modifiers
would break every other shortcut on the system.

**`GetAsyncKeyState` cannot be the only source of modifier state.** Two distinct problems:

1. While the callback for a key-*up* is executing, that key still reads as down.
2. Worse, and the cause of a real bug: `SendInput` changes what `GetAsyncKeyState` reports.
   `TextInjector` releases modifiers the user is still holding before it types, so afterwards
   Ctrl reads as *up* while the user's finger is still on the key. Nothing resyncs it - the
   key never physically moves again, so there is no second key-down. The hotkey then stopped
   matching, silently, until the user let go of the modifiers and pressed them again: hold
   Ctrl+Shift, dictate, keep holding, press Space again, and nothing happened at all.

`HotkeyWatcher` therefore tracks physical modifier state from the hook's own events, which
describe only real key movement because injected input is filtered out by signature before the
watcher sees it. The effective state is the union of that and `GetAsyncKeyState`: tracked-only
covers the injector's synthetic key-ups, OS-only covers a key-down that happened while the hook
was not installed. Updating the tracked state from the event *before* evaluating it also makes
problem 1 disappear, so no explicit correction is needed any more.

**L/R variants.** The hook reports `VK_LCONTROL`/`VK_RCONTROL`, not `VK_CONTROL`.
`HotkeyWatcher.Normalize` collapses them so the two Ctrl keys behave identically.

## SendInput

**`KEYEVENTF_UNICODE` beats scan codes.** With `wVk = 0`, `wScan = <UTF-16 code unit>` and the
`KEYEVENTF_UNICODE` flag, Windows delivers that exact character. No layout translation, no
dead keys, no difference between a US and a German keyboard, and spacing and punctuation
arrive verbatim. Sending scan codes instead would produce different characters on different
layouts.

**Surrogate pairs go as two consecutive inputs.** A character outside the BMP (an emoji) is two
UTF-16 code units; send them as two `INPUT` entries in the same `SendInput` call. Iterating a
C# `string` by `char` does exactly this for free.

**Newlines and tabs are not Unicode.** Sending `\n` as a Unicode input does nothing useful in
most apps. `VK_RETURN` and `VK_TAB` go as real virtual-key events.

**Batch, but not unboundedly.** `SendInput` takes an array and delivers it atomically —
no other thread's input can interleave within one call. The injector batches 200 inputs
(100 characters) per call; very large arrays are more likely to be partially delivered.
A short return value means some events were dropped; the log says so.

**`cbSize` must be `sizeof(INPUT)` for the current architecture** — 40 bytes on x64, 28 on x86.
`Marshal.SizeOf<INPUT>()` handles it, which is why the `MOUSEINPUT` member is declared even
though this app never sends mouse input: it's the largest union member and therefore sets the
struct size.

**Tag your own input.** Every synthesised event carries `dwExtraInfo = 0x50545444` ("PTTD").
The app's own hook checks for it and passes such events straight through. Without this, typing
a transcription while the hook is live feeds those keystrokes back into the hotkey state
machine — and if the text contains a space while a modifier is somehow still down, you get a
feedback loop. Windows also sets `LLKHF_INJECTED` on synthetic input, but that flag is set for
*any* injected input including other automation tools, so it's too broad to filter on alone.

**Clear modifiers before typing.** `KEYEVENTF_UNICODE` bypasses layout translation, but the
target app can still call `GetKeyState(VK_CONTROL)` and decide it's looking at a shortcut. If
the user is still holding Ctrl when injection begins, dictated text turns into a burst of
keyboard shortcuts — a genuinely destructive failure in an editor. The injector waits up to
`WaitForModifierReleaseMs` for physical release, then releases whatever is still down.

**Release only the keys actually down.** The first version fired key-ups for both Ctrl, both
Shift *and* both Alt unconditionally. That synthesised an Alt release on every dictation that
ended with a modifier held, for a key that was never involved — and a stray Alt-up is how you
activate a window's menu bar. Read each key with `GetAsyncKeyState` and release only that set.
Note this still desynchronises the OS from the user's fingers for the keys it does release; see
the `GetAsyncKeyState` note above for why the hotkey survives that.

**Text follows focus, so capture the target window up front.** `SendInput` goes to whatever has
focus *when it runs*, which for dictation is a second or more after the user stopped speaking.
Opening the tray menu to check the status is enough to move focus, and then the transcript is
typed into the menu instead of the document. `DictationController` reads
`GetForegroundWindow()` at `HoldStarted` and passes it down with the clip; `TextInjector`
restores it with the `AttachThreadInput` + `SetForegroundWindow` dance if focus has moved.
Windows refuses `SetForegroundWindow` from a process that doesn't already own the foreground,
which is what the attach works around.

## UIPI and integrity levels

A process cannot send input to, or hook keys inside, a window belonging to a **higher
integrity level** process. User Interface Privilege Isolation blocks it silently — `SendInput`
returns success, and nothing happens.

Practical consequences:

- Running non-elevated (the default, `asInvoker`), dictation works in normal apps but does
  nothing in an elevated Terminal, Task Manager, or an app launched "as administrator".
- The fix is `requestedExecutionLevel level="requireAdministrator"` in `app.manifest`, which
  costs a UAC prompt at every launch.
- The middle path is `uiAccess="true"`, which allows cross-integrity input without full
  elevation — but requires the binary to be Authenticode-signed and installed under
  `Program Files`. Not worth it for a dev build.
- Nothing works on the secure desktop (UAC prompt itself, Ctrl+Alt+Del, the lock screen).
  That is by design and cannot be worked around.

## WASAPI capture

**Shared mode gives you the device's mix format, not what you asked for.** Typically 32-bit
IEEE float, 48 kHz, stereo — whatever the user set in Sound Control Panel. Read
`capture.WaveFormat` and convert; don't assume. `AudioRecorder` handles float32, PCM16 and
PCM32, and logs the format it actually got at `Debug` level.

**Buffers flush asynchronously after `StopRecording()`.** `RecordingStopped` fires later.
Grabbing the buffer immediately clips the tail of the utterance — exactly the last word, which
is the one users notice. The recorder waits up to 500 ms for that event.

**Event-sync mode with a 50 ms buffer** bounds how much audio is still in flight when the user
releases the key, which is the recorder's contribution to perceived latency.

## Tray icons

`Bitmap.GetHicon()` returns an `HICON` that **`Icon.Dispose()` does not free**. Both are
needed: dispose the `Icon`, then `DestroyIcon` the handle. `TrayIcons` keeps the handles in a
list for exactly this. Creating icons per state change without freeing them is a slow,
invisible GDI handle leak that eventually breaks the whole process's ability to draw.

`NotifyIcon.Text` is capped at 63 characters and throws above that.

`NotifyIcon` only shows its `ContextMenuStrip` on right-click; left-click is wired manually via
`MouseUp` + `ContextMenuStrip.Show(Control.MousePosition)`.

## Single instance

A `Global\` named mutex, held for process lifetime. Two instances would install two hooks and
type everything twice.

## Antivirus and EDR

A background process that installs a global keyboard hook and synthesises keystrokes matches
the behavioural signature of a keylogger, because at the Win32 level it *is* one. Expect
heuristic flags from some endpoint products. Nothing in the code can prevent that; on a
managed machine you'll need the binary signed and allow-listed by whoever administers it.
Worth knowing before deploying beyond your own box.
