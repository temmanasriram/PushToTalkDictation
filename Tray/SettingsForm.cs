using Microsoft.Extensions.Logging;
using NAudio.CoreAudioApi;
using PushToTalkDictation.Configuration;

namespace PushToTalkDictation.Tray;

/// <summary>
/// The settings window, opened from the tray menu.
///
/// Built in code rather than with the designer, to match the rest of the UI and keep the
/// project free of generated files. It edits a draft copy and only touches the live
/// settings when Save succeeds, so Cancel genuinely changes nothing.
///
/// It deliberately covers the settings people actually change, not every key in the file.
/// The per-engine model paths and file names stay in <c>appsettings.json</c>, where the
/// comments explain them - the *Advanced* button opens that file for those.
/// </summary>
public sealed class SettingsForm : Form
{
    private const int LabelWidth = 200;
    private const int FieldLeft = 216;
    private const int FieldWidth = 260;
    private const int RowHeight = 30;

    private readonly AppSettings _draft;
    private readonly SettingsService _settings;
    private readonly ILogger _logger;

    // Hotkey
    private readonly TextBox _combo = new();
    private readonly Label _comboError = new();
    private readonly CheckBox _suppressTrigger = new();
    private readonly NumericUpDown _modifiersOnlyHold = new();
    private readonly NumericUpDown _maxRecording = new();

    // Audio
    private readonly ComboBox _device = new();
    private readonly NumericUpDown _minClip = new();
    private readonly NumericUpDown _silenceRms = new();
    private readonly CheckBox _chunkLong = new();
    private readonly NumericUpDown _chunkSeconds = new();

    // Speech
    private readonly ComboBox _engine = new();
    private readonly CheckBox _warmUp = new();
    private readonly CheckBox _unloadOnInactive = new();

    // Typing
    private readonly NumericUpDown _keystrokeDelay = new();
    private readonly NumericUpDown _clipboardThreshold = new();
    private readonly NumericUpDown _modifierWait = new();
    private readonly CheckBox _trailingSpace = new();
    private readonly CheckBox _restoreTarget = new();
    private readonly CheckBox _showOverlay = new();

    // General
    private readonly ComboBox _logLevel = new();
    private readonly CheckBox _startActive = new();

    public SettingsForm(SettingsService settings, ILogger logger)
    {
        _settings = settings;
        _logger = logger;
        _draft = settings.CreateDraft();

        Text = $"{AppInfo.ProductName} settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = true;
        ClientSize = new Size(520, 430);
        Font = SystemFonts.MessageBoxFont ?? Font;

        var tabs = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 6) };
        tabs.TabPages.Add(BuildHotkeyTab());
        tabs.TabPages.Add(BuildAudioTab());
        tabs.TabPages.Add(BuildSpeechTab());
        tabs.TabPages.Add(BuildTypingTab());
        tabs.TabPages.Add(BuildGeneralTab());

        Controls.Add(tabs);
        Controls.Add(BuildButtonBar());

        Load += (_, _) => ReadFromDraft();
    }

    // ------------------------------------------------------------------ tabs

    private TabPage BuildHotkeyTab()
    {
        var page = NewPage("Hotkey");
        var y = 14;

        Row(page, ref y, "Hold-to-talk combo", _combo,
            "Modifiers plus one key, e.g. Ctrl+Shift+Space, Ctrl+Alt+D, F9");
        _combo.TextChanged += (_, _) => ValidateCombo();

        _comboError.SetBounds(FieldLeft, y, FieldWidth + 30, 34);
        _comboError.ForeColor = Color.Firebrick;
        page.Controls.Add(_comboError);
        y += 40;

        Row(page, ref y, "Swallow the trigger key", _suppressTrigger,
            "Keeps the key itself out of the window you are typing into");

        Spin(_modifiersOnlyHold, 1, 5000, 10);
        Row(page, ref y, "Modifiers-only hold (ms)", _modifiersOnlyHold,
            "Only used when the combo has no ordinary key, e.g. \"Ctrl+Shift\"");

        Spin(_maxRecording, 1, 3600, 10);
        Row(page, ref y, "Maximum recording (seconds)", _maxRecording,
            "Hard stop, so a stuck key cannot record forever");

        return page;
    }

    private TabPage BuildAudioTab()
    {
        var page = NewPage("Audio");
        var y = 14;

        _device.DropDownStyle = ComboBoxStyle.DropDownList;
        Row(page, ref y, "Microphone", _device,
            "Applies to the next time you hold the hotkey");

        Spin(_minClip, 0, 5000, 50);
        Row(page, ref y, "Ignore clips shorter than (ms)", _minClip,
            "Stops an accidental tap from being transcribed");

        // RMS is a small fraction; show it in units of 0.0001 to keep the spinner usable.
        Spin(_silenceRms, 0, 1000, 5);
        Row(page, ref y, "Silence threshold (×0.0001)", _silenceRms,
            "25 is the default. Raise it if stray taps produce text, lower it if quiet speech is dropped");

        Row(page, ref y, "Type while I speak", _chunkLong,
            "Transcribes in segments as you hold, instead of all at once on release");
        _chunkLong.CheckedChanged += (_, _) => _chunkSeconds.Enabled = _chunkLong.Checked;

        Spin(_chunkSeconds, 5, 120, 5);
        Row(page, ref y, "Segment length (seconds)", _chunkSeconds,
            "20 is a good default. Above about 45 the model starts dropping words");

        return page;
    }

    private TabPage BuildSpeechTab()
    {
        var page = NewPage("Speech");
        var y = 14;

        _engine.DropDownStyle = ComboBoxStyle.DropDownList;
        _engine.Items.AddRange([.. Enum.GetNames<SttEngineKind>().Cast<object>()]);
        Row(page, ref y, "Engine  (needs a restart)", _engine,
            "SherpaOnnx is the local default. Model files are set in appsettings.json");

        Row(page, ref y, "Load the model at startup", _warmUp,
            "Avoids a pause on the first phrase after launching");

        Row(page, ref y, "Free memory when switched off", _unloadOnInactive,
            "Releases about 270 MB while off; costs about a second when switching back on");

        return page;
    }

    private TabPage BuildTypingTab()
    {
        var page = NewPage("Typing");
        var y = 14;

        Row(page, ref y, "Show a preview while speaking", _showOverlay,
            "A small floating caption of what is being heard; only finished text is typed");

        Row(page, ref y, "Add a trailing space", _trailingSpace,
            "Keeps consecutive dictations from running together");

        Row(page, ref y, "Return focus to the window", _restoreTarget,
            "Types into the window you were speaking into, even if focus moved meanwhile");

        Spin(_clipboardThreshold, 0, 100000, 20);
        Row(page, ref y, "Paste above this many chars", _clipboardThreshold,
            "0 always types. Typing long passages is slow; pasting is instant");

        Spin(_keystrokeDelay, 0, 100, 1);
        Row(page, ref y, "Delay between batches (ms)", _keystrokeDelay,
            "Raise to 1-2 only if a particular app drops characters");

        Spin(_modifierWait, 0, 5000, 50);
        Row(page, ref y, "Wait for keys to be released (ms)", _modifierWait,
            "Stops dictated text turning into keyboard shortcuts");

        return page;
    }

    private TabPage BuildGeneralTab()
    {
        var page = NewPage("General");
        var y = 14;

        Row(page, ref y, "Start listening on launch", _startActive,
            "Off means you switch it on from the tray each session");

        _logLevel.DropDownStyle = ComboBoxStyle.DropDownList;
        _logLevel.Items.AddRange(["Trace", "Debug", "Information", "Warning", "Error", "Critical"]);
        Row(page, ref y, "Log detail", _logLevel,
            "Debug records the microphone, timings and every discarded clip");

        var note = new Label
        {
            Text = "Changes are saved to appsettings.user.json next to the program, so the "
                   + "commented defaults in appsettings.json stay intact. Delete that file to "
                   + "return to the defaults.",
            AutoSize = false
        };
        note.SetBounds(14, y + 12, 470, 60);
        note.ForeColor = SystemColors.GrayText;
        page.Controls.Add(note);

        return page;
    }

    private Panel BuildButtonBar()
    {
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 52 };

        var advanced = new Button { Text = "Advanced...", Width = 100 };
        advanced.SetBounds(12, 12, 100, 28);
        advanced.Click += (_, _) => OpenRawSettings();

        var cancel = new Button { Text = "Cancel", Width = 88, DialogResult = DialogResult.Cancel };
        cancel.SetBounds(320, 12, 88, 28);

        var save = new Button { Text = "Save", Width = 88 };
        save.SetBounds(416, 12, 88, 28);
        save.Click += (_, _) => OnSave();

        bar.Controls.AddRange([advanced, cancel, save]);
        AcceptButton = save;
        CancelButton = cancel;
        return bar;
    }

    // ------------------------------------------------------------- draft <-> ui

    private void ReadFromDraft()
    {
        _combo.Text = _draft.Hotkey.Combo;
        _suppressTrigger.Checked = _draft.Hotkey.SuppressTriggerKey;
        _modifiersOnlyHold.Value = Clamp(_modifiersOnlyHold, _draft.Hotkey.ModifiersOnlyHoldMs);
        _maxRecording.Value = Clamp(_maxRecording, _draft.Hotkey.MaxRecordingSeconds);

        PopulateDevices(_draft.Audio.DeviceId);
        _minClip.Value = Clamp(_minClip, _draft.Audio.MinClipMs);
        _silenceRms.Value = Clamp(_silenceRms, (int)Math.Round(_draft.Audio.SilenceRmsThreshold * 10000));
        _chunkLong.Checked = _draft.Audio.ChunkLongDictation;
        _chunkSeconds.Value = Clamp(_chunkSeconds, _draft.Audio.ChunkSeconds);
        _chunkSeconds.Enabled = _chunkLong.Checked;

        _engine.SelectedItem = _draft.SpeechToText.Engine.ToString();
        _warmUp.Checked = _draft.SpeechToText.WarmUpOnStart;
        _unloadOnInactive.Checked = _draft.SpeechToText.UnloadOnInactive;

        _keystrokeDelay.Value = Clamp(_keystrokeDelay, _draft.Injection.KeystrokeDelayMs);
        _clipboardThreshold.Value = Clamp(_clipboardThreshold, _draft.Injection.ClipboardThreshold);
        _modifierWait.Value = Clamp(_modifierWait, _draft.Injection.WaitForModifierReleaseMs);
        _trailingSpace.Checked = _draft.Injection.AppendTrailingSpace;
        _restoreTarget.Checked = _draft.Injection.RestoreTargetWindow;
        _showOverlay.Checked = _draft.Preview.ShowOverlay;

        _logLevel.SelectedItem = NormalizeLevel(_draft.Logging.MinimumLevel);
        _startActive.Checked = _draft.StartActive;

        ValidateCombo();
    }

    private void WriteToDraft()
    {
        _draft.Hotkey.Combo = _combo.Text.Trim();
        _draft.Hotkey.SuppressTriggerKey = _suppressTrigger.Checked;
        _draft.Hotkey.ModifiersOnlyHoldMs = (int)_modifiersOnlyHold.Value;
        _draft.Hotkey.MaxRecordingSeconds = (int)_maxRecording.Value;

        _draft.Audio.DeviceId = (_device.SelectedItem as DeviceChoice)?.Id;
        _draft.Audio.MinClipMs = (int)_minClip.Value;
        _draft.Audio.SilenceRmsThreshold = (float)(_silenceRms.Value / 10000m);
        _draft.Audio.ChunkLongDictation = _chunkLong.Checked;
        _draft.Audio.ChunkSeconds = (int)_chunkSeconds.Value;

        if (_engine.SelectedItem is string engine && Enum.TryParse<SttEngineKind>(engine, out var kind))
            _draft.SpeechToText.Engine = kind;
        _draft.SpeechToText.WarmUpOnStart = _warmUp.Checked;
        _draft.SpeechToText.UnloadOnInactive = _unloadOnInactive.Checked;

        _draft.Injection.KeystrokeDelayMs = (int)_keystrokeDelay.Value;
        _draft.Injection.ClipboardThreshold = (int)_clipboardThreshold.Value;
        _draft.Injection.WaitForModifierReleaseMs = (int)_modifierWait.Value;
        _draft.Injection.AppendTrailingSpace = _trailingSpace.Checked;
        _draft.Injection.RestoreTargetWindow = _restoreTarget.Checked;
        _draft.Preview.ShowOverlay = _showOverlay.Checked;

        _draft.Logging.MinimumLevel = _logLevel.SelectedItem as string ?? "Information";
        _draft.StartActive = _startActive.Checked;
    }

    private void OnSave()
    {
        WriteToDraft();

        var problem = SettingsService.Validate(_draft);
        if (problem is not null)
        {
            MessageBox.Show(this, problem, "That setting will not work",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        try
        {
            var restartNeeded = _settings.Apply(_draft);

            if (restartNeeded.Count > 0)
            {
                MessageBox.Show(this,
                    "Saved. Everything is in effect now except:"
                    + Environment.NewLine + Environment.NewLine
                    + "  " + string.Join(Environment.NewLine + "  ", restartNeeded)
                    + Environment.NewLine + Environment.NewLine
                    + "Exit and start the app again to pick those up.",
                    "Restart needed for some changes", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }

            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Could not save settings.");
            MessageBox.Show(this,
                $"Could not save to {_settings.StorePath}:{Environment.NewLine}{ex.Message}",
                "Save failed", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ValidateCombo()
    {
        try
        {
            var parsed = Input.HotkeyCombo.Parse(_combo.Text.Trim());
            _comboError.ForeColor = SystemColors.GrayText;
            _comboError.Text = parsed.IsModifiersOnly
                ? $"Reads as {parsed}. Modifiers-only combos fire on other shortcuts too."
                : $"Reads as {parsed}.";
        }
        catch (FormatException ex)
        {
            _comboError.ForeColor = Color.Firebrick;
            _comboError.Text = ex.Message;
        }
    }

    private void PopulateDevices(string? selectedId)
    {
        _device.Items.Clear();
        _device.Items.Add(new DeviceChoice(null, "System default"));

        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
                _device.Items.Add(new DeviceChoice(device.ID, device.FriendlyName));
        }
        catch (Exception ex)
        {
            // Not fatal: the list just falls back to "System default".
            _logger.LogWarning(ex, "Could not enumerate capture devices.");
        }

        _device.SelectedIndex = 0;
        if (string.IsNullOrWhiteSpace(selectedId)) return;

        for (var i = 0; i < _device.Items.Count; i++)
        {
            if (_device.Items[i] is DeviceChoice c && c.Id == selectedId)
            {
                _device.SelectedIndex = i;
                return;
            }
        }

        // Configured device is not present right now; keep it rather than silently dropping it.
        _device.Items.Add(new DeviceChoice(selectedId, "(configured device, not connected)"));
        _device.SelectedIndex = _device.Items.Count - 1;
    }

    private void OpenRawSettings()
    {
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open '{Path}'.", path);
        }
    }

    // ----------------------------------------------------------------- helpers

    private static TabPage NewPage(string text) => new(text) { UseVisualStyleBackColor = true };

    /// <summary>Lays out one label + control + hint row and advances <paramref name="y"/>.</summary>
    private static void Row(TabPage page, ref int y, string label, Control field, string? hint = null)
    {
        var caption = new Label { Text = label, AutoSize = false, TextAlign = ContentAlignment.MiddleLeft };
        caption.SetBounds(14, y, LabelWidth, 22);
        page.Controls.Add(caption);

        if (field is CheckBox check)
        {
            check.Text = string.Empty;
            check.SetBounds(FieldLeft, y + 2, 20, 20);
        }
        else
        {
            field.SetBounds(FieldLeft, y, FieldWidth, 22);
        }

        page.Controls.Add(field);
        y += 24;

        if (hint is not null)
        {
            var note = new Label { Text = hint, AutoSize = false, ForeColor = SystemColors.GrayText };
            note.SetBounds(16, y, 470, 16);
            note.Font = new Font(note.Font.FontFamily, note.Font.Size - 0.5f);
            page.Controls.Add(note);
            y += 18;
        }

        y += RowHeight - 24;
    }

    private static void Spin(NumericUpDown spinner, int min, int max, int increment)
    {
        spinner.Minimum = min;
        spinner.Maximum = max;
        spinner.Increment = increment;
        spinner.ThousandsSeparator = true;
    }

    private static decimal Clamp(NumericUpDown spinner, int value) =>
        Math.Clamp(value, spinner.Minimum, spinner.Maximum);

    private static string NormalizeLevel(string value) =>
        Enum.TryParse<LogLevel>(value, ignoreCase: true, out var level) ? level.ToString() : "Information";

    private sealed record DeviceChoice(string? Id, string Name)
    {
        public override string ToString() => Name;
    }
}
