using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace Overlay.UI;

/// <summary>
/// The widget is click-through, so the tray icon is the only way to interact with it.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _image;

    public TrayIcon(Action openSettings, Action openConfig, Action reload, Action dumpSensors, Action<bool> previewGameOverlay, Action exit)
    {
        _image = DrawIcon();
        var preview = new ToolStripMenuItem("Preview game overlay") { CheckOnClick = true };
        preview.CheckedChanged += (_, _) => previewGameOverlay(preview.Checked);

        var menu = new ContextMenuStrip();
        var settings = new ToolStripMenuItem("Settings...", null, (_, _) => openSettings());
        settings.Font = new Font(settings.Font, FontStyle.Bold); // the double-click action
        menu.Items.Add(settings);
        menu.Items.Add("Edit config.json", null, (_, _) => openConfig());
        menu.Items.Add("Reload config", null, (_, _) => reload());
        menu.Items.Add("Dump sensors to file", null, (_, _) => dumpSensors());
        menu.Items.Add(preview);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => exit());

        _icon = new NotifyIcon { Icon = _image, Text = "Hardware widget", ContextMenuStrip = menu, Visible = true };
        _icon.DoubleClick += (_, _) => openSettings();
    }

    public void ShowMessage(string text) => _icon.ShowBalloonTip(3000, "Hardware widget", text, ToolTipIcon.None);

    /// <summary>A thin ring with a short arc: no image assets needed.</summary>
    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using var ring = new Pen(Color.FromArgb(150, 255, 255, 255), 2.5f);
            using var arc = new Pen(Color.FromArgb(255, 255, 122, 89), 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawEllipse(ring, 5, 5, 22, 22);
            g.DrawArc(arc, 5, 5, 22, 22, -90, 110);
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _image.Dispose();
    }
}
