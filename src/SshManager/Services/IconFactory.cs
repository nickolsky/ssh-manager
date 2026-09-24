using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SshManager.Services;

/// <summary>
/// The app icon: a blue rounded tile with a white terminal prompt and a green status dot (grey when locked).
/// The same drawing produces Assets\app.ico (see <see cref="WriteIco"/>) and the runtime tray / window icons.
/// </summary>
public static partial class IconFactory
{
    public static readonly int[] IcoSizes = [16, 20, 24, 32, 40, 48, 64, 96, 128, 256];

    public static Bitmap Render(int size, bool unlocked)
    {
        var bmp = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(System.Drawing.Color.Transparent);
        float s = size;

        // tile
        var inset = s <= 20 ? 0.5f : s * 0.04f;
        var tile = new RectangleF(inset, inset, s - 2 * inset, s - 2 * inset);
        using (var path = RoundedRect(tile, s * 0.23f))
        {
            var (from, to) = unlocked
                ? (System.Drawing.Color.FromArgb(60, 146, 255), System.Drawing.Color.FromArgb(24, 72, 214))
                : (System.Drawing.Color.FromArgb(150, 155, 164), System.Drawing.Color.FromArgb(88, 94, 104));
            using var fill = new System.Drawing.Drawing2D.LinearGradientBrush(new PointF(0, 0), new PointF(s, s), from, to);
            g.FillPath(fill, path);

            if (size >= 32)
            {
                // soft glass highlight on the upper half
                using var clip = new Region(path);
                g.SetClip(clip, CombineMode.Replace);
                using var gloss = new System.Drawing.Drawing2D.LinearGradientBrush(new PointF(0, 0), new PointF(0, s * 0.55f),
                    System.Drawing.Color.FromArgb(60, 255, 255, 255), System.Drawing.Color.FromArgb(0, 255, 255, 255));
                g.FillRectangle(gloss, 0, 0, s, s * 0.55f);
                g.ResetClip();
            }
        }

        // prompt ">_"
        var stroke = Math.Max(1.6f, s * 0.095f);
        using (var pen = new System.Drawing.Pen(System.Drawing.Color.White, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
        {
            g.DrawLines(pen, [P(s, 0.25f, 0.36f), P(s, 0.45f, 0.54f), P(s, 0.25f, 0.72f)]);
            g.DrawLine(pen, P(s, 0.54f, 0.72f), P(s, 0.76f, 0.72f));
        }

        // status dot (top right)
        if (size >= 20)
        {
            var r = s * 0.12f;
            var c = P(s, 0.73f, 0.29f);
            using var ring = new SolidBrush(unlocked ? System.Drawing.Color.FromArgb(24, 72, 214) : System.Drawing.Color.FromArgb(88, 94, 104));
            var rr = r + Math.Max(1f, s * 0.035f);
            g.FillEllipse(ring, c.X - rr, c.Y - rr, rr * 2, rr * 2);
            using var dot = new SolidBrush(unlocked ? System.Drawing.Color.FromArgb(52, 211, 153) : System.Drawing.Color.FromArgb(200, 204, 210));
            g.FillEllipse(dot, c.X - r, c.Y - r, r * 2, r * 2);
        }
        return bmp;
    }

    private static PointF P(float s, float x, float y) => new(s * x, s * y);

    public static Icon Create(bool unlocked, int size = 32)
    {
        using var bmp = Render(size, unlocked);
        var h = bmp.GetHicon();
        var icon = (Icon)Icon.FromHandle(h).Clone();
        DestroyIcon(h);
        return icon;
    }

    /// <summary>Window icon: the multi-size app.ico when unlocked (crisp in the taskbar), drawn grey when locked.</summary>
    public static ImageSource CreateImage(bool unlocked)
    {
        if (unlocked)
        {
            try
            {
                var frame = BitmapFrame.Create(new Uri("pack://application:,,,/Assets/app.ico"));
                frame.Freeze();
                return frame;
            }
            catch (IOException)
            {
            }
        }
        using var icon = Create(unlocked, 64);
        var src = Imaging.CreateBitmapSourceFromHIcon(icon.Handle, System.Windows.Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
        src.Freeze();
        return src;
    }

    /// <summary>Writes a multi-resolution .ico: 32-bit DIB entries, PNG for 256 px.</summary>
    public static void WriteIco(string path, bool unlocked = true)
    {
        var images = IcoSizes.Select(size =>
        {
            using var bmp = Render(size, unlocked);
            return size >= 256 ? Png(bmp) : Dib(bmp);
        }).ToList();

        using var fs = File.Create(path);
        using var w = new BinaryWriter(fs);
        w.Write((short)0);
        w.Write((short)1);
        w.Write((short)images.Count);
        var offset = 6 + 16 * images.Count;
        for (int i = 0; i < images.Count; i++)
        {
            var size = IcoSizes[i];
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((short)1);
            w.Write((short)32);
            w.Write(images[i].Length);
            w.Write(offset);
            offset += images[i].Length;
        }
        foreach (var img in images) w.Write(img);
    }

    private static byte[] Png(Bitmap bmp)
    {
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static byte[] Dib(Bitmap bmp)
    {
        int w = bmp.Width, h = bmp.Height;
        var maskStride = (w + 31) / 32 * 4;
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        bw.Write(40);
        bw.Write(w);
        bw.Write(h * 2);
        bw.Write((short)1);
        bw.Write((short)32);
        bw.Write(0);
        bw.Write(w * h * 4 + maskStride * h);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        bw.Write(0);
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        try
        {
            var row = new byte[w * 4];
            for (int y = h - 1; y >= 0; y--) // bottom-up
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                bw.Write(row);
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        bw.Write(new byte[maskStride * h]); // alpha channel does the masking
        return ms.ToArray();
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
