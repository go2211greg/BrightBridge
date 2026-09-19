using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Timer = System.Windows.Forms.Timer;
using System.Windows.Forms;
using Microsoft.Win32;

namespace BrightBridge
{
    static class Program
    {
        [DllImport("user32.dll")] static extern bool SetProcessDPIAware();
        [DllImport("kernel32.dll")] static extern bool AttachConsole(int pid);

        public static string ExeDir
        {
            get { return Path.GetDirectoryName(Application.ExecutablePath); }
        }

        static string dataDir;

        /// <summary>
        /// Where the ini and log live. Next to the exe when that is writable, so a
        /// copy on a USB stick keeps its settings with it; otherwise under AppData,
        /// so running from Program Files or read-only media still works.
        /// </summary>
        public static string DataDir
        {
            get
            {
                if (dataDir != null) return dataDir;
                if (IsWritable(ExeDir)) return dataDir = ExeDir;
                string appData = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BrightBridge");
                try { Directory.CreateDirectory(appData); } catch { }
                return dataDir = appData;
            }
        }

        static bool IsWritable(string dir)
        {
            try
            {
                string probe = Path.Combine(dir, "." + Guid.NewGuid().ToString("N") + ".tmp");
                File.WriteAllText(probe, "");
                File.Delete(probe);
                return true;
            }
            catch { return false; }
        }

        public static string ConfigPath
        {
            get { return Path.Combine(DataDir, "BrightBridge.ini"); }
        }

        [STAThread]
        static int Main(string[] args)
        {
            try { SetProcessDPIAware(); } catch { }

            if (args.Length > 0)
            {
                try { AttachConsole(-1); } catch { }
                return Cli.Run(args);
            }

            using (var mutex = new Mutex(false, "Local\\BrightBridge.SingleInstance"))
            {
                if (!mutex.WaitOne(0, false))
                {
                    MessageBox.Show("BrightBridge is already running.", "BrightBridge",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return 0;
                }

                Log.File_ = Path.Combine(DataDir, "BrightBridge.log");

                // Without these, a crash leaves nothing behind but a Windows Error
                // Reporting entry. Log it where the rest of the story is.
                AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
                {
                    Log.Write("FATAL unhandled exception: " + e.ExceptionObject);
                };
                Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
                {
                    Log.Write("UI thread exception (continuing): " + e.Exception);
                };
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                using (var app = new TrayApp())
                    Application.Run(app);
            }
            return 0;
        }

        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr handle);

        public static Icon MakeIcon(bool enabled)
        {
            using (var bmp = Glyph.RenderMark(32,
                       enabled ? Glyph.Bright : Glyph.OffBright,
                       enabled ? Glyph.Dim : Glyph.OffDim,
                       false))
            {
                // Clone to a managed icon so the GDI handle can be released; the
                // menu is rebuilt often and these would otherwise accumulate.
                IntPtr h = bmp.GetHicon();
                try
                {
                    using (var temp = Icon.FromHandle(h))
                        return (Icon)temp.Clone();
                }
                finally { DestroyIcon(h); }
            }
        }
    }

    class TrayApp : ApplicationContext
    {
        Config config;
        DdcEngine engine;
        RawInputListener listener;
        Osd osd;
        NotifyIcon tray;
        readonly Timer repeatTimer = new Timer();
        readonly Timer menuRefresh = new Timer();
        Binding repeating;
        string repeatingMatcher;
        readonly HashSet<string> selfRepeating = new HashSet<string>();
        bool enabled = true;

        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunValue = "BrightBridge";

        public TrayApp()
        {
            config = Config.Load(Program.ConfigPath);
            Log.Write("started; " + config.Bindings.Count + " binding(s), targets='" + config.Targets + "'");

            engine = new DdcEngine(config.Targets);
            engine.Log += delegate(string s) { Log.Write(s); };
            foreach (var m in engine.Monitors)
                Log.Write("monitor: " + m.Description + "  [" + m.Device + "]");

            osd = new Osd();

            listener = new RawInputListener();
            listener.Event += OnRawEvent;
            listener.Resync += delegate(string reason) { engine.Resync(reason); };

            // Belt and braces: the window messages cover display power and system
            // resume, these cover the cases Windows reports only through here.
            SystemEvents.PowerModeChanged += OnPowerModeChanged;
            SystemEvents.SessionSwitch += OnSessionSwitch;

            repeatTimer.Tick += delegate
            {
                repeatTimer.Interval = config.RepeatRateMs;
                if (repeating != null) Apply(repeating);
            };

            menuRefresh.Interval = 1500;
            menuRefresh.Tick += delegate { menuRefresh.Stop(); RebuildMenu(); };

            BuildTray();
            PrimeAll();
        }

        void PrimeAll()
        {
            var codes = new HashSet<byte>();
            foreach (var b in config.Bindings) codes.Add(b.Vcp);
            foreach (var c in codes) engine.Prime(c);
        }

        void BuildTray()
        {
            tray = new NotifyIcon();
            tray.Icon = Program.MakeIcon(enabled);
            tray.Text = "BrightBridge";
            tray.Visible = true;
            tray.DoubleClick += delegate { ToggleEnabled(); };
            RebuildMenu();
        }

        void RebuildMenu()
        {
            var menu = new ContextMenuStrip();

            var header = new ToolStripMenuItem("BrightBridge");
            header.Enabled = false;
            menu.Items.Add(header);
            menu.Items.Add(new ToolStripSeparator());

            var en = new ToolStripMenuItem("Enabled");
            en.Checked = enabled;
            en.CheckOnClick = true;
            en.Click += delegate { ToggleEnabled(); };
            menu.Items.Add(en);

            var learn = new ToolStripMenuItem("Learn a key...");
            learn.Click += delegate { DoLearn(); };
            menu.Items.Add(learn);

            var binds = new ToolStripMenuItem("Bindings");
            if (config.Bindings.Count == 0)
            {
                var none = new ToolStripMenuItem("(none)");
                none.Enabled = false;
                binds.DropDownItems.Add(none);
            }
            foreach (var b in config.Bindings)
            {
                var bound = b;
                var item = new ToolStripMenuItem(b.Describe());
                var remove = new ToolStripMenuItem("Remove this binding");
                remove.Click += delegate
                {
                    config.Bindings.Remove(bound);
                    config.Save();
                    Log.Write("removed binding " + bound.Describe());
                    RebuildMenu();
                };
                item.DropDownItems.Add(remove);
                binds.DropDownItems.Add(item);
            }
            menu.Items.Add(binds);

            menu.Items.Add(new ToolStripSeparator());

            var monitors = new ToolStripMenuItem("Monitors");
            foreach (var m in engine.Monitors)
            {
                var item = new ToolStripMenuItem(m.Description + "   (" + m.Device + ")");
                item.Enabled = false;
                monitors.DropDownItems.Add(item);
            }
            var rescan = new ToolStripMenuItem("Rescan now");
            // The rescan happens on the worker thread, so refresh the list once it
            // has had time to finish rather than redrawing the stale one.
            rescan.Click += delegate { engine.Resync("manual rescan"); menuRefresh.Stop(); menuRefresh.Start(); };
            monitors.DropDownItems.Add(new ToolStripSeparator());
            monitors.DropDownItems.Add(rescan);
            menu.Items.Add(monitors);

            var openCfg = new ToolStripMenuItem("Open config file");
            openCfg.Click += delegate { OpenPath(config.Path); };
            menu.Items.Add(openCfg);

            var reload = new ToolStripMenuItem("Reload config");
            reload.Click += delegate { ReloadConfig(); };
            menu.Items.Add(reload);

            var openLog = new ToolStripMenuItem("Open log");
            openLog.Click += delegate { OpenPath(Log.File_); };
            menu.Items.Add(openLog);

            var autostart = new ToolStripMenuItem("Start with Windows");
            autostart.Checked = IsAutostart();
            autostart.Click += delegate { SetAutostart(!IsAutostart()); RebuildMenu(); };
            menu.Items.Add(autostart);

            menu.Items.Add(new ToolStripSeparator());

            var exit = new ToolStripMenuItem("Exit");
            exit.Click += delegate { ExitApp(); };
            menu.Items.Add(exit);

            tray.ContextMenuStrip = menu;
        }

        void ToggleEnabled()
        {
            enabled = !enabled;
            tray.Icon = Program.MakeIcon(enabled);
            Log.Write(enabled ? "enabled" : "disabled");
            StopRepeat();
            RebuildMenu();
        }

        void ReloadConfig()
        {
            config = Config.Load(Program.ConfigPath);
            engine.Dispose();
            engine = new DdcEngine(config.Targets);
            engine.Log += delegate(string s) { Log.Write(s); };
            PrimeAll();
            RebuildMenu();
            Log.Write("config reloaded; " + config.Bindings.Count + " binding(s)");
        }

        void DoLearn()
        {
            using (var f = new LearnForm(listener))
            {
                if (f.ShowDialog() != DialogResult.OK || f.Result == null) return;
                config.Bindings.RemoveAll(delegate(Binding b) { return b.Matcher == f.Result.Matcher; });
                config.Bindings.Add(f.Result);
                config.Save();
                engine.Prime(f.Result.Vcp);
                RebuildMenu();
                Log.Write("learned " + f.Result.Describe());
            }
        }

        void OnRawEvent(RawEvent e)
        {
            if (!enabled) return;

            if (!e.Pressed)
            {
                if (repeatingMatcher != null && repeatingMatcher == e.Matcher) StopRepeat();
                return;
            }

            Binding hit = null;
            foreach (var b in config.Bindings)
                if (b.Matcher == e.Matcher) { hit = b; break; }
            if (hit == null) return;

            // A second press while our own repeat is running means the keyboard
            // repeats in hardware. Let it drive, so the two do not compound.
            if (repeatingMatcher != null && repeatingMatcher == e.Matcher)
            {
                StopRepeat();
                if (selfRepeating.Add(e.Matcher))
                    Log.Write("device auto-repeats " + e.Matcher + "; using its rate");
            }

            Apply(hit);

            // Virtual keys already auto-repeat at the OS level; HID usages and
            // vendor bits report press/release only, so we drive the repeat.
            if (config.Repeat && e.Kind != "vk" && !hit.Absolute && !selfRepeating.Contains(e.Matcher))
            {
                repeating = hit;
                repeatingMatcher = e.Matcher;
                repeatTimer.Interval = config.RepeatDelayMs;
                repeatTimer.Start();
            }
        }

        // These arrive on a system thread, not the UI thread. Requesting a rescan
        // is just a flag and a wake, so there is nothing to marshal.
        void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume) engine.Resync("power mode resume");
            else if (e.Mode == PowerModes.Suspend) Log.Write("system suspending");
        }

        void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
        {
            if (e.Reason == SessionSwitchReason.SessionUnlock
             || e.Reason == SessionSwitchReason.ConsoleConnect
             || e.Reason == SessionSwitchReason.SessionLogon)
                engine.Resync("session " + e.Reason);
        }

        void StopRepeat()
        {
            repeatTimer.Stop();
            repeating = null;
            repeatingMatcher = null;
        }

        void Apply(Binding b)
        {
            int amount = b.EffectiveAmount(config.Step);
            int snapTo = (config.Snap && b.UsesStep && !b.Absolute) ? config.Step : 0;
            int pct = engine.Adjust(b.Vcp, amount, b.Absolute, snapTo);
            if (config.Osd)
            {
                if (pct < 0) pct = engine.KnownValue(b.Vcp);
                if (pct >= 0) osd.Show(Capitalize(Config.VcpName(b.Vcp)), pct);
            }
        }

        static string Capitalize(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            return char.ToUpper(s[0]) + s.Substring(1);
        }

        static void OpenPath(string path)
        {
            try
            {
                if (path != null && File.Exists(path)) System.Diagnostics.Process.Start("notepad.exe", "\"" + path + "\"");
                else MessageBox.Show("Not found: " + path, "BrightBridge");
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "BrightBridge"); }
        }

        static bool IsAutostart()
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, false))
                    return k != null && k.GetValue(RunValue) != null;
            }
            catch { return false; }
        }

        static void SetAutostart(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on) k.SetValue(RunValue, "\"" + Application.ExecutablePath + "\"");
                    else k.DeleteValue(RunValue, false);
                }
                Log.Write("autostart " + (on ? "enabled" : "disabled"));
            }
            catch (Exception ex) { MessageBox.Show(ex.Message, "BrightBridge"); }
        }

        void ExitApp()
        {
            StopRepeat();
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            if (tray != null) { tray.Visible = false; tray.Dispose(); }
            if (listener != null) listener.Dispose();
            if (engine != null) engine.Dispose();
            if (osd != null) osd.Dispose();
            ExitThread();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                if (tray != null) { tray.Visible = false; tray.Dispose(); tray = null; }
            }
            base.Dispose(disposing);
        }
    }
}
