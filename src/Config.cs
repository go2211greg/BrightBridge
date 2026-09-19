using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace BrightBridge
{
    class Binding
    {
        public string Matcher;     // "usage:000C/006F" | "bit:FFC0/03/7/01" | "vk:AF"
        public byte Vcp;           // VCP opcode to drive
        public int Amount;         // delta, or absolute value when Absolute
        public bool Absolute;
        public bool UsesStep;      // Amount follows config.Step
        public string ActionText;  // as written in the file

        public int EffectiveAmount(int step)
        {
            if (!UsesStep) return Amount;
            return Amount < 0 ? -step : step;
        }

        public string Describe()
        {
            return Matcher + " => " + ActionText;
        }
    }

    class Config
    {
        public string Targets = "";        // substring match on monitor description; "" = all
        public bool Osd = false;           // Windows draws its own flyout; ours is opt-in
        public bool Repeat = true;
        public int Step = 10;              // matches the shell's 10% grid
        public bool Snap = true;           // land on multiples of Step, like Windows does
        public int RepeatDelayMs = 400;
        public int RepeatRateMs = 250;     // 4 Hz, the rate the Windows flyout advances at
        public List<Binding> Bindings = new List<Binding>();
        public string Path;

        public static readonly Dictionary<string, byte> Aliases = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase)
        {
            { "brightness", 0x10 },
            { "contrast",   0x12 },
            { "volume",     0x62 },
        };

        public static string VcpName(byte code)
        {
            foreach (var kv in Aliases) if (kv.Value == code) return kv.Key;
            return "vcp" + code.ToString("X2");
        }

        public static Config Default(string path)
        {
            var c = new Config();
            c.Path = path;
            // Standard HID consumer brightness keys, in case the keyboard speaks them.
            c.Bindings.Add(Parse("usage:000C/006F", "brightness:+step"));
            c.Bindings.Add(Parse("usage:000C/0070", "brightness:-step"));
            return c;
        }

        public static Binding Parse(string matcher, string action)
        {
            var b = new Binding();
            b.Matcher = matcher.Trim();
            b.ActionText = action.Trim();

            int colon = b.ActionText.LastIndexOf(':');
            if (colon <= 0) throw new FormatException("bad action '" + action + "'");
            string name = b.ActionText.Substring(0, colon).Trim();
            string val = b.ActionText.Substring(colon + 1).Trim();

            byte code;
            if (Aliases.TryGetValue(name, out code)) b.Vcp = code;
            else if (name.StartsWith("vcp", StringComparison.OrdinalIgnoreCase)
                     && byte.TryParse(name.Substring(3), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code)) b.Vcp = code;
            else throw new FormatException("unknown control '" + name + "'");

            if (val.Length < 2) throw new FormatException("bad amount '" + val + "'");
            char sign = val[0];
            string num = val.Substring(1).Trim();
            bool isStep = num.Equals("step", StringComparison.OrdinalIgnoreCase);
            int amount = 0;
            if (!isStep && !int.TryParse(num, out amount)) throw new FormatException("bad amount '" + val + "'");

            if (sign == '=') { b.Absolute = true; b.Amount = amount; }
            else if (sign == '+') { b.Amount = amount; b.UsesStep = isStep; }
            else if (sign == '-') { b.Amount = -amount; b.UsesStep = isStep; if (isStep) b.Amount = -1; }
            else throw new FormatException("amount must start with + - or = : '" + val + "'");

            return b;
        }

        public static Config Load(string path)
        {
            if (!File.Exists(path)) { var d = Default(path); d.Save(); return d; }
            var c = new Config();
            c.Path = path;
            int lineNo = 0;
            foreach (var rawLine in File.ReadAllLines(path))
            {
                lineNo++;
                string line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();

                try
                {
                    if (key.Equals("targets", StringComparison.OrdinalIgnoreCase)) c.Targets = value;
                    else if (key.Equals("osd", StringComparison.OrdinalIgnoreCase)) c.Osd = Truthy(value);
                    else if (key.Equals("repeat", StringComparison.OrdinalIgnoreCase)) c.Repeat = Truthy(value);
                    else if (key.Equals("snap", StringComparison.OrdinalIgnoreCase)) c.Snap = Truthy(value);
                    else if (key.Equals("step", StringComparison.OrdinalIgnoreCase)) c.Step = int.Parse(value);
                    else if (key.Equals("repeatdelayms", StringComparison.OrdinalIgnoreCase)) c.RepeatDelayMs = int.Parse(value);
                    else if (key.Equals("repeatratems", StringComparison.OrdinalIgnoreCase)) c.RepeatRateMs = int.Parse(value);
                    else if (key.Equals("bind", StringComparison.OrdinalIgnoreCase))
                    {
                        int arrow = value.IndexOf("=>", StringComparison.Ordinal);
                        if (arrow < 0) continue;
                        c.Bindings.Add(Parse(value.Substring(0, arrow), value.Substring(arrow + 2)));
                    }
                }
                catch (Exception ex)
                {
                    Log.Write("config line " + lineNo + ": " + ex.Message);
                }
            }
            if (c.Step <= 0) c.Step = 5;
            return c;
        }

        static bool Truthy(string v)
        {
            return v.Equals("1") || v.Equals("true", StringComparison.OrdinalIgnoreCase)
                || v.Equals("yes", StringComparison.OrdinalIgnoreCase) || v.Equals("on", StringComparison.OrdinalIgnoreCase);
        }

        public void Save()
        {
            var sb = new StringBuilder();
            sb.AppendLine("# BrightBridge configuration");
            sb.AppendLine("#");
            sb.AppendLine("# targets        substring of the monitor name to drive; blank = every DDC/CI monitor");
            sb.AppendLine("# step           how much one key press moves the control");
            sb.AppendLine("# snap           land on exact multiples of step, the way the Windows flyout does");
            sb.AppendLine("# osd            show our own on-screen readout (Windows draws its own regardless)");
            sb.AppendLine("# repeat         hold a key to keep moving");
            sb.AppendLine("# repeatRateMs   250 = 4 Hz, matching the rate the Windows flyout advances at");
            sb.AppendLine("#");
            sb.AppendLine("# bind = <matcher> => <control>:<amount>");
            sb.AppendLine("#   matcher   usage:PPPP/UUUU     decoded HID usage (page/usage)");
            sb.AppendLine("#             bit:PPPP/RR/N/MM    vendor report bit (page/reportId/byteIndex/bitMask)");
            sb.AppendLine("#             vk:XX               virtual key code");
            sb.AppendLine("#   control   brightness | contrast | volume | vcpXX");
            sb.AppendLine("#   amount    +step -step +N -N =N");
            sb.AppendLine("#");
            sb.AppendLine("# Run  BrightBridge.exe --spy  to discover what your keys emit.");
            sb.AppendLine();
            sb.AppendLine("targets=" + Targets);
            sb.AppendLine("step=" + Step);
            sb.AppendLine("snap=" + (Snap ? "true" : "false"));
            sb.AppendLine("osd=" + (Osd ? "true" : "false"));
            sb.AppendLine("repeat=" + (Repeat ? "true" : "false"));
            sb.AppendLine("repeatDelayMs=" + RepeatDelayMs);
            sb.AppendLine("repeatRateMs=" + RepeatRateMs);
            sb.AppendLine();
            foreach (var b in Bindings)
                sb.AppendLine("bind=" + b.Matcher + " => " + b.ActionText);

            // Never fatal: a read-only location just means settings are not persisted.
            try
            {
                var dir = System.IO.Path.GetDirectoryName(Path);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(Path, sb.ToString());
            }
            catch (Exception ex)
            {
                Log.Write("could not save config to " + Path + ": " + ex.Message);
            }
        }
    }

    static class Log
    {
        static readonly object gate = new object();
        public static string File_;
        public static Action<string> Echo;

        public static void Write(string msg)
        {
            string line = DateTime.Now.ToString("HH:mm:ss.fff") + "  " + msg;
            var echo = Echo;
            if (echo != null) echo(line);
            if (File_ == null) return;
            lock (gate)
            {
                try { System.IO.File.AppendAllText(File_, line + Environment.NewLine); } catch { }
            }
        }
    }
}
