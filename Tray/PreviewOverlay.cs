using System.Runtime.InteropServices;

namespace PushToTalkDictation.Tray;

/// <summary>
/// A small caption bar showing what is being recognised while the hotkey is held.
///
/// The window must be completely inert. It is created with WS_EX_NOACTIVATE and overrides
/// <see cref="ShowWithoutActivation"/> so it never takes focus - if it did, it would become
/// the foreground window and the transcript would be typed into it instead of the user's
/// document. WS_EX_TRANSPARENT lets the mouse through, so it cannot swallow a click even
/// while sitting over the user's work, and WS_EX_TOOLWINDOW keeps it out of Alt+Tab.
///
/// Everything here is UI-thread only, like the rest of <c>Tray</c>.
/// </summary>
public sealed class PreviewOverlay : Form
{
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private const int MarginFromBottom = 96;

    /// <summary>
    /// Hard cap on the caption width. Two thirds of the screen alone is not enough on a
    /// wide monitor - at 5120 px that is a 3400 px single line, which is unreadable. Capping
    /// it makes long previews wrap into a few short lines instead.
    /// </summary>
    private const int MaxWidth = 1200;

    private readonly Label _text;

    public PreviewOverlay()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Color.FromArgb(24, 24, 28);
        Opacity = 0.88;
        Padding = new Padding(16, 10, 16, 10);
        MinimumSize = new Size(240, 0);

        _text = new Label
        {
            AutoSize = true,
            MaximumSize = new Size(Math.Min(ScreenWidth() - 80, MaxWidth), 0),
            ForeColor = Color.FromArgb(238, 238, 234),
            Font = new Font(SystemFonts.MessageBoxFont?.FontFamily ?? FontFamily.GenericSansSerif,
                            13f, FontStyle.Regular),
            Dock = DockStyle.Fill,
            TextAlign = ContentAlignment.MiddleLeft
        };

        Controls.Add(_text);
        _text.SizeChanged += (_, _) => Reposition();
    }

    /// <summary>Stops the window becoming active when it is shown.</summary>
    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_TOPMOST;
            return cp;
        }
    }

    /// <summary>Shows the given text, sizing and repositioning to fit. Empty text hides it.</summary>
    public void Update(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            HideOverlay();
            return;
        }

        _text.Text = text;

        if (!Visible)
        {
            Reposition();
            // Explicitly not Show(): the base Show respects ShowWithoutActivation, but being
            // deliberate here documents why nothing about this window may take focus.
            Show();
        }
        else
        {
            Reposition();
        }
    }

    public void HideOverlay()
    {
        _text.Text = string.Empty;
        if (Visible) Hide();
    }

    private void Reposition()
    {
        var screen = Screen.PrimaryScreen?.WorkingArea
                     ?? new Rectangle(0, 0, ScreenWidth(), 800);

        var width = Math.Min(_text.PreferredWidth + Padding.Horizontal, screen.Width - 40);
        var height = _text.PreferredHeight + Padding.Vertical;

        Size = new Size(Math.Max(MinimumSize.Width, width), height);
        Location = new Point(
            screen.Left + (screen.Width - Width) / 2,
            screen.Bottom - Height - MarginFromBottom);
    }

    private static int ScreenWidth() => Screen.PrimaryScreen?.WorkingArea.Width ?? 1280;

    // Rounded corners where the OS supports them. Purely cosmetic; ignored pre-Windows 11.
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        try
        {
            var preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(Handle, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */,
                ref preference, sizeof(int));
        }
        catch (Exception)
        {
            // Older Windows: no rounded corners, nothing else to do.
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute,
        ref int value, int size);
}
