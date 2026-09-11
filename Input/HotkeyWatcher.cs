using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using PushToTalkDictation.Configuration;
using PushToTalkDictation.Interop;

namespace PushToTalkDictation.Input;

/// <summary>
/// Turns raw hook events into two clean signals: the hold started, and the hold ended.
///
/// Everything here runs on the UI/message-pump thread inside the hook callback, so it
/// does nothing but bookkeeping. Consumers must not block these events.
/// </summary>
public sealed class HotkeyWatcher : IDisposable
{
    private readonly GlobalKeyboardHook _hook;
    private readonly HotkeySettings _settings;
    private readonly ILogger<HotkeyWatcher> _logger;
    private readonly HotkeyCombo _combo;

    private readonly System.Windows.Forms.Timer _armTimer;   // modifiers-only hold threshold
    private readonly System.Windows.Forms.Timer _maxTimer;   // runaway guard

    private bool _enabled;
    private bool _holding;        // a dictation hold is in progress
    private bool _triggerDown;    // trigger key physically down (filters auto-repeat)
    private bool _arming;         // modifiers-only: waiting out the hold threshold

    /// <summary>Raised when the user starts holding the combo.</summary>
    public event EventHandler? HoldStarted;

    /// <summary>Raised when the hold ends. <c>true</c> means "discard", not "transcribe".</summary>
    public event EventHandler<bool>? HoldEnded;

    public HotkeyCombo Combo => _combo;

    public HotkeyWatcher(GlobalKeyboardHook hook, AppSettings settings, ILogger<HotkeyWatcher> logger)
    {
        _hook = hook;
        _settings = settings.Hotkey;
        _logger = logger;
        _combo = HotkeyCombo.Parse(_settings.Combo);

        _armTimer = new System.Windows.Forms.Timer { Interval = Math.Max(1, _settings.ModifiersOnlyHoldMs) };
        _armTimer.Tick += OnArmElapsed;

        _maxTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(1000, _settings.MaxRecordingSeconds * 1000)
        };
        _maxTimer.Tick += (_, _) =>
        {
            _logger.LogWarning("Maximum hold duration reached; stopping.");
            EndHold(cancelled: false);
        };

        _hook.KeyEvent += OnKeyEvent;
    }

    public void Enable()
    {
        _enabled = true;
        _logger.LogInformation("Hotkey watcher enabled for {Combo}.", _combo);
    }

    public void Disable()
    {
        _enabled = false;
        CancelArm();
        if (_holding) EndHold(cancelled: true);
        _triggerDown = false;
    }

    // ------------------------------------------------------------------ core

    private void OnKeyEvent(object? sender, KeyEventArgsLite e)
    {
        if (!_enabled) return;

        var key = Normalize(e.Key);
        var modifiers = CurrentModifiers(key, e.IsKeyDown);

        if (_combo.IsModifiersOnly)
            HandleModifiersOnly(key, e, modifiers);
        else
            HandleTriggerKey(key, e, modifiers);
    }

    private void HandleTriggerKey(Keys key, KeyEventArgsLite e, HotModifiers modifiers)
    {
        var isTrigger = key == _combo.TriggerKey;

        if (isTrigger && e.IsKeyDown)
        {
            if (_triggerDown)
            {
                // Auto-repeat while held: swallow it, but don't restart anything.
                if (_holding && _settings.SuppressTriggerKey) e.Handled = true;
                return;
            }

            _triggerDown = true;

            if (_combo.ModifiersSatisfied(modifiers))
            {
                if (_settings.SuppressTriggerKey) e.Handled = true;
                BeginHold();
            }

            return;
        }

        if (isTrigger && !e.IsKeyDown)
        {
            _triggerDown = false;
            if (_holding)
            {
                if (_settings.SuppressTriggerKey) e.Handled = true;
                EndHold(cancelled: false);
            }

            return;
        }

        // A required modifier was released mid-hold -> treat it as the end of the hold.
        if (_holding && IsModifierKey(key) && !e.IsKeyDown && !_combo.ModifiersSatisfied(modifiers))
            EndHold(cancelled: false);
    }

    private void HandleModifiersOnly(Keys key, KeyEventArgsLite e, HotModifiers modifiers)
    {
        if (IsModifierKey(key))
        {
            if (_combo.ModifiersSatisfied(modifiers))
            {
                if (!_holding && !_arming) StartArm();
            }
            else
            {
                CancelArm();
                if (_holding) EndHold(cancelled: false);
            }

            return;
        }

        // Any ordinary key means the user is doing something else with these modifiers
        // (Ctrl+Shift+Arrow, Ctrl+Shift+T, ...). Abort rather than dictate.
        if (e.IsKeyDown)
        {
            if (_arming) CancelArm();
            if (_holding)
            {
                _logger.LogDebug("Hold cancelled: {Key} pressed while modifiers held.", key);
                EndHold(cancelled: true);
            }
        }
    }

    private void StartArm()
    {
        _arming = true;
        _armTimer.Stop();
        _armTimer.Start();
    }

    private void CancelArm()
    {
        _arming = false;
        _armTimer.Stop();
    }

    private void OnArmElapsed(object? sender, EventArgs e)
    {
        _armTimer.Stop();
        if (!_arming) return;
        _arming = false;

        if (_combo.ModifiersSatisfied(CurrentModifiers(Keys.None, false)))
            BeginHold();
    }

    private void BeginHold()
    {
        if (_holding) return;
        _holding = true;
        _maxTimer.Stop();
        _maxTimer.Start();
        HoldStarted?.Invoke(this, EventArgs.Empty);
    }

    private void EndHold(bool cancelled)
    {
        if (!_holding) return;
        _holding = false;
        _maxTimer.Stop();
        HoldEnded?.Invoke(this, cancelled);
    }

    // ------------------------------------------------------------- utilities

    /// <summary>Collapse L/R variants so Keys.LControlKey and Keys.RControlKey compare equal.</summary>
    private static Keys Normalize(Keys key) => key switch
    {
        Keys.LControlKey or Keys.RControlKey => Keys.ControlKey,
        Keys.LShiftKey or Keys.RShiftKey => Keys.ShiftKey,
        Keys.LMenu or Keys.RMenu => Keys.Menu,
        Keys.LWin or Keys.RWin => Keys.LWin,
        _ => key
    };

    private static bool IsModifierKey(Keys key) =>
        key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin;

    /// <summary>
    /// Physical modifier state. GetAsyncKeyState still reports a modifier as down while
    /// we are inside the hook callback for its own key-up, so that one case is corrected
    /// from the event itself.
    /// </summary>
    private static HotModifiers CurrentModifiers(Keys eventKey, bool eventIsDown)
    {
        var m = HotModifiers.None;
        if (Win32.IsKeyDown(Win32.VK_CONTROL)) m |= HotModifiers.Control;
        if (Win32.IsKeyDown(Win32.VK_SHIFT)) m |= HotModifiers.Shift;
        if (Win32.IsKeyDown(Win32.VK_MENU)) m |= HotModifiers.Alt;
        if (Win32.IsKeyDown(Win32.VK_LWIN) || Win32.IsKeyDown(Win32.VK_RWIN)) m |= HotModifiers.Win;

        if (!eventIsDown)
        {
            m &= eventKey switch
            {
                Keys.ControlKey => ~HotModifiers.Control,
                Keys.ShiftKey => ~HotModifiers.Shift,
                Keys.Menu => ~HotModifiers.Alt,
                Keys.LWin or Keys.RWin => ~HotModifiers.Win,
                _ => ~HotModifiers.None
            };
        }

        return m;
    }

    public void Dispose()
    {
        _hook.KeyEvent -= OnKeyEvent;
        _armTimer.Dispose();
        _maxTimer.Dispose();
    }
}
