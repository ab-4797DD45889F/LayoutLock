using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

// Renders the "snap layout + padlock" icon (same geometry as icon.svg, 64x64 design grid) into a multi-size .ico.
// Usage: dotnet run --project tools/IconGenerator -- <output.ico> [png-preview-folder]

var output = args.Length > 0 ? args[0] : "app.ico";
var previewFolder = args.Length > 1 ? args[1] : null;
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 256];

var frames = sizes.Select(size => (size, bitmap: Render(size))).ToList();

if (previewFolder is not null)
{
    Directory.CreateDirectory(previewFolder);
    foreach (var (size, bitmap) in frames)
        bitmap.Save(Path.Combine(previewFolder, $"icon-{size}.png"), ImageFormat.Png);
}

WriteIco(output, frames);
Console.WriteLine($"Wrote {output} ({string.Join(", ", sizes)} px)");

static Bitmap Render(int size)
{
    var bitmap = new Bitmap(size, size, PixelFormat.Format32bppArgb);
    using var g = Graphics.FromImage(bitmap);
    g.SmoothingMode = SmoothingMode.AntiAlias;
    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
    g.Clear(Color.Transparent);
    g.ScaleTransform(size / 64f, size / 64f);

    var navy = ColorTranslator.FromHtml("#0B1B3A");
    var amber = ColorTranslator.FromHtml("#F5B400");

    FillRounded(g, "#23304A", 3, 5, 50, 42, 5);
    FillRounded(g, "#5AA2FF", 7, 9, 20, 34, 2);
    FillRounded(g, "#A98BFF", 30, 9, 19, 16, 2);
    FillRounded(g, "#3DD39A", 30, 27, 19, 16, 2);

    using (var shackle = new GraphicsPath())
    {
        shackle.AddLine(43, 40, 43, 35);
        shackle.AddArc(43, 29, 12, 12, 180, 180);
        shackle.AddLine(55, 35, 55, 40);
        using var outline = new Pen(navy, 7.5f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        using var fill = new Pen(amber, 3.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        g.DrawPath(outline, shackle);
        g.DrawPath(fill, shackle);
    }

    using (var body = RoundedRect(37, 39, 24, 20, 4))
    {
        using var brush = new SolidBrush(amber);
        using var pen = new Pen(navy, 2f);
        g.FillPath(brush, body);
        g.DrawPath(pen, body);
    }

    using (var keyhole = new SolidBrush(navy))
    {
        g.FillEllipse(keyhole, 49 - 2.6f, 47 - 2.6f, 5.2f, 5.2f);
        using var stem = RoundedRect(48, 48, 2, 6, 1);
        g.FillPath(keyhole, stem);
    }

    return bitmap;
}

static void FillRounded(Graphics g, string color, float x, float y, float w, float h, float r)
{
    using var path = RoundedRect(x, y, w, h, r);
    using var brush = new SolidBrush(ColorTranslator.FromHtml(color));
    g.FillPath(brush, path);
}

static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
{
    var d = r * 2;
    var path = new GraphicsPath();
    path.AddArc(x, y, d, d, 180, 90);
    path.AddArc(x + w - d, y, d, d, 270, 90);
    path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
    path.AddArc(x, y + h - d, d, d, 90, 90);
    path.CloseFigure();
    return path;
}

// 256 px is stored as PNG; smaller frames as classic 32-bit DIBs, which every icon loader (including System.Drawing.Icon) reads.
static void WriteIco(string path, List<(int size, Bitmap bitmap)> frames)
{
    var images = frames.Select(f => f.size >= 256 ? ToPng(f.bitmap) : ToDib(f.bitmap)).ToList();

    using var stream = File.Create(path);
    using var writer = new BinaryWriter(stream);
    writer.Write((short)0);
    writer.Write((short)1);
    writer.Write((short)frames.Count);

    var offset = 6 + 16 * frames.Count;
    for (var i = 0; i < frames.Count; i++)
    {
        var size = frames[i].size;
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)(size >= 256 ? 0 : size));
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((short)1);
        writer.Write((short)32);
        writer.Write(images[i].Length);
        writer.Write(offset);
        offset += images[i].Length;
    }

    foreach (var image in images)
        writer.Write(image);
}

static byte[] ToPng(Bitmap bitmap)
{
    using var ms = new MemoryStream();
    bitmap.Save(ms, ImageFormat.Png);
    return ms.ToArray();
}

static byte[] ToDib(Bitmap bitmap)
{
    var size = bitmap.Width;
    var maskStride = ((size + 31) / 32) * 4;
    using var ms = new MemoryStream();
    using var writer = new BinaryWriter(ms);

    writer.Write(40);
    writer.Write(size);
    writer.Write(size * 2); // XOR image + AND mask
    writer.Write((short)1);
    writer.Write((short)32);
    writer.Write(0);
    writer.Write(size * size * 4 + maskStride * size);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);
    writer.Write(0);

    for (var y = size - 1; y >= 0; y--)
        for (var x = 0; x < size; x++)
        {
            var c = bitmap.GetPixel(x, y);
            writer.Write(c.B);
            writer.Write(c.G);
            writer.Write(c.R);
            writer.Write(c.A);
        }

    for (var y = size - 1; y >= 0; y--)
    {
        var row = new byte[maskStride];
        for (var x = 0; x < size; x++)
            if (bitmap.GetPixel(x, y).A == 0)
                row[x / 8] |= (byte)(0x80 >> (x % 8));
        writer.Write(row);
    }

    return ms.ToArray();
}
