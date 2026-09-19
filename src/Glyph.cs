using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace BrightBridge
{
    /// <summary>
    /// The one definition of the BrightBridge mark. The tray icon and the exe's
    /// shell icon are both drawn from here, so they cannot drift apart.
    /// Proportions are fractions of the icon box, so the same drawing serves a
    /// 16 px tray slot and a 256 px shell icon.
    ///
    /// A core disc with a ring of dots, graded along a 45 degree diagonal:
    /// bright on the upper-left, falling to 25% grey on the lower-right, then
    /// feathered. It is meant to sit quietly in the tray, not announce itself.
    /// </summary>
    static class Glyph
    {
        const float CoreRadius = 0.170f;
        const float DotRing    = 0.355f;   // centre of the icon to centre of a dot
        const float DotRadius  = 0.062f;
        const float TileInset  = 0.160f;   // margin when drawn on a backing tile
        const float BlurFactor = 0.026f;   // feather radius, as a fraction of size
        const int   Supersample = 4;

        public static readonly Color Bright   = Color.FromArgb(255, 255, 255);
        public static readonly Color Dim      = Color.FromArgb(64, 64, 64);     // 25% grey
        public static readonly Color OffBright = Color.FromArgb(130, 130, 130);
        public static readonly Color OffDim    = Color.FromArgb(44, 44, 44);
        public static readonly Color Tile      = Color.FromArgb(28, 28, 30);    // the OSD panel charcoal

        /// <summary>Renders the mark, feathered, optionally on a backing tile.</summary>
        public static Bitmap RenderMark(int size, Color top, Color bottom, bool withTile)
        {
            int super = size * Supersample;
            Bitmap mark = new Bitmap(size, size, PixelFormat.Format32bppArgb);

            using (var big = new Bitmap(super, super, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(big))
                {
                    g.Clear(Color.Transparent);
                    float inset = withTile ? super * TileInset : 0f;
                    g.TranslateTransform(inset, inset);
                    DrawMark(g, super - inset * 2f, top, bottom);
                    g.ResetTransform();
                }
                using (var g = Graphics.FromImage(mark))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(big, new Rectangle(0, 0, size, size));
                }
            }

            Feather(mark, (int)Math.Max(1, Math.Round(size * BlurFactor)));
            if (!withTile) return mark;

            // The tile stays crisp; only the mark on it is softened.
            var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.Clear(Color.Transparent);
                DrawTile(g, size, Tile);
                g.DrawImage(mark, 0, 0);
            }
            mark.Dispose();
            return result;
        }

        public static void DrawMark(Graphics g, float size, Color top, Color bottom)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float c = size / 2f;
            float core = size * CoreRadius;
            float dot = size * DotRadius;

            using (var brush = MarkBrush(size, top, bottom))
            {
                g.FillEllipse(brush, c - core, c - core, core * 2f, core * 2f);
                for (int i = 0; i < 8; i++)
                {
                    double a = i * Math.PI / 4;
                    float x = c + (float)(Math.Cos(a) * size * DotRing);
                    float y = c + (float)(Math.Sin(a) * size * DotRing);
                    g.FillEllipse(brush, x - dot, y - dot, dot * 2f, dot * 2f);
                }
            }
        }

        /// <summary>
        /// The 45 degree split, as a gradient rather than a cut. Clipping the two
        /// halves leaves a hard seam straight across the brightest part of the
        /// mark, which is the first thing the eye lands on. A graded falloff
        /// reads as shading instead, and stays quiet. One brush spans the whole
        /// icon, so the dots are lit consistently with the core.
        /// </summary>
        static Brush MarkBrush(float size, Color top, Color bottom)
        {
            var rect = new RectangleF(-size * 0.05f, -size * 0.05f, size * 1.1f, size * 1.1f);
            var brush = new LinearGradientBrush(rect, top, bottom, 135f);
            var blend = new ColorBlend(4);
            blend.Colors = new[] { top, top, bottom, bottom };
            blend.Positions = new[] { 0f, 0.34f, 0.70f, 1f };
            brush.InterpolationColors = blend;
            brush.WrapMode = WrapMode.TileFlipXY;
            return brush;
        }

        /// <summary>
        /// Rounded backing tile, in the same charcoal as the OSD panel. The tray
        /// mark is bare because the taskbar is always dark; a file icon has to
        /// survive Explorer's white background too, so it gets the tile.
        /// </summary>
        public static void DrawTile(Graphics g, float size, Color fill)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            float r = size * 0.18f;
            var rect = new RectangleF(0, 0, size, size);
            using (var path = new GraphicsPath())
            using (var brush = new SolidBrush(fill))
            {
                float d = r * 2f;
                path.AddArc(rect.X, rect.Y, d, d, 180, 90);
                path.AddArc(rect.Right - d, rect.Y, d, d, 270, 90);
                path.AddArc(rect.Right - d, rect.Bottom - d, d, d, 0, 90);
                path.AddArc(rect.X, rect.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                g.FillPath(brush, path);
            }
        }

        // Box blur, twice, on premultiplied alpha. Premultiplying matters: blurring
        // colour and alpha independently leaves a dark fringe around the edges.
        static void Feather(Bitmap bmp, int radius)
        {
            if (radius < 1) return;
            int w = bmp.Width, h = bmp.Height;
            var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
            try
            {
                int stride = data.Stride;
                var buf = new byte[stride * h];
                Marshal.Copy(data.Scan0, buf, 0, buf.Length);

                for (int i = 0; i < buf.Length; i += 4)
                {
                    int a = buf[i + 3];
                    buf[i] = (byte)(buf[i] * a / 255);
                    buf[i + 1] = (byte)(buf[i + 1] * a / 255);
                    buf[i + 2] = (byte)(buf[i + 2] * a / 255);
                }

                var tmp = new byte[buf.Length];
                BoxPass(buf, tmp, w, h, stride, radius, true);
                BoxPass(tmp, buf, w, h, stride, radius, false);

                for (int i = 0; i < buf.Length; i += 4)
                {
                    int a = buf[i + 3];
                    if (a == 0) { buf[i] = buf[i + 1] = buf[i + 2] = 0; continue; }
                    buf[i] = (byte)Math.Min(255, buf[i] * 255 / a);
                    buf[i + 1] = (byte)Math.Min(255, buf[i + 1] * 255 / a);
                    buf[i + 2] = (byte)Math.Min(255, buf[i + 2] * 255 / a);
                }

                Marshal.Copy(buf, 0, data.Scan0, buf.Length);
            }
            finally { bmp.UnlockBits(data); }
        }

        static void BoxPass(byte[] src, byte[] dst, int w, int h, int stride, int r, bool horizontal)
        {
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int b = 0, g = 0, rr = 0, a = 0, n = 0;
                    for (int k = -r; k <= r; k++)
                    {
                        int sx = horizontal ? x + k : x;
                        int sy = horizontal ? y : y + k;
                        if (sx < 0 || sx >= w || sy < 0 || sy >= h) continue;
                        int o = sy * stride + sx * 4;
                        b += src[o]; g += src[o + 1]; rr += src[o + 2]; a += src[o + 3];
                        n++;
                    }
                    int d = y * stride + x * 4;
                    dst[d] = (byte)(b / n);
                    dst[d + 1] = (byte)(g / n);
                    dst[d + 2] = (byte)(rr / n);
                    dst[d + 3] = (byte)(a / n);
                }
            }
        }
    }
}
