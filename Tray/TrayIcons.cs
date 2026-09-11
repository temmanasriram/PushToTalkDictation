using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace PushToTalkDictation.Tray;

/// <summary>
/// Generates the tray icons at runtime so the project ships without binary assets.
/// Swap <see cref="Build"/> for <c>new Icon("app.ico")</c> if you have real artwork.
/// </summary>
public sealed class TrayIcons : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    private readonly List<IntPtr> _handles = [];

    public Icon Inactive { get; }
    public Icon Idle { get; }
    public Icon Recording { get; }
    public Icon Transcribing { get; }

    public TrayIcons()
    {
        Inactive = Build(Color.FromArgb(120, 120, 125), filled: false);
        Idle = Build(Color.FromArgb(64, 158, 255), filled: false);
        Recording = Build(Color.FromArgb(232, 66, 66), filled: true);
        Transcribing = Build(Color.FromArgb(245, 176, 65), filled: true);
    }

    public Icon For(DictationState state) => state switch
    {
        DictationState.Recording => Recording,
        DictationState.Transcribing => Transcribing,
        DictationState.Idle => Idle,
        _ => Inactive
    };

    private Icon Build(Color color, bool filled)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            // Microphone capsule.
            var capsule = new Rectangle(11, 5, 10, 15);
            using var brush = new SolidBrush(color);
            using var pen = new Pen(color, 2.4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };

            using var capsulePath = RoundedRect(capsule, 5);
            if (filled)
                g.FillPath(brush, capsulePath);
            else
                g.DrawPath(pen, capsulePath);

            // Cradle + stem.
            g.DrawArc(pen, new Rectangle(7, 12, 18, 14), 20, 140);
            g.DrawLine(pen, 16, 25, 16, 28);
        }

        var handle = bitmap.GetHicon();
        _handles.Add(handle);
        return Icon.FromHandle(handle);
    }

    private static GraphicsPath RoundedRect(Rectangle r, int radius)
    {
        var d = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public void Dispose()
    {
        Inactive.Dispose();
        Idle.Dispose();
        Recording.Dispose();
        Transcribing.Dispose();

        foreach (var handle in _handles)
            DestroyIcon(handle);

        _handles.Clear();
    }
}
