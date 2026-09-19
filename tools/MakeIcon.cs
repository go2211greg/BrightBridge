using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace BrightBridge
{
    /// <summary>
    /// Writes BrightBridge.ico from the same Glyph drawing the tray icon uses.
    /// Build-time only - see tools\make-icon.cmd.
    /// </summary>
    static class MakeIcon
    {
        // What the Windows shell actually asks for across DPI settings and views.
        static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

        static int Main(string[] args)
        {
            string outPath = args.Length > 0 ? args[0] : "BrightBridge.ico";
            string preview = args.Length > 1 ? args[1] : null;

            var images = new List<Bitmap>();
            foreach (int s in Sizes) images.Add(Render(s));

            WriteIco(outPath, images);
            Console.WriteLine("wrote " + outPath + "  (" + images.Count + " sizes: " + string.Join(", ", Array.ConvertAll(Sizes, x => x.ToString())) + ")");

            if (preview != null) { WritePreview(preview, images); Console.WriteLine("wrote " + preview); }

            foreach (var b in images) b.Dispose();
            return 0;
        }

        static Bitmap Render(int size)
        {
            return Glyph.RenderMark(size, Glyph.Bright, Glyph.Dim, true);
        }

        static void WriteIco(string path, List<Bitmap> images)
        {
            var payloads = new List<byte[]>();
            foreach (var b in images)
                payloads.Add(b.Width >= 128 ? PngBytes(b) : DibBytes(b));

            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            using (var bw = new BinaryWriter(fs))
            {
                bw.Write((short)0);                  // reserved
                bw.Write((short)1);                  // type: icon
                bw.Write((short)images.Count);

                int offset = 6 + 16 * images.Count;
                for (int i = 0; i < images.Count; i++)
                {
                    var b = images[i];
                    bw.Write((byte)(b.Width >= 256 ? 0 : b.Width));   // 0 means 256
                    bw.Write((byte)(b.Height >= 256 ? 0 : b.Height));
                    bw.Write((byte)0);               // palette size
                    bw.Write((byte)0);               // reserved
                    bw.Write((short)1);              // colour planes
                    bw.Write((short)32);             // bits per pixel
                    bw.Write(payloads[i].Length);
                    bw.Write(offset);
                    offset += payloads[i].Length;
                }
                foreach (var p in payloads) bw.Write(p);
            }
        }

        static byte[] PngBytes(Bitmap bmp)
        {
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }

        // 32bpp BGRA bottom-up DIB, with the AND mask left empty because the
        // alpha channel already carries transparency.
        static byte[] DibBytes(Bitmap bmp)
        {
            int w = bmp.Width, h = bmp.Height;
            int xorSize = w * h * 4;
            int maskStride = ((w + 31) / 32) * 4;
            int maskSize = maskStride * h;

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(40);                // biSize
                bw.Write(w);                 // biWidth
                bw.Write(h * 2);             // biHeight: XOR image plus AND mask
                bw.Write((short)1);          // biPlanes
                bw.Write((short)32);         // biBitCount
                bw.Write(0);                 // biCompression: BI_RGB
                bw.Write(xorSize + maskSize);
                bw.Write(0); bw.Write(0);    // pixels per metre
                bw.Write(0); bw.Write(0);    // palette

                for (int y = h - 1; y >= 0; y--)
                    for (int x = 0; x < w; x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A);
                    }
                bw.Write(new byte[maskSize]);

                bw.Flush();
                return ms.ToArray();
            }
        }

        static void WritePreview(string path, List<Bitmap> images)
        {
            int pad = 16, x = pad;
            int width = pad, tallest = 0;
            foreach (var b in images) { width += b.Width + pad; if (b.Height > tallest) tallest = b.Height; }
            int rowTop = pad, rowBottom = pad * 2 + tallest;
            int height = tallest * 2 + pad * 3;

            using (var sheet = new Bitmap(width, height))
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(245, 245, 247));                    // Explorer light
                g.FillRectangle(Brushes.Black, 0, 0, width, rowBottom - pad / 2);
                foreach (var b in images)
                {
                    g.DrawImage(b, x, rowTop);       // against dark
                    g.DrawImage(b, x, rowBottom);    // against light
                    x += b.Width + pad;
                }
                sheet.Save(path, ImageFormat.Png);
            }
        }
    }
}
