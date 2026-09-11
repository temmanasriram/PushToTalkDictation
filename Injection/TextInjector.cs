using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Extensions.Logging;
using PushToTalkDictation.Configuration;
using PushToTalkDictation.Interop;

namespace PushToTalkDictation.Injection;

public interface ITextInjector
{
    Task InjectAsync(string text, CancellationToken ct = default);
}

/// <summary>
/// Types text into whatever window currently has focus, using SendInput with
/// KEYEVENTF_UNICODE.
///
/// Unicode injection is layout-independent: it delivers the exact code unit rather
/// than a scan code the target re-maps, so accented characters, em dashes and emoji
/// arrive intact regardless of the user's keyboard layout. Spaces and punctuation
/// are ordinary code units, so spacing is preserved verbatim.
///
/// Two things matter for correctness and both are handled here:
///   * Physical modifiers must be up first. If the user is still holding Ctrl when we
///     start typing, the target app sees Ctrl+&lt;char&gt; and runs shortcuts instead of
///     inserting text.
///   * Every synthesised event carries our dwExtraInfo signature so the app's own
///     keyboard hook ignores it.
/// </summary>
public sealed class TextInjector : ITextInjector
{
    private const int MaxInputsPerCall = 200;

    private readonly InjectionSettings _settings;
    private readonly ILogger<TextInjector> _logger;

    public TextInjector(AppSettings settings, ILogger<TextInjector> logger)
    {
        _settings = settings.Injection;
        _logger = logger;
    }

    public async Task InjectAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(text)) return;

        await WaitForModifierReleaseAsync(ct).ConfigureAwait(false);
        ClearStuckModifiers();

        if (_settings.ClipboardThreshold > 0 && text.Length >= _settings.ClipboardThreshold)
        {
            if (await TryPasteAsync(text, ct).ConfigureAwait(false))
                return;

            _logger.LogWarning("Clipboard paste failed; falling back to keystroke injection.");
        }

        SendAsKeystrokes(text, ct);
    }

    // -------------------------------------------------------------- keystrokes

    private void SendAsKeystrokes(string text, CancellationToken ct)
    {
        var batch = new List<Win32.INPUT>(MaxInputsPerCall);

        foreach (var c in text)
        {
            ct.ThrowIfCancellationRequested();

            switch (c)
            {
                case '\r':
                    continue; // \r\n is handled by the \n below
                case '\n':
                    batch.Add(Win32.VirtualKey((ushort)Win32.VK_RETURN, keyUp: false));
                    batch.Add(Win32.VirtualKey((ushort)Win32.VK_RETURN, keyUp: true));
                    break;
                case '\t':
                    batch.Add(Win32.VirtualKey((ushort)Win32.VK_TAB, keyUp: false));
                    batch.Add(Win32.VirtualKey((ushort)Win32.VK_TAB, keyUp: true));
                    break;
                default:
                    // Surrogate pairs go through as two consecutive unicode inputs,
                    // which is exactly what Windows expects.
                    batch.Add(Win32.UnicodeKey(c, keyUp: false));
                    batch.Add(Win32.UnicodeKey(c, keyUp: true));
                    break;
            }

            if (batch.Count >= MaxInputsPerCall)
                Flush(batch, ct);
        }

        Flush(batch, ct);
    }

    private void Flush(List<Win32.INPUT> batch, CancellationToken ct)
    {
        if (batch.Count == 0) return;

        var inputs = batch.ToArray();
        batch.Clear();

        var sent = Win32.SendInput((uint)inputs.Length, inputs, Win32.InputSize);
        if (sent != inputs.Length)
        {
            var error = Marshal.GetLastWin32Error();
            _logger.LogError(
                "SendInput delivered {Sent}/{Total} events (Win32 error {Error}). " +
                "A higher-integrity window in the foreground will block injection unless this app is elevated.",
                sent, inputs.Length, error);
        }

        if (_settings.KeystrokeDelayMs > 0 && !ct.IsCancellationRequested)
            Thread.Sleep(_settings.KeystrokeDelayMs);
    }

    // --------------------------------------------------------------- clipboard

    /// <summary>
    /// Long transcriptions are pasted rather than typed: a 400-character SendInput
    /// burst is visibly slow and some editors drop characters under it.
    /// The previous clipboard contents are restored afterwards.
    /// </summary>
    private async Task<bool> TryPasteAsync(string text, CancellationToken ct)
    {
        IDataObject? previous = null;

        try
        {
            previous = await RunStaAsync(() => Clipboard.GetDataObject()).ConfigureAwait(false);
            await RunStaAsync(() => { Clipboard.SetText(text); return true; }).ConfigureAwait(false);

            // Give the target's clipboard listener a moment before Ctrl+V.
            await Task.Delay(30, ct).ConfigureAwait(false);

            var inputs = new[]
            {
                Win32.VirtualKey((ushort)Win32.VK_CONTROL, keyUp: false),
                Win32.VirtualKey(0x56, keyUp: false), // 'V'
                Win32.VirtualKey(0x56, keyUp: true),
                Win32.VirtualKey((ushort)Win32.VK_CONTROL, keyUp: true)
            };

            Win32.SendInput((uint)inputs.Length, inputs, Win32.InputSize);

            await Task.Delay(120, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Clipboard injection failed.");
            return false;
        }
        finally
        {
            if (previous is not null)
            {
                try
                {
                    await RunStaAsync(() => { Clipboard.SetDataObject(previous, true); return true; })
                        .ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Could not restore the previous clipboard contents.");
                }
            }
        }
    }

    /// <summary>The Windows clipboard API is STA-only; run the call on a dedicated STA thread.</summary>
    private static Task<T> RunStaAsync<T>(Func<T> action)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            try { tcs.SetResult(action()); }
            catch (Exception ex) { tcs.SetException(ex); }
        })
        {
            IsBackground = true
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        return tcs.Task;
    }

    // --------------------------------------------------------------- modifiers

    private async Task WaitForModifierReleaseAsync(CancellationToken ct)
    {
        var budget = _settings.WaitForModifierReleaseMs;
        if (budget <= 0) return;

        var deadline = Environment.TickCount64 + budget;
        while (AnyModifierDown() && Environment.TickCount64 < deadline)
            await Task.Delay(10, ct).ConfigureAwait(false);

        if (AnyModifierDown())
            _logger.LogDebug("Modifiers still held after {Ms} ms; injecting anyway.", budget);
    }

    private static bool AnyModifierDown() =>
        Win32.IsKeyDown(Win32.VK_CONTROL) ||
        Win32.IsKeyDown(Win32.VK_SHIFT) ||
        Win32.IsKeyDown(Win32.VK_MENU) ||
        Win32.IsKeyDown(Win32.VK_LWIN) ||
        Win32.IsKeyDown(Win32.VK_RWIN);

    /// <summary>
    /// Sends key-up for every modifier. Harmless when they are already up, and it
    /// clears the "stuck Ctrl" state that a suppressed key-up can otherwise leave in
    /// the target window's view of the keyboard.
    /// </summary>
    private static void ClearStuckModifiers()
    {
        if (!AnyModifierDown()) return;

        var inputs = new[]
        {
            Win32.VirtualKey((ushort)Win32.VK_LCONTROL, keyUp: true),
            Win32.VirtualKey((ushort)Win32.VK_RCONTROL, keyUp: true),
            Win32.VirtualKey((ushort)Win32.VK_LSHIFT, keyUp: true),
            Win32.VirtualKey((ushort)Win32.VK_RSHIFT, keyUp: true),
            Win32.VirtualKey((ushort)Win32.VK_LMENU, keyUp: true),
            Win32.VirtualKey((ushort)Win32.VK_RMENU, keyUp: true)
        };

        Win32.SendInput((uint)inputs.Length, inputs, Win32.InputSize);
    }
}
