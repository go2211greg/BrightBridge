using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BrightBridge
{
    /// <summary>
    /// A small readout that shows the value we actually sent to the monitor.
    /// Deliberately bottom-centre so it does not sit on top of Windows' own
    /// (non-functional) brightness flyout in the top-left corner.
    /// </summary>
    class Osd : Form
    {
        readonly Timer hideTimer = new Timer();
        string caption = "Brightness";
        int percent;

        public Osd()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            TopMost = true;
            StartPosition = FormStartPosition.Manual;
            Size = new Size(320, 86);
            BackColor = Color.FromArgb(24, 24, 24);
            DoubleBuffered = true;
            Opacity = 0.94;
            hideTimer.Interval = 1500;
            hideTimer.Tick += delegate { hideTimer.Stop(); Hide(); };
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= 0x08000000; // WS_EX_NOACTIVATE
                cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
                cp.ExStyle |= 0x00000008; // WS_EX_TOPMOST
                return cp;
            }
        }

        public void Show(string label, int value)
        {
            caption = label;
            percent = value < 0 ? 0 : (value > 100 ? 100 : value);

            var wa = Screen.PrimaryScreen.WorkingArea;
            Location = new Point(wa.Left + (wa.Width - Width) / 2, wa.Bottom - Height - 80);

            Invalidate();
            if (!Visible) Show();
            hideTimer.Stop();
            hideTimer.Start();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

            var r = new Rectangle(0, 0, Width - 1, Height - 1);
            using (var path = Rounded(r, 10))
            using (var bg = new SolidBrush(Color.FromArgb(28, 28, 30)))
            using (var edge = new Pen(Color.FromArgb(70, 70, 74)))
            {
                g.FillPath(bg, path);
                g.DrawPath(edge, path);
            }

            using (var f = new Font("Segoe UI", 10f, FontStyle.Regular))
            using (var fb = new Font("Segoe UI", 10f, FontStyle.Bold))
            using (var fg = new SolidBrush(Color.FromArgb(235, 235, 235)))
            using (var dim = new SolidBrush(Color.FromArgb(150, 150, 155)))
            {
                g.DrawString(caption, f, dim, 20, 16);
                string v = percent + "%";
                var sz = g.MeasureString(v, fb);
                g.DrawString(v, fb, fg, Width - 20 - sz.Width, 15);
            }

            var bar = new Rectangle(20, 50, Width - 40, 8);
            using (var track = new SolidBrush(Color.FromArgb(58, 58, 62)))
            using (var trackPath = Rounded(bar, 4))
                g.FillPath(track, trackPath);

            int w = (int)Math.Round(bar.Width * percent / 100.0);
            if (w > 0)
            {
                if (w < 8) w = 8;
                var fill = new Rectangle(bar.X, bar.Y, w, bar.Height);
                using (var b = new SolidBrush(Color.FromArgb(0, 160, 230)))
                using (var fillPath = Rounded(fill, 4))
                    g.FillPath(b, fillPath);
            }
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }
    }
}
