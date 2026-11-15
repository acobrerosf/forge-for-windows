using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Forge.Core.Model;

namespace Forge.App.Tray;

/// <summary>
/// The tray mark plus a badge for the worst state across every watched
/// organization: none, hollow (maintenance), pulsing (deploy running),
/// amber (token or request problem), red (server unreachable or deploy failed).
/// Same mark as tools/make-icon.ps1.
/// </summary>
internal static partial class IconFactory
{
    private static readonly Color Slate = Color.FromArgb(255, 36, 52, 71);
    private static readonly Dictionary<(Badge, bool), Icon> Cache = new();

    public static Icon Get(Badge badge, bool lit)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue((badge, lit), out var icon)) Cache[(badge, lit)] = icon = Draw(badge, lit);
            return icon;
        }
    }

    private static Icon Draw(Badge badge, bool lit)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using (var path = RoundedRect(new RectangleF(1, 1, 30, 30), 7))
            using (var bg = new SolidBrush(Slate))
                g.FillPath(bg, path);

            using (var white = new SolidBrush(Color.White))
            {
                g.FillRectangle(white, 9, 7, 5, 18);
                g.FillRectangle(white, 9, 7, 14, 5);
                g.FillRectangle(white, 9, 14, 11, 4);
            }

            if (badge != Badge.None)
            {
                var outer = new RectangleF(17, 17, 15, 15);
                var inner = new RectangleF(19.5f, 19.5f, 10, 10);
                // A slate collar keeps the badge legible against the white glyph and any taskbar colour.
                using (var collar = new SolidBrush(Slate)) g.FillEllipse(collar, outer);
                var color = badge switch
                {
                    Badge.Bad => Color.FromArgb(255, 239, 68, 68),
                    Badge.Warn => Color.FromArgb(255, 245, 158, 11),
                    Badge.Busy => lit ? Color.FromArgb(255, 96, 165, 250) : Color.FromArgb(255, 37, 99, 235),
                    _ => Color.White,
                };
                if (badge == Badge.Maintenance)
                {
                    using var pen = new Pen(color, 2.4f);
                    g.DrawEllipse(pen, new RectangleF(20.5f, 20.5f, 8, 8));
                }
                else
                {
                    using var fill = new SolidBrush(color);
                    g.FillEllipse(fill, inner);
                }
            }
        }

        var handle = bmp.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(handle);
            return (Icon)temp.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
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

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr handle);
}
