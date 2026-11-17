using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;

namespace AlwaysOnTop;

// Tray glyph: the Windows 11 icon font's own pin (Segoe Fluent Icons; Segoe MDL2 Assets on Windows 10), drawn in the
// taskbar foreground colour like the native tray icons – white on a dark taskbar, near-black on a light one.
// Nothing pinned: outline pin (E718). At least one window pinned: filled pin (E840 outline + E841 fill layer).
internal static class TrayIcons
{
    private const string Outline = "", PinnedOutline = "", PinnedFill = "";

    private static readonly string FontName = IsInstalled("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    private static bool IsInstalled(string family)
    {
        using var fonts = new InstalledFontCollection();
        return fonts.Families.Any(f => f.Name == family);
    }

    public static Bitmap Draw(bool pinned, Color foreground, int size)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        // The icon font is designed on a 16/20/24/32 px em; the glyph at em = icon size fills it like a native icon.
        using var font = new Font(FontName, size, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(foreground);
        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoClip,
        };
        var box = new RectangleF(0, 0, size, size);
        if (pinned)
        {
            g.DrawString(PinnedFill, font, brush, box, format);
            g.DrawString(PinnedOutline, font, brush, box, format);
        }
        else
        {
            g.DrawString(Outline, font, brush, box, format);
        }
        return bmp;
    }

    public static Icon Create(Bitmap bmp)
    {
        using var png = new MemoryStream();
        bmp.Save(png, ImageFormat.Png);
        var bytes = png.ToArray();
        var size = bmp.Width;

        // Wrap the PNG in a one-entry ICO container; Icon(Stream) understands PNG-compressed entries.
        using var ico = new MemoryStream();
        var w = new BinaryWriter(ico);
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)1);
        var dim = (byte)(size >= 256 ? 0 : size);
        w.Write(dim); w.Write(dim); w.Write((byte)0); w.Write((byte)0);
        w.Write((ushort)1); w.Write((ushort)32);
        w.Write((uint)bytes.Length); w.Write((uint)22);
        w.Write(bytes); w.Flush();
        ico.Position = 0;
        return new Icon(ico, size, size);
    }

    // Diagnostic contact sheet: both states on a dark and a light taskbar strip at 16/20/24/32 px, 8x and 1:1.
    public static void RenderPreview(string dir)
    {
        Directory.CreateDirectory(dir);
        var sizes = new[] { 16, 20, 24, 32 };
        const int scale = 8, cell = 32 * scale + 16;
        using var sheet = new Bitmap(2 * cell + 200, 2 * sizes.Length * cell, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sheet);
        g.InterpolationMode = InterpolationMode.NearestNeighbor;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        var row = 0;
        foreach (var dark in new[] { true, false })
        {
            var fore = dark ? Color.White : Color.FromArgb(27, 27, 27);
            foreach (var size in sizes)
            {
                var y = row * cell;
                using var back = new SolidBrush(dark ? Color.FromArgb(32, 32, 32) : Color.FromArgb(238, 238, 238));
                g.FillRectangle(back, 0, y, sheet.Width, cell);
                for (var i = 0; i < 2; i++)
                {
                    using var bmp = Draw(i == 1, fore, size);
                    g.DrawImage(bmp, new Rectangle(i * cell + 8, y + 8, size * scale, size * scale));
                    g.DrawImage(bmp, new Rectangle(2 * cell + 20 + i * 50, y + cell / 2 - size / 2, size, size));
                }
                using var f = new Font("Segoe UI", 10f);
                g.DrawString($"{size} px", f, dark ? Brushes.White : Brushes.Black, 2 * cell + 20, y + 4);
                row++;
            }
        }
        sheet.Save(Path.Combine(dir, "tray-preview.png"), ImageFormat.Png);
    }
}
