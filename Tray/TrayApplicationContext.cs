using Microsoft.Extensions.Logging;
using PushToTalkDictation.Configuration;

namespace PushToTalkDictation.Tray;

/// <summary>
/// The whole UI: a NotifyIcon and a context menu. There is no window, no taskbar
/// entry and no Alt+Tab entry — <see cref="ApplicationContext"/> keeps the message
/// loop alive without one, which is what the low-level keyboard hook needs.
///
/// Every callback here marshals back to this thread before touching the icon.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{
    private readonly DictationController _controller;
    private readonly AppSettings _settings;
    private readonly ILogger<TrayApplicationContext> _logger;

    private readonly TrayIcons _icons = new();
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _menu;
    private readonly ToolStripMenuItem _toggleItem;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _modelItem;

    // Owns the thread affinity for UI updates coming off the pipeline.
    private readonly SynchronizationContext _uiContext;

    public TrayApplicationContext(
        DictationController controller,
        AppSettings settings,
        ILogger<TrayApplicationContext> logger)
    {
        _controller = controller;
        _settings = settings;
        _logger = logger;
        _uiContext = SynchronizationContext.Current
                     ?? throw new InvalidOperationException("TrayApplicationContext must be built on the UI thread.");

        var versionItem = new ToolStripMenuItem(AppInfo.NameAndVersion) { Enabled = false };

        _toggleItem = new ToolStripMenuItem("Toggle Active (Off)")
        {
            CheckOnClick = false
        };
        _toggleItem.Click += (_, _) => _controller.SetActive(!_controller.IsActive);

        _statusItem = new ToolStripMenuItem("Idle") { Enabled = false };

        var engineItem = new ToolStripMenuItem($"Engine: {_controller.EngineName}") { Enabled = false };
        _modelItem = new ToolStripMenuItem("Model: -") { Enabled = false };
        var hotkeyItem = new ToolStripMenuItem($"Hold: {_controller.Combo}") { Enabled = false };

        var guideItem = new ToolStripMenuItem("User guide");
        guideItem.Click += (_, _) => OpenUserGuide();

        var settingsItem = new ToolStripMenuItem("Open settings file...");
        settingsItem.Click += (_, _) => OpenSettings();

        var logItem = new ToolStripMenuItem("Open log folder...");
        logItem.Click += (_, _) => OpenLogFolder();

        var exitItem = new ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => ExitApplication();

        _menu = new ContextMenuStrip();
        _menu.Items.AddRange(
        [
            versionItem,
            new ToolStripSeparator(),
            _toggleItem,
            new ToolStripSeparator(),
            _statusItem,
            engineItem,
            _modelItem,
            hotkeyItem,
            new ToolStripSeparator(),
            guideItem,
            settingsItem,
            logItem,
            new ToolStripSeparator(),
            exitItem
        ]);

        // The model is released asynchronously after switching off, so this line is
        // refreshed when the menu opens rather than only on a state change.
        _menu.Opening += (_, _) => RefreshModelItem();

        _notifyIcon = new NotifyIcon
        {
            Icon = _icons.Inactive,
            Text = AppInfo.ProductName,
            Visible = true,
            ContextMenuStrip = _menu
        };

        // NotifyIcon only opens the menu on right-click by default; mirror it on left.
        _notifyIcon.MouseUp += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
                _menu.Show(Control.MousePosition);
        };

        _controller.StateChanged += OnStateChanged;
        _controller.Failed += OnFailed;

        _controller.SetActive(_settings.StartActive);

        // Activating already warms the model up, so only warm up here when we did not -
        // otherwise both paths run and the log reports the engine ready twice.
        if (!_controller.IsActive)
            _ = _controller.WarmUpAsync();

        RefreshUi(_controller.State);
    }

    // ------------------------------------------------------------- ui updates

    private void OnStateChanged(object? sender, DictationState state) =>
        _uiContext.Post(_ => RefreshUi(state), null);

    private void OnFailed(object? sender, Exception ex) => _uiContext.Post(_ =>
    {
        _notifyIcon.ShowBalloonTip(4000, "Push-to-Talk Dictation", ex.Message, ToolTipIcon.Error);
    }, null);

    private void RefreshModelItem() =>
        _modelItem.Text = _controller.IsModelLoaded
            ? "Model: loaded in memory"
            : "Model: released (loads on demand)";

    private void RefreshUi(DictationState state)
    {
        _notifyIcon.Icon = _icons.For(state);
        _toggleItem.Text = _controller.IsActive ? "Toggle Active (On)" : "Toggle Active (Off)";
        _toggleItem.Checked = _controller.IsActive;

        _statusItem.Text = state switch
        {
            DictationState.Recording => "Recording...",
            DictationState.Transcribing => "Transcribing...",
            DictationState.Idle => $"Ready - hold {_controller.Combo}",
            _ => "Inactive"
        };

        RefreshModelItem();

        // NotifyIcon.Text is capped at 63 characters.
        var tip = $"Push-to-Talk Dictation - {_statusItem.Text}";
        _notifyIcon.Text = tip.Length <= 63 ? tip : tip[..63];
    }

    // ---------------------------------------------------------------- actions

    /// <summary>
    /// Opens the HTML guide that ships beside the executable, in the default browser.
    /// A missing file means the app was copied without it, so say so rather than
    /// silently doing nothing.
    /// </summary>
    private void OpenUserGuide()
    {
        var path = Path.Combine(AppContext.BaseDirectory, AppInfo.UserGuideFileName);

        if (!File.Exists(path))
        {
            _logger.LogWarning("User guide not found at '{Path}'.", path);
            _notifyIcon.ShowBalloonTip(5000, AppInfo.ProductName,
                $"{AppInfo.UserGuideFileName} is missing from the application folder.",
                ToolTipIcon.Warning);
            return;
        }

        TryStart(path);
    }

    private void OpenSettings()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "appsettings.json");
        TryStart(path);
    }

    private void OpenLogFolder()
    {
        var path = PushToTalkDictation.Diagnostics.FileLoggerProvider.ResolveLogDirectory(_settings.Logging.FilePath);
        Directory.CreateDirectory(path);
        TryStart(path);
    }

    private void TryStart(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not open '{Path}'.", path);
        }
    }

    private void ExitApplication()
    {
        _logger.LogInformation("Exit requested from the tray menu.");
        _notifyIcon.Visible = false;
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _controller.StateChanged -= OnStateChanged;
            _controller.Failed -= OnFailed;

            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _menu.Dispose();
            _icons.Dispose();
        }

        base.Dispose(disposing);
    }
}
