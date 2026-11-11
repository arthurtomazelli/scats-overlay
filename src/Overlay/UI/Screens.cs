using System.Drawing;
using System.Windows.Forms;
using Overlay.Config;

namespace Overlay.UI;

/// <summary>Monitor selection and anchoring, in physical pixels (the process is per-monitor DPI aware).</summary>
internal static class Screens
{
    /// <summary>Device names for "primary", "all", or "1,2" (1-based, in Windows' enumeration order).</summary>
    public static IReadOnlyList<string> Resolve(string? spec)
    {
        var all = Screen.AllScreens;
        spec = spec?.Trim().ToLowerInvariant() ?? "primary";
        if (spec == "all") return all.Select(s => s.DeviceName).ToList();

        var picked = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => int.TryParse(p, out int n) && n >= 1 && n <= all.Length ? all[n - 1].DeviceName : null)
            .OfType<string>()
            .Distinct()
            .ToList();
        return picked.Count > 0 ? picked : [Screen.PrimaryScreen!.DeviceName];
    }

    public static Screen ByName(string deviceName) =>
        Screen.AllScreens.FirstOrDefault(s => s.DeviceName == deviceName) ?? Screen.PrimaryScreen!;

    /// <summary>Top-left pixel for a w×h window anchored at <paramref name="pos"/> inside <paramref name="area"/>.</summary>
    public static (int X, int Y) Place(Rectangle area, int w, int h, Position pos, int dx, int dy)
    {
        int x = pos switch
        {
            Position.TopLeft or Position.CenterLeft or Position.BottomLeft => area.Left + dx,
            Position.TopRight or Position.CenterRight or Position.BottomRight => area.Right - w - dx,
            _ => area.Left + (area.Width - w) / 2 + dx,
        };
        int y = pos switch
        {
            Position.TopLeft or Position.TopCenter or Position.TopRight => area.Top + dy,
            Position.BottomLeft or Position.BottomCenter or Position.BottomRight => area.Bottom - h - dy,
            _ => area.Top + (area.Height - h) / 2 + dy,
        };
        return (x, y);
    }
}
