using Microsoft.Extensions.Logging;
using PushToTalkDictation.Diagnostics;
using PushToTalkDictation.Input;

namespace PushToTalkDictation.Configuration;

/// <summary>
/// The one place that knows how to put edited settings into effect.
///
/// Most settings apply immediately, because the components that use them read the shared
/// settings objects on every use. Two do not, and cannot honestly be made to:
///
///   * <c>SpeechToText.Engine</c> and the per-engine model paths. The engine is a singleton
///     picked by the factory when the container is built; swapping it mid-session would mean
///     tearing down a loaded native model underneath a possibly-running decode.
///   * <c>Logging.FilePath</c>. The log writer owns an open path for the process lifetime.
///
/// <see cref="Apply"/> returns what needs a restart so the UI can say so plainly instead of
/// leaving the user to wonder whether the change took.
/// </summary>
public sealed class SettingsService
{
    private readonly AppSettings _live;
    private readonly SettingsStore _store;
    private readonly HotkeyWatcher _hotkeys;
    private readonly FileLoggerProvider _logProvider;
    private readonly ILogger<SettingsService> _logger;

    public SettingsService(
        AppSettings live,
        SettingsStore store,
        HotkeyWatcher hotkeys,
        FileLoggerProvider logProvider,
        ILogger<SettingsService> logger)
    {
        _live = live;
        _store = store;
        _hotkeys = hotkeys;
        _logProvider = logProvider;
        _logger = logger;
    }

    public string StorePath => _store.Path;

    /// <summary>A working copy for the UI to edit.</summary>
    public AppSettings CreateDraft() => SettingsStore.Clone(_live);

    /// <summary>
    /// Validates a draft. Returns null when it is usable, or a message naming the problem.
    /// </summary>
    public static string? Validate(AppSettings draft)
    {
        try
        {
            HotkeyCombo.Parse(draft.Hotkey.Combo);
        }
        catch (FormatException ex)
        {
            return $"Hotkey: {ex.Message}";
        }

        if (!Enum.TryParse<LogLevel>(draft.Logging.MinimumLevel, ignoreCase: true, out _))
            return $"Logging level '{draft.Logging.MinimumLevel}' is not a known level.";

        if (draft.Hotkey.MaxRecordingSeconds < 1)
            return "Maximum recording seconds must be at least 1.";

        return null;
    }

    /// <summary>
    /// Persists the draft and applies everything that can take effect now.
    /// Must be called on the UI thread: it reconfigures the keyboard hook's watcher.
    /// </summary>
    /// <returns>Descriptions of changes that only take effect after a restart; empty if none.</returns>
    public IReadOnlyList<string> Apply(AppSettings draft)
    {
        var restartNeeded = new List<string>();

        if (draft.SpeechToText.Engine != _live.SpeechToText.Engine)
            restartNeeded.Add($"Speech engine ({_live.SpeechToText.Engine} → {draft.SpeechToText.Engine})");

        var hotkeyChanged =
            draft.Hotkey.Combo != _live.Hotkey.Combo ||
            draft.Hotkey.ModifiersOnlyHoldMs != _live.Hotkey.ModifiersOnlyHoldMs ||
            draft.Hotkey.MaxRecordingSeconds != _live.Hotkey.MaxRecordingSeconds;

        var logLevelChanged = draft.Logging.MinimumLevel != _live.Logging.MinimumLevel;

        // Live values first, so anything read per-use is already current.
        SettingsStore.CopyInto(draft, _live);
        _store.Save(draft);

        if (hotkeyChanged) _hotkeys.Reconfigure();

        if (logLevelChanged &&
            Enum.TryParse<LogLevel>(_live.Logging.MinimumLevel, ignoreCase: true, out var level))
        {
            _logProvider.SetMinimumLevel(level);
        }

        _logger.LogInformation("Settings saved to '{Path}'.{Restart}", _store.Path,
            restartNeeded.Count == 0 ? string.Empty : " Some changes need a restart.");

        return restartNeeded;
    }
}
