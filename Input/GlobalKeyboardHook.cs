using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using PushToTalkDictation.Interop;

namespace PushToTalkDictation.Input;

public sealed class KeyEventArgsLite : EventArgs
{
    public required Keys Key { get; init; }
    public required bool IsKeyDown { get; init; }
    public required bool IsInjected { get; init; }

    /// <summary>Set to true inside the handler to swallow the keystroke.</summary>
    public bool Handled { get; set; }
}

/// <summary>
/// WH_KEYBOARD_LL wrapper.
///
/// Two hard rules are baked in:
///  1. Install / uninstall happen on a thread that pumps messages (the UI thread).
///     Windows delivers low-level hook callbacks on that thread's message loop.
///  2. The callback must return fast. It only reads key state and raises an event;
///     anything expensive is dispatched to the thread pool by the consumer.
///     If a callback exceeds LowLevelHooksTimeout, Windows silently detaches the
///     hook — the watchdog below re-installs it.
/// </summary>
public sealed class GlobalKeyboardHook : IDisposable
{
    private readonly ILogger<GlobalKeyboardHook> _logger;

    // Held in a field so the GC never collects the delegate the OS holds a pointer to.
    private readonly Win32.LowLevelKeyboardProc _proc;
    private readonly System.Windows.Forms.Timer _watchdog;
    private readonly int _ownerThreadId;

    private IntPtr _hookHandle = IntPtr.Zero;
    private bool _disposed;

    public event EventHandler<KeyEventArgsLite>? KeyEvent;

    public bool IsInstalled => _hookHandle != IntPtr.Zero;

    public GlobalKeyboardHook(ILogger<GlobalKeyboardHook> logger)
    {
        _logger = logger;
        _proc = HookCallback;
        _ownerThreadId = Environment.CurrentManagedThreadId;

        _watchdog = new System.Windows.Forms.Timer { Interval = 5000 };
        _watchdog.Tick += (_, _) =>
        {
            if (_wantInstalled && !IsInstalled)
            {
                _logger.LogWarning("Keyboard hook was detached by the OS; re-installing.");
                Install();
            }
        };
    }

    private bool _wantInstalled;

    public void Install()
    {
        EnsureOwnerThread();
        _wantInstalled = true;
        if (IsInstalled) return;

        var module = Win32.GetModuleHandleW(null);
        _hookHandle = Win32.SetWindowsHookExW(Win32.WH_KEYBOARD_LL, _proc, module, 0);

        if (_hookHandle == IntPtr.Zero)
        {
            var err = Marshal.GetLastWin32Error();
            _logger.LogError("SetWindowsHookEx failed (Win32 error {Error}).", err);
            throw new InvalidOperationException($"Could not install the global keyboard hook (error {err}).");
        }

        _watchdog.Start();
        _logger.LogInformation("Global keyboard hook installed.");
    }

    public void Uninstall()
    {
        EnsureOwnerThread();
        _wantInstalled = false;
        _watchdog.Stop();

        if (!IsInstalled) return;

        Win32.UnhookWindowsHookEx(_hookHandle);
        _hookHandle = IntPtr.Zero;
        _logger.LogInformation("Global keyboard hook removed.");
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode < 0)
            return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

        try
        {
            var data = Marshal.PtrToStructure<Win32.KBDLLHOOKSTRUCT>(lParam);

            // Ignore the keystrokes this app synthesises, otherwise injecting text
            // would feed straight back into the hotkey state machine.
            if (data.dwExtraInfo == Win32.InjectionSignature)
                return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            var msg = (int)wParam;
            var isDown = msg is Win32.WM_KEYDOWN or Win32.WM_SYSKEYDOWN;
            var isUp = msg is Win32.WM_KEYUP or Win32.WM_SYSKEYUP;
            if (!isDown && !isUp)
                return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);

            var args = new KeyEventArgsLite
            {
                Key = (Keys)data.vkCode,
                IsKeyDown = isDown,
                IsInjected = (data.flags & Win32.LLKHF_INJECTED) != 0
            };

            KeyEvent?.Invoke(this, args);

            if (args.Handled)
                return 1; // swallow
        }
        catch (Exception ex)
        {
            // Never let an exception escape into unmanaged code.
            _logger.LogError(ex, "Unhandled exception in keyboard hook callback.");
        }

        return Win32.CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    private void EnsureOwnerThread()
    {
        if (Environment.CurrentManagedThreadId != _ownerThreadId)
            throw new InvalidOperationException(
                "The keyboard hook must be installed and removed on the thread that owns the message loop.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        if (IsInstalled)
        {
            Win32.UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }

        _watchdog.Dispose();
    }
}
