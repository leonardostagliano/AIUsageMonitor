using System.Drawing;
using System.Drawing.Imaging;
using AIUsageMonitor.App.Tray;

var output = args.Length > 0 ? args[0] : Path.Combine("src", "AIUsageMonitor.App", "Assets", "app.ico");
int[] sizes = [16, 24, 32, 48, 64, 128, 256];
var frames = new List<byte[]>();
foreach (var size in sizes)
{
    using var rendered = TrayIconRenderer.Render(null, size);
    using var bitmap = rendered.Icon.ToBitmap();
    using var ms = new MemoryStream();
    bitmap.Save(ms, ImageFormat.Png);
    frames.Add(ms.ToArray());
}

using var file = new FileStream(output, FileMode.Create, FileAccess.Write);
using var w = new BinaryWriter(file);
w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
var offset = 6 + 16 * sizes.Length;
for (var i = 0; i < sizes.Length; i++)
{
    w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
    w.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
    w.Write((byte)0); w.Write((byte)0);
    w.Write((ushort)1); w.Write((ushort)32);
    w.Write(frames[i].Length); w.Write(offset);
    offset += frames[i].Length;
}
foreach (var frame in frames) w.Write(frame);
Console.WriteLine($"wrote {output} ({sizes.Length} sizes)");
