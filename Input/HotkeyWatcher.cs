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

    // Not readonly: the settings UI can change the combo and the timings while running.
    private HotkeyCombo _combo;

    private readonly System.Windows.Forms.Timer _armTimer;   // modifiers-only hold threshold
    private readonly System.Windows.Forms.Timer _maxTimer;   // runaway guard

    private bool _enabled;
    private bool _holding;        // a dictation hold is in progress
    private bool _triggerDown;    // trigger key physically down (filters auto-repeat)
    private bool _arming;         // modifiers-only: waiting out the hold threshold

    /// <summary>
    /// Physical modifier state, tracked from the hook's own events.
    ///
    /// This exists because GetAsyncKeyState alone is not trustworthy here.
    /// TextInjector releases held modifiers before typing, and those synthetic key-ups
    /// make GetAsyncKeyState report a modifier as up while the user is still holding the
    /// key down - there is no second key-down to resync it, because the key never
    /// physically moved. The hotkey then silently stopped matching until the user let go
    /// of the modifiers and pressed them again.
    ///
    /// The hook filters out this app's own injected input by signature before we see it,
    /// so these events describe only real key movement.
    /// </summary>
    private HotModifiers _physicalModifiers;

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

    /// <summary>
    /// Re-reads the hotkey settings after they change. Must run on the owning UI thread,
    /// since it touches the WinForms timers the hook callback uses.
    ///
    /// Any hold in progress is abandoned rather than carried across the change: the combo
    /// that started it may no longer exist, so there would be no key release that could
    /// cleanly end it.
    /// </summary>
    public void Reconfigure()
    {
        var wasEnabled = _enabled;

        if (_holding) EndHold(cancelled: true);
        CancelArm();
        _triggerDown = false;

        _combo = HotkeyCombo.Parse(_settings.Combo);
        _armTimer.Interval = Math.Max(1, _settings.ModifiersOnlyHoldMs);
        _maxTimer.Interval = Math.Max(1000, _settings.MaxRecordingSeconds * 1000);

        // Re-seed physical state: we may have missed key movement while resetting.
        if (wasEnabled) _physicalModifiers = QueryModifiers();

        _logger.LogInformation("Hotkey reconfigured to {Combo}.", _combo);
    }

    public void Enable()
    {
        // Seed from the OS: the hook was not installed until now, so any modifier already
        // held has no tracked key-down. Injected key-ups cannot make this a false positive,
        // since this app only ever synthesises modifier key-*ups*.
        _physicalModifiers = QueryModifiers();
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

        // Record the key movement before deciding anything, so the state already reflects
        // the event being handled - including a modifier's own key-up.
        TrackModifier(key, e.IsKeyDown);
        var modifiers = CurrentModifiers();

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

        if (_combo.ModifiersSatisfied(CurrentModifiers()))
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

    /// <summary>Maps a normalised modifier key to its flag; None for anything else.</summary>
    private static HotModifiers FlagFor(Keys key) => key switch
    {
        Keys.ControlKey => HotModifiers.Control,
        Keys.ShiftKey => HotModifiers.Shift,
        Keys.Menu => HotModifiers.Alt,
        Keys.LWin or Keys.RWin => HotModifiers.Win,
        _ => HotModifiers.None
    };

    private void TrackModifier(Keys key, bool isDown)
    {
        var flag = FlagFor(key);
        if (flag == HotModifiers.None) return;

        if (isDown) _physicalModifiers |= flag;
        else _physicalModifiers &= ~flag;
    }

    /// <summary>
    /// What the user is physically holding: the union of our tracked state and the OS
    /// state.
    ///
    /// Tracked-but-not-OS covers the injector's synthetic key-ups, where the key is still
    /// held but the OS has been told otherwise. OS-but-not-tracked covers a key-down this
    /// app never saw, because the hook was not installed at the time - so a bit that goes
    /// stale corrects itself the next time that key is actually pressed.
    /// </summary>
    private HotModifiers CurrentModifiers() => _physicalModifiers | QueryModifiers();

    private static HotModifiers QueryModifiers()
    {
        var m = HotModifiers.None;
        if (Win32.IsKeyDown(Win32.VK_CONTROL)) m |= HotModifiers.Control;
        if (Win32.IsKeyDown(Win32.VK_SHIFT)) m |= HotModifiers.Shift;
        if (Win32.IsKeyDown(Win32.VK_MENU)) m |= HotModifiers.Alt;
        if (Win32.IsKeyDown(Win32.VK_LWIN) || Win32.IsKeyDown(Win32.VK_RWIN)) m |= HotModifiers.Win;
        return m;
    }

    public void Dispose()
    {
        _hook.KeyEvent -= OnKeyEvent;
        _armTimer.Dispose();
        _maxTimer.Dispose();
    }
}
