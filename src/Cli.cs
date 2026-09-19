using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Forms;

namespace BrightBridge
{
    static class Cli
    {
        public static int Run(string[] args)
        {
            string cmd = args[0].TrimStart('-', '/').ToLowerInvariant();
            try
            {
                switch (cmd)
                {
                    case "list": return List();
                    case "caps": return Caps();
                    case "get": return Get(Arg(args, 1, "brightness"));
                    case "set": return Set(Arg(args, 1, null), Arg(args, 2, null));
                    case "adjust": return Adjust(Arg(args, 1, null), Arg(args, 2, null));
                    case "spy": return Spy(Arg(args, 1, "30"));
                    case "help":
                    case "?": Help(); return 0;
                    default:
                        Console.WriteLine("unknown command: " + args[0]);
                        Help();
                        return 2;
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine("error: " + ex.Message);
                return 1;
            }
        }

        static string Arg(string[] a, int i, string fallback)
        {
            return i < a.Length ? a[i] : fallback;
        }

        static void Help()
        {
            Console.WriteLine();
            Console.WriteLine("BrightBridge - drive monitor controls over DDC/CI from keyboard media keys");
            Console.WriteLine();
            Console.WriteLine("  BrightBridge.exe                     run in the tray (normal use)");
            Console.WriteLine("  BrightBridge.exe --list              list DDC/CI monitors and current values");
            Console.WriteLine("  BrightBridge.exe --caps              dump each monitor's MCCS capability string");
            Console.WriteLine("  BrightBridge.exe --get <control>     read a control");
            Console.WriteLine("  BrightBridge.exe --set <control> <n> write an absolute value");
            Console.WriteLine("  BrightBridge.exe --adjust <ctl> <+n> nudge a control");
            Console.WriteLine("  BrightBridge.exe --spy [seconds]     print what your keys emit (default 30s)");
            Console.WriteLine();
            Console.WriteLine("  <control> = brightness | contrast | volume | vcpXX  (XX = hex VCP opcode)");
            Console.WriteLine();
        }

        static byte ParseControl(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new ArgumentException("missing control name");
            byte code;
            if (Config.Aliases.TryGetValue(name, out code)) return code;
            if (name.StartsWith("vcp", StringComparison.OrdinalIgnoreCase)
                && byte.TryParse(name.Substring(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) return code;
            if (byte.TryParse(name, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) return code;
            throw new ArgumentException("unknown control '" + name + "'");
        }

        static int List()
        {
            using (var e = new DdcEngine(""))
            {
                var mons = e.Monitors;
                if (mons.Count == 0) { Console.WriteLine("no DDC/CI monitors found"); return 1; }
                foreach (var m in mons)
                {
                    Console.WriteLine(m.Description + "   [" + m.Device + "]");
                    byte[] codes = { 0x10, 0x12, 0x62 };
                    foreach (var c in codes)
                    {
                        int cur, max;
                        if (e.ReadVcp(m, c, out cur, out max))
                            Console.WriteLine("    " + Config.VcpName(c).PadRight(11) + " (0x" + c.ToString("X2") + ")  "
                                + cur + " / " + max);
                        else
                            Console.WriteLine("    " + Config.VcpName(c).PadRight(11) + " (0x" + c.ToString("X2") + ")  not supported");
                    }
                }
            }
            return 0;
        }

        static int Caps()
        {
            using (var e = new DdcEngine(""))
            {
                foreach (var m in e.Monitors)
                {
                    Console.WriteLine(m.Description + "   [" + m.Device + "]");
                    string caps = e.ReadCapabilities(m);
                    Console.WriteLine(caps == null ? "    (no capability string)" : "    " + caps);
                }
            }
            return 0;
        }

        static int Get(string control)
        {
            byte code = ParseControl(control);
            using (var e = new DdcEngine(""))
            {
                foreach (var m in e.Monitors)
                {
                    int cur, max;
                    if (e.ReadVcp(m, code, out cur, out max))
                        Console.WriteLine(m.Description + "  " + cur + " / " + max);
                    else
                        Console.WriteLine(m.Description + "  not supported");
                }
            }
            return 0;
        }

        static int Set(string control, string value)
        {
            byte code = ParseControl(control);
            int v;
            if (!int.TryParse(value, out v)) throw new ArgumentException("bad value '" + value + "'");
            using (var e = new DdcEngine(""))
            {
                foreach (var m in e.Monitors)
                {
                    int cur, max;
                    if (!e.ReadVcp(m, code, out cur, out max)) { Console.WriteLine(m.Description + "  not supported"); continue; }
                    int want = v < 0 ? 0 : (v > max ? max : v);
                    bool ok = e.WriteVcp(m, code, want);
                    Console.WriteLine(m.Description + "  " + cur + " -> " + want + (ok ? "  ok" : "  FAILED"));
                }
            }
            return 0;
        }

        static int Adjust(string control, string delta)
        {
            byte code = ParseControl(control);
            int d;
            if (!int.TryParse(delta, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out d))
                throw new ArgumentException("bad delta '" + delta + "'");
            using (var e = new DdcEngine(""))
            {
                foreach (var m in e.Monitors)
                {
                    int cur, max;
                    if (!e.ReadVcp(m, code, out cur, out max)) { Console.WriteLine(m.Description + "  not supported"); continue; }
                    int want = cur + d;
                    if (want < 0) want = 0;
                    if (want > max) want = max;
                    bool ok = e.WriteVcp(m, code, want);
                    Console.WriteLine(m.Description + "  " + cur + " -> " + want + (ok ? "  ok" : "  FAILED"));
                }
            }
            return 0;
        }

        static int Spy(string secondsText)
        {
            int seconds;
            if (!int.TryParse(secondsText, out seconds) || seconds <= 0) seconds = 30;

            Console.WriteLine();
            Console.WriteLine("Listening for " + seconds + "s. Press the keys you want to bind.");
            Console.WriteLine("Copy the matcher (first column) into BrightBridge.ini, or use tray > Learn a key.");
            Console.WriteLine();

            var listener = new RawInputListener();
            listener.Event += delegate(RawEvent e)
            {
                // Releases are shown too: holding a key and seeing repeated DOWN
                // lines is how you tell whether the keyboard repeats in hardware.
                string note = "";
                if (e.Kind == "usage")
                {
                    string n = HidNames.Consumer(e.Page, e.Usage);
                    if (n != null) note = "  " + n;
                }
                Console.WriteLine(DateTime.Now.ToString("HH:mm:ss.fff") + "  "
                    + (e.Pressed ? "DOWN  " : "up    ")
                    + e.Matcher.PadRight(26) + Short(e.Device).PadRight(22) + note);
            };

            var quit = new Timer();
            quit.Interval = seconds * 1000;
            quit.Tick += delegate { quit.Stop(); Application.ExitThread(); };
            quit.Start();

            Application.Run();
            listener.Dispose();
            Console.WriteLine();
            Console.WriteLine("done");
            return 0;
        }

        static string Short(string path)
        {
            if (string.IsNullOrEmpty(path)) return "?";
            int i = path.IndexOf("VID_", StringComparison.OrdinalIgnoreCase);
            if (i < 0) return path;
            int end = path.IndexOf('#', i);
            return end > i ? path.Substring(i, end - i) : path.Substring(i);
        }
    }
}
