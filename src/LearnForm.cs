using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace BrightBridge
{
    /// <summary>
    /// Binds whatever a key actually emits. The keyboard may report a standard
    /// consumer usage, a vendor bitmap bit, a virtual key, or several at once -
    /// every signature seen is listed so the most specific one can be chosen.
    /// </summary>
    class LearnForm : Form
    {
        readonly RawInputListener listener;
        readonly ComboBox actionBox = new ComboBox();
        readonly ListBox candidates = new ListBox();
        readonly Label status = new Label();
        readonly Button capture = new Button();
        readonly Button save = new Button();
        readonly Timer settle = new Timer();
        readonly List<string> seen = new List<string>();
        readonly Dictionary<string, string> devices = new Dictionary<string, string>();
        DateTime captureStart;
        bool capturing;

        public Binding Result;

        static readonly string[] Actions = {
            "brightness:+step", "brightness:-step",
            "contrast:+step",   "contrast:-step",
            "volume:+step",     "volume:-step",
            "brightness:=100",  "brightness:=0",
        };

        public LearnForm(RawInputListener listener)
        {
            this.listener = listener;

            Text = "BrightBridge - learn a key";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(440, 330);
            Font = new Font("Segoe UI", 9f);

            var l1 = new Label();
            l1.Text = "1.  What should the key do?";
            l1.SetBounds(14, 14, 400, 20);

            actionBox.DropDownStyle = ComboBoxStyle.DropDownList;
            actionBox.SetBounds(14, 36, 410, 24);
            actionBox.Items.AddRange(Actions);
            actionBox.SelectedIndex = 0;

            var l2 = new Label();
            l2.Text = "2.  Press the key, then pick its signature below.";
            l2.SetBounds(14, 72, 400, 20);

            capture.Text = "Press a key...";
            capture.SetBounds(14, 94, 130, 28);
            capture.Click += delegate { StartCapture(); };

            status.SetBounds(154, 100, 270, 20);
            status.ForeColor = Color.DimGray;
            status.Text = "idle";

            candidates.SetBounds(14, 132, 410, 120);
            candidates.SelectedIndexChanged += delegate { save.Enabled = candidates.SelectedIndex >= 0; };

            save.Text = "Save binding";
            save.SetBounds(238, 264, 100, 30);
            save.Enabled = false;
            save.Click += delegate { Commit(); };

            var cancel = new Button();
            cancel.Text = "Cancel";
            cancel.SetBounds(346, 264, 78, 30);
            cancel.Click += delegate { DialogResult = DialogResult.Cancel; Close(); };

            Controls.AddRange(new Control[] { l1, actionBox, l2, capture, status, candidates, save, cancel });

            settle.Interval = 700;
            settle.Tick += delegate { settle.Stop(); FinishCapture(); };

            listener.Event += OnRawEvent;
            FormClosed += delegate { listener.Event -= OnRawEvent; settle.Stop(); };
        }

        void StartCapture()
        {
            seen.Clear();
            devices.Clear();
            candidates.Items.Clear();
            save.Enabled = false;
            capturing = true;
            captureStart = DateTime.UtcNow;
            status.Text = "listening - press the key now";
            status.ForeColor = Color.FromArgb(0, 120, 200);
        }

        void OnRawEvent(RawEvent e)
        {
            if (!capturing || !e.Pressed) return;
            // Ignore the keystroke that may have activated the button itself.
            if ((DateTime.UtcNow - captureStart).TotalMilliseconds < 350) return;

            if (!seen.Contains(e.Matcher))
            {
                seen.Add(e.Matcher);
                devices[e.Matcher] = ShortDevice(e.Device);
                candidates.Items.Add(Describe(e));
                if (!settle.Enabled) settle.Start();
            }
        }

        void FinishCapture()
        {
            capturing = false;
            if (seen.Count == 0)
            {
                status.Text = "nothing captured - try again";
                status.ForeColor = Color.Firebrick;
                return;
            }
            status.Text = seen.Count + " signature(s) captured";
            status.ForeColor = Color.FromArgb(30, 120, 40);
            candidates.SelectedIndex = BestIndex();
        }

        // Prefer the most portable signature: a real consumer usage beats a vendor
        // bitmap bit, which beats a plain virtual key.
        int BestIndex()
        {
            int best = 0, bestRank = -1;
            for (int i = 0; i < seen.Count; i++)
            {
                string m = seen[i];
                int rank;
                if (m.StartsWith("usage:000C/")) rank = 4;
                else if (m.StartsWith("usage:")) rank = 3;
                else if (m.StartsWith("bit:")) rank = 2;
                else rank = 1;
                if (rank > bestRank) { bestRank = rank; best = i; }
            }
            return best;
        }

        string Describe(RawEvent e)
        {
            string note = "";
            if (e.Kind == "usage")
            {
                string n = HidNames.Consumer(e.Page, e.Usage);
                if (n != null) note = "   (" + n + ")";
            }
            else if (e.Kind == "vk") note = "   (virtual key)";
            else note = "   (vendor report bit)";
            return e.Matcher + note + "   [" + ShortDevice(e.Device) + "]";
        }

        static string ShortDevice(string path)
        {
            if (string.IsNullOrEmpty(path)) return "?";
            int i = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return path;
            int end = path.IndexOf('#', i);
            return end > i ? path.Substring(i, end - i) : path.Substring(i);
        }

        void Commit()
        {
            if (candidates.SelectedIndex < 0) return;
            string matcher = seen[candidates.SelectedIndex];
            try
            {
                Result = Config.Parse(matcher, (string)actionBox.SelectedItem);
                DialogResult = DialogResult.OK;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "BrightBridge", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }
    }

    static class HidNames
    {
        static readonly Dictionary<ushort, string> consumer = new Dictionary<ushort, string>
        {
            { 0x006F, "Display Brightness Increment" },
            { 0x0070, "Display Brightness Decrement" },
            { 0x0079, "Keyboard Backlight Up" },
            { 0x007A, "Keyboard Backlight Down" },
            { 0x00B5, "Next Track" },
            { 0x00B6, "Previous Track" },
            { 0x00B7, "Stop" },
            { 0x00CD, "Play/Pause" },
            { 0x00E2, "Mute" },
            { 0x00E9, "Volume Up" },
            { 0x00EA, "Volume Down" },
            { 0x018A, "Mail" },
            { 0x0192, "Calculator" },
            { 0x0221, "Search" },
            { 0x0223, "Home" },
            { 0x0224, "Back" },
            { 0x0225, "Forward" },
        };

        public static string Consumer(ushort page, ushort usage)
        {
            if (page != 0x000C) return null;
            string n;
            return consumer.TryGetValue(usage, out n) ? n : null;
        }
    }
}
