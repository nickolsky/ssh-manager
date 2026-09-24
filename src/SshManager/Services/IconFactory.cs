using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SshManager.Services;

/// <summary>Draws the app icon at runtime (blue = unlocked, grey = locked), so no binary assets are needed.</summary>
internal static partial class IconFactory
{
    public static Icon Create(bool unlocked, int size = 32)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(System.Drawing.Color.Transparent);
            var color = unlocked ? System.Drawing.Color.FromArgb(0, 120, 212) : System.Drawing.Color.FromArgb(110, 110, 110);
            using var path = RoundedRect(new RectangleF(1, 1, size - 2, size - 2), size / 5f);
            using var fill = new SolidBrush(color);
            g.FillPath(fill, path);
            using var font = new Font("Consolas", size * 0.42f, System.Drawing.FontStyle.Bold, GraphicsUnit.Pixel);
            using var fmt = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(">_", font, System.Drawing.Brushes.White, new RectangleF(0, 0, size, size), fmt);
        }
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    public static ImageSource CreateImage(bool unlocked)
    {
        using var icon = Create(unlocked, 64);
        var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr hIcon);
}
