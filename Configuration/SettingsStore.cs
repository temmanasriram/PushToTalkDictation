using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PushToTalkDictation.Configuration;

/// <summary>
/// Reads and writes the user's settings overrides.
///
/// The settings UI never rewrites <c>appsettings.json</c>. That file is the documented
/// default set - it is full of explanatory comments, and a serializer would strip every one
/// of them. Instead the UI writes only the values that differ from it into
/// <c>appsettings.user.json</c>, which <see cref="Program"/> layers on top. Three things
/// fall out of that: the comments survive, a diff shows exactly what was changed, and
/// deleting the file restores the shipped defaults.
/// </summary>
public sealed class SettingsStore
{
    public const string FileName = "appsettings.user.json";

    /// <summary>Settings as configured before the user overrides layer - what to diff against.</summary>
    private readonly AppSettings _baseline;

    public string Path { get; }

    public SettingsStore(AppSettings baseline)
    {
        _baseline = baseline;
        Path = System.IO.Path.Combine(AppContext.BaseDirectory, FileName);
    }

    /// <summary>
    /// Writes the values in <paramref name="edited"/> that differ from the baseline.
    /// Deletes the file when nothing differs, so reverting every change leaves no residue.
    /// </summary>
    public void Save(AppSettings edited)
    {
        var root = new JsonObject();

        var hotkey = Section(root, "Hotkey");
        Add(hotkey, "Combo", edited.Hotkey.Combo, _baseline.Hotkey.Combo);
        Add(hotkey, "SuppressTriggerKey", edited.Hotkey.SuppressTriggerKey, _baseline.Hotkey.SuppressTriggerKey);
        Add(hotkey, "ModifiersOnlyHoldMs", edited.Hotkey.ModifiersOnlyHoldMs, _baseline.Hotkey.ModifiersOnlyHoldMs);
        Add(hotkey, "MaxRecordingSeconds", edited.Hotkey.MaxRecordingSeconds, _baseline.Hotkey.MaxRecordingSeconds);

        var audio = Section(root, "Audio");
        Add(audio, "DeviceId", edited.Audio.DeviceId, _baseline.Audio.DeviceId);
        Add(audio, "MinClipMs", edited.Audio.MinClipMs, _baseline.Audio.MinClipMs);
        Add(audio, "SilenceRmsThreshold", edited.Audio.SilenceRmsThreshold, _baseline.Audio.SilenceRmsThreshold);
        Add(audio, "ChunkLongDictation", edited.Audio.ChunkLongDictation, _baseline.Audio.ChunkLongDictation);
        Add(audio, "ChunkSeconds", edited.Audio.ChunkSeconds, _baseline.Audio.ChunkSeconds);

        var stt = Section(root, "SpeechToText");
        Add(stt, "Engine", edited.SpeechToText.Engine.ToString(), _baseline.SpeechToText.Engine.ToString());
        Add(stt, "WarmUpOnStart", edited.SpeechToText.WarmUpOnStart, _baseline.SpeechToText.WarmUpOnStart);
        Add(stt, "UnloadOnInactive", edited.SpeechToText.UnloadOnInactive, _baseline.SpeechToText.UnloadOnInactive);

        var injection = Section(root, "Injection");
        Add(injection, "KeystrokeDelayMs", edited.Injection.KeystrokeDelayMs, _baseline.Injection.KeystrokeDelayMs);
        Add(injection, "ClipboardThreshold", edited.Injection.ClipboardThreshold, _baseline.Injection.ClipboardThreshold);
        Add(injection, "WaitForModifierReleaseMs", edited.Injection.WaitForModifierReleaseMs, _baseline.Injection.WaitForModifierReleaseMs);
        Add(injection, "AppendTrailingSpace", edited.Injection.AppendTrailingSpace, _baseline.Injection.AppendTrailingSpace);
        Add(injection, "RestoreTargetWindow", edited.Injection.RestoreTargetWindow, _baseline.Injection.RestoreTargetWindow);

        var logging = Section(root, "Logging");
        Add(logging, "MinimumLevel", edited.Logging.MinimumLevel, _baseline.Logging.MinimumLevel);

        Add(root, "StartActive", edited.StartActive, _baseline.StartActive);

        // Drop sections that ended up empty so the file only ever shows real changes.
        foreach (var name in root.Select(pair => pair.Key).ToArray())
        {
            if (root[name] is JsonObject { Count: 0 })
                root.Remove(name);
        }

        if (root.Count == 0)
        {
            if (File.Exists(Path)) File.Delete(Path);
            return;
        }

        // Relaxed escaping keeps a combo readable as "Ctrl+Alt+D". The default encoder
        // escapes the plus character for HTML safety, which buys nothing in a config file
        // and costs legibility for anyone who opens it.
        var json = root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        File.WriteAllText(Path, json + Environment.NewLine);
    }

    private static JsonObject Section(JsonObject root, string name)
    {
        var section = new JsonObject();
        root[name] = section;
        return section;
    }

    private static void Add<T>(JsonObject target, string key, T value, T baseline)
    {
        if (EqualityComparer<T>.Default.Equals(value, baseline)) return;
        target[key] = JsonValue.Create(value);
    }

    /// <summary>
    /// Copies the editable values onto the live settings instance, mutating the nested
    /// objects in place rather than replacing them.
    ///
    /// That is deliberate and load-bearing: <c>TextInjector</c>, <c>AudioRecorder</c> and
    /// <c>HotkeyWatcher</c> each hold a reference to one of the nested settings objects and
    /// read it on every use, which is what makes most changes apply without a restart.
    /// Assigning a new nested object would leave them pointing at the old one.
    /// </summary>
    public static void CopyInto(AppSettings source, AppSettings target)
    {
        target.Hotkey.Combo = source.Hotkey.Combo;
        target.Hotkey.SuppressTriggerKey = source.Hotkey.SuppressTriggerKey;
        target.Hotkey.ModifiersOnlyHoldMs = source.Hotkey.ModifiersOnlyHoldMs;
        target.Hotkey.MaxRecordingSeconds = source.Hotkey.MaxRecordingSeconds;

        target.Audio.DeviceId = source.Audio.DeviceId;
        target.Audio.MinClipMs = source.Audio.MinClipMs;
        target.Audio.SilenceRmsThreshold = source.Audio.SilenceRmsThreshold;
        target.Audio.ChunkLongDictation = source.Audio.ChunkLongDictation;
        target.Audio.ChunkSeconds = source.Audio.ChunkSeconds;

        target.SpeechToText.Engine = source.SpeechToText.Engine;
        target.SpeechToText.WarmUpOnStart = source.SpeechToText.WarmUpOnStart;
        target.SpeechToText.UnloadOnInactive = source.SpeechToText.UnloadOnInactive;

        target.Injection.KeystrokeDelayMs = source.Injection.KeystrokeDelayMs;
        target.Injection.ClipboardThreshold = source.Injection.ClipboardThreshold;
        target.Injection.WaitForModifierReleaseMs = source.Injection.WaitForModifierReleaseMs;
        target.Injection.AppendTrailingSpace = source.Injection.AppendTrailingSpace;
        target.Injection.RestoreTargetWindow = source.Injection.RestoreTargetWindow;

        target.Logging.MinimumLevel = source.Logging.MinimumLevel;

        target.StartActive = source.StartActive;
    }

    /// <summary>A copy of the editable values, for the form to work on without touching the live set.</summary>
    public static AppSettings Clone(AppSettings source)
    {
        var copy = new AppSettings();
        CopyInto(source, copy);
        return copy;
    }
}
