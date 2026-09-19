using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace BrightBridge
{
    class RawEvent
    {
        public string Kind;        // "usage" | "bit" | "vk"
        public bool Pressed;
        public ushort Page, Usage, Vk;
        public byte ReportId;
        public int ByteIndex;
        public byte BitMask;
        public string Device;
        public string Matcher;     // canonical form used in the config file

        public override string ToString()
        {
            return (Pressed ? "DOWN " : "UP   ") + Matcher.PadRight(26) + " " + Device;
        }
    }

    /// <summary>
    /// Listens to every HID top-level collection on the machine via Raw Input with
    /// RIDEV_INPUTSINK, so keys are seen regardless of which window has focus.
    ///
    /// Two views of the same report are published, because vendor keyboards differ:
    ///   "usage" - decoded through HidP, the correct route for standard consumer keys
    ///   "bit"   - a raw byte/bit transition, which is how ASUS reports its keys on
    ///             vendor page 0xFFC0 where HidP yields nothing useful
    /// </summary>
    class RawInputListener : NativeWindow, IDisposable
    {
        const int WM_INPUT = 0x00FF;
        const int WM_DISPLAYCHANGE = 0x007E;
        const int WM_DEVICECHANGE = 0x0219;
        const int DBT_DEVNODES_CHANGED = 0x0007;
        const int WM_POWERBROADCAST = 0x0218;
        const int PBT_APMSUSPEND = 0x0004;
        const int PBT_APMRESUMESUSPEND = 0x0007;
        const int PBT_APMRESUMEAUTOMATIC = 0x0012;
        const int PBT_POWERSETTINGCHANGE = 0x8013;

        // Fires when the display is switched off, dimmed or switched back on -
        // screen blanking and power saving, which never raise WM_DISPLAYCHANGE.
        static readonly Guid GuidConsoleDisplayState = new Guid("6fe69556-704a-47a0-8f24-c28d936fda47");

        [StructLayout(LayoutKind.Sequential)]
        struct POWERBROADCAST_SETTING { public Guid PowerSetting; public uint DataLength; public byte Data; }
        const uint RID_INPUT = 0x10000003;
        const uint RIDI_DEVICENAME = 0x20000007;
        const uint RIDI_DEVICEINFO = 0x2000000b;
        const uint RIDI_PREPARSEDDATA = 0x20000005;
        const uint RIDEV_INPUTSINK = 0x00000100;
        const int RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1, RIM_TYPEHID = 2;
        const int HIDP_STATUS_SUCCESS = 0x00110000;
        const ushort POWER_DEVICE_PAGE = 0x0084;   // UPS telemetry - noisy, never a key

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICELIST { public IntPtr hDevice; public uint dwType; }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }

        [StructLayout(LayoutKind.Sequential)]
        struct RID_DEVICE_INFO_HID { public uint dwVendorId, dwProductId, dwVersionNumber; public ushort usUsagePage, usUsage; }

        [StructLayout(LayoutKind.Sequential)]
        struct RID_DEVICE_INFO_KEYBOARD { public uint dwType, dwSubType, dwKeyboardMode, dwNumberOfFunctionKeys, dwNumberOfIndicators, dwNumberOfKeysTotal; }

        [StructLayout(LayoutKind.Explicit)]
        struct RID_DEVICE_INFO
        {
            [FieldOffset(0)] public uint cbSize;
            [FieldOffset(4)] public uint dwType;
            [FieldOffset(8)] public RID_DEVICE_INFO_HID hid;
            [FieldOffset(8)] public RID_DEVICE_INFO_KEYBOARD keyboard;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWINPUTHEADER { public uint dwType, dwSize; public IntPtr hDevice; public IntPtr wParam; }

        [StructLayout(LayoutKind.Sequential)]
        struct RAWKEYBOARD { public ushort MakeCode, Flags, Reserved, VKey; public uint Message; public uint ExtraInformation; }

        [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputDeviceList([Out] RAWINPUTDEVICELIST[] list, ref uint num, uint size);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern uint GetRawInputDeviceInfoW(IntPtr h, uint cmd, IntPtr data, ref uint size);
        [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] d, uint num, uint size);
        [DllImport("user32.dll", SetLastError = true)] static extern uint GetRawInputData(IntPtr hRawInput, uint cmd, IntPtr data, ref uint size, uint headerSize);
        [DllImport("hid.dll")] static extern int HidP_GetUsages(int reportType, ushort usagePage, ushort linkCollection, [In, Out] ushort[] usageList, ref int usageLength, IntPtr preparsed, byte[] report, int reportLength);
        [DllImport("hid.dll")] static extern int HidP_MaxUsageListLength(int reportType, ushort usagePage, IntPtr preparsed);
        [DllImport("user32.dll", SetLastError = true)] static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSettingGuid, int flags);
        [DllImport("user32.dll")] static extern bool UnregisterPowerSettingNotification(IntPtr handle);

        readonly Dictionary<IntPtr, string> deviceNames = new Dictionary<IntPtr, string>();
        readonly Dictionary<IntPtr, RID_DEVICE_INFO> deviceInfos = new Dictionary<IntPtr, RID_DEVICE_INFO>();
        readonly Dictionary<IntPtr, IntPtr> preparsed = new Dictionary<IntPtr, IntPtr>();
        readonly Dictionary<string, byte[]> lastReport = new Dictionary<string, byte[]>();
        readonly Dictionary<IntPtr, HashSet<uint>> lastUsages = new Dictionary<IntPtr, HashSet<uint>>();

        public event Action<RawEvent> Event;
        public event Action<string> Resync;

        IntPtr powerNotify = IntPtr.Zero;

        public RawInputListener()
        {
            var cp = new CreateParams();
            cp.Caption = "BrightBridge.RawInput";
            cp.Style = unchecked((int)0x80000000); // WS_POPUP, never shown
            cp.X = 0; cp.Y = 0; cp.Width = 0; cp.Height = 0;
            CreateHandle(cp);
            Register();

            Guid g = GuidConsoleDisplayState;
            powerNotify = RegisterPowerSettingNotification(Handle, ref g, 0 /* DEVICE_NOTIFY_WINDOW_HANDLE */);
        }

        void RaiseResync(string reason)
        {
            var h = Resync;
            if (h != null) h(reason);
        }

        void Register()
        {
            uint num = 0;
            uint elemSize = (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICELIST));
            GetRawInputDeviceList(null, ref num, elemSize);
            var list = new RAWINPUTDEVICELIST[num];
            GetRawInputDeviceList(list, ref num, elemSize);

            var pairs = new HashSet<uint>();
            foreach (var d in list)
            {
                if (d.dwType == RIM_TYPEMOUSE) continue;
                if (d.dwType == RIM_TYPEKEYBOARD) { pairs.Add(0x00010006u); continue; }
                var info = DeviceInfo(d.hDevice);
                ushort page = info.hid.usUsagePage, usage = info.hid.usUsage;
                if (usage == 0) continue;                 // not registerable
                if (page == POWER_DEVICE_PAGE) continue;  // UPS chatter
                pairs.Add((uint)page << 16 | usage);
            }

            foreach (uint p in pairs)
            {
                var one = new RAWINPUTDEVICE[1];
                one[0].usUsagePage = (ushort)(p >> 16);
                one[0].usUsage = (ushort)(p & 0xFFFF);
                one[0].dwFlags = RIDEV_INPUTSINK;
                one[0].hwndTarget = Handle;
                RegisterRawInputDevices(one, 1, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            }
        }

        string DeviceName(IntPtr h)
        {
            string n;
            if (deviceNames.TryGetValue(h, out n)) return n;
            uint size = 0;
            GetRawInputDeviceInfoW(h, RIDI_DEVICENAME, IntPtr.Zero, ref size);
            IntPtr buf = Marshal.AllocHGlobal((int)(size + 1) * 2);
            GetRawInputDeviceInfoW(h, RIDI_DEVICENAME, buf, ref size);
            n = Marshal.PtrToStringUni(buf);
            Marshal.FreeHGlobal(buf);
            deviceNames[h] = n ?? "";
            return deviceNames[h];
        }

        RID_DEVICE_INFO DeviceInfo(IntPtr h)
        {
            RID_DEVICE_INFO cached;
            if (deviceInfos.TryGetValue(h, out cached)) return cached;
            var info = new RID_DEVICE_INFO();
            info.cbSize = (uint)Marshal.SizeOf(typeof(RID_DEVICE_INFO));
            uint size = info.cbSize;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            Marshal.StructureToPtr(info, buf, false);
            GetRawInputDeviceInfoW(h, RIDI_DEVICEINFO, buf, ref size);
            info = (RID_DEVICE_INFO)Marshal.PtrToStructure(buf, typeof(RID_DEVICE_INFO));
            Marshal.FreeHGlobal(buf);
            deviceInfos[h] = info;
            return info;
        }

        IntPtr Preparsed(IntPtr h)
        {
            IntPtr p;
            if (preparsed.TryGetValue(h, out p)) return p;
            uint size = 0;
            GetRawInputDeviceInfoW(h, RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
            if (size == 0) { preparsed[h] = IntPtr.Zero; return IntPtr.Zero; }
            p = Marshal.AllocHGlobal((int)size);
            if (GetRawInputDeviceInfoW(h, RIDI_PREPARSEDDATA, p, ref size) == unchecked((uint)-1))
            {
                Marshal.FreeHGlobal(p);
                p = IntPtr.Zero;
            }
            preparsed[h] = p;
            return p;
        }

        void Emit(RawEvent e) { var h = Event; if (h != null) h(e); }

        DateTime lastReregister = DateTime.MinValue;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_INPUT) { OnInput(m.LParam); return; }
            if (m.Msg == WM_DISPLAYCHANGE) RaiseResync("display settings changed");

            // Everything here must return immediately. Windows is often mid power
            // transition, and a handler that blocks gets the process killed.
            if (m.Msg == WM_POWERBROADCAST)
            {
                int ev = m.WParam.ToInt32();
                if (ev == PBT_APMRESUMEAUTOMATIC || ev == PBT_APMRESUMESUSPEND) RaiseResync("system resumed");
                else if (ev == PBT_APMSUSPEND) RaiseResync("system suspending");
                else if (ev == PBT_POWERSETTINGCHANGE && m.LParam != IntPtr.Zero)
                {
                    var setting = (POWERBROADCAST_SETTING)Marshal.PtrToStructure(m.LParam, typeof(POWERBROADCAST_SETTING));
                    if (setting.PowerSetting == GuidConsoleDisplayState && setting.Data == 1)
                        RaiseResync("display powered on");
                }
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_DEVICECHANGE && m.WParam.ToInt64() == DBT_DEVNODES_CHANGED)
            {
                // A wireless keyboard that reconnects can expose a collection we were
                // not listening to yet. Re-registering the same page is harmless.
                if ((DateTime.UtcNow - lastReregister).TotalSeconds > 2)
                {
                    lastReregister = DateTime.UtcNow;
                    deviceInfos.Clear();
                    try { Register(); } catch { }
                }
            }
            base.WndProc(ref m);
        }

        void OnInput(IntPtr hRawInput)
        {
            uint size = 0;
            uint hdrSize = (uint)Marshal.SizeOf(typeof(RAWINPUTHEADER));
            GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, hdrSize);
            if (size == 0) return;
            IntPtr buf = Marshal.AllocHGlobal((int)size);
            try
            {
                if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, hdrSize) != size) return;
                var hdr = (RAWINPUTHEADER)Marshal.PtrToStructure(buf, typeof(RAWINPUTHEADER));
                IntPtr body = (IntPtr)(buf.ToInt64() + hdrSize);
                string dev = DeviceName(hdr.hDevice);

                if (hdr.dwType == RIM_TYPEKEYBOARD)
                {
                    var kb = (RAWKEYBOARD)Marshal.PtrToStructure(body, typeof(RAWKEYBOARD));
                    if (kb.VKey == 0 || kb.VKey == 0xFF) return;
                    bool down = kb.Message == 0x0100 || kb.Message == 0x0104;
                    bool up = kb.Message == 0x0101 || kb.Message == 0x0105;
                    if (!down && !up) return;
                    var e = new RawEvent();
                    e.Kind = "vk"; e.Pressed = down; e.Vk = kb.VKey; e.Device = dev;
                    e.Matcher = "vk:" + kb.VKey.ToString("X2");
                    Emit(e);
                }
                else if (hdr.dwType == RIM_TYPEHID)
                {
                    uint sizeHid = (uint)Marshal.ReadInt32(body, 0);
                    uint count = (uint)Marshal.ReadInt32(body, 4);
                    if (sizeHid == 0 || count == 0) return;
                    IntPtr raw = (IntPtr)(body.ToInt64() + 8);
                    var info = DeviceInfo(hdr.hDevice);
                    for (uint i = 0; i < count; i++)
                    {
                        var report = new byte[sizeHid];
                        Marshal.Copy((IntPtr)(raw.ToInt64() + i * sizeHid), report, 0, (int)sizeHid);
                        HandleHidReport(hdr.hDevice, dev, info.hid.usUsagePage, report);
                    }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
        }

        void HandleHidReport(IntPtr hDevice, string dev, ushort devicePage, byte[] report)
        {
            byte reportId = report.Length > 0 ? report[0] : (byte)0;

            // --- View 1: properly decoded usages (standard consumer / generic desktop keys)
            IntPtr pp = Preparsed(hDevice);
            if (pp != IntPtr.Zero)
            {
                var now = new HashSet<uint>();
                CollectUsages(pp, 0x000C, report, now);
                CollectUsages(pp, 0x0001, report, now);
                if (devicePage != 0x000C && devicePage != 0x0001) CollectUsages(pp, devicePage, report, now);

                HashSet<uint> before;
                if (!lastUsages.TryGetValue(hDevice, out before)) before = new HashSet<uint>();
                foreach (uint u in now)
                    if (!before.Contains(u)) EmitUsage(dev, u, true);
                foreach (uint u in before)
                    if (!now.Contains(u)) EmitUsage(dev, u, false);
                lastUsages[hDevice] = now;
            }

            // --- View 2: raw bit transitions (vendor bitmaps such as ASUS page 0xFFC0)
            string key = dev + "#" + reportId;
            byte[] prev;
            if (lastReport.TryGetValue(key, out prev) && prev.Length == report.Length)
            {
                for (int b = 1; b < report.Length; b++)
                {
                    byte changed = (byte)(prev[b] ^ report[b]);
                    if (changed == 0) continue;
                    for (int bit = 0; bit < 8; bit++)
                    {
                        byte mask = (byte)(1 << bit);
                        if ((changed & mask) == 0) continue;
                        var e = new RawEvent();
                        e.Kind = "bit";
                        e.Pressed = (report[b] & mask) != 0;
                        e.Page = devicePage; e.ReportId = reportId; e.ByteIndex = b; e.BitMask = mask;
                        e.Device = dev;
                        e.Matcher = "bit:" + devicePage.ToString("X4") + "/" + reportId.ToString("X2") + "/" + b + "/" + mask.ToString("X2");
                        Emit(e);
                    }
                }
            }
            lastReport[key] = report;
        }

        void CollectUsages(IntPtr pp, ushort page, byte[] report, HashSet<uint> into)
        {
            int max = HidP_MaxUsageListLength(0, page, pp);
            if (max <= 0) return;
            var buf = new ushort[max];
            int len = max;
            if (HidP_GetUsages(0, page, 0, buf, ref len, pp, report, report.Length) != HIDP_STATUS_SUCCESS) return;
            for (int i = 0; i < len; i++)
                if (buf[i] != 0) into.Add((uint)page << 16 | buf[i]);
        }

        void EmitUsage(string dev, uint packed, bool pressed)
        {
            var e = new RawEvent();
            e.Kind = "usage";
            e.Pressed = pressed;
            e.Page = (ushort)(packed >> 16);
            e.Usage = (ushort)(packed & 0xFFFF);
            e.Device = dev;
            e.Matcher = "usage:" + e.Page.ToString("X4") + "/" + e.Usage.ToString("X4");
            Emit(e);
        }

        public void Dispose()
        {
            if (powerNotify != IntPtr.Zero)
            {
                try { UnregisterPowerSettingNotification(powerNotify); } catch { }
                powerNotify = IntPtr.Zero;
            }
            foreach (var p in preparsed.Values) if (p != IntPtr.Zero) Marshal.FreeHGlobal(p);
            preparsed.Clear();
            if (Handle != IntPtr.Zero) DestroyHandle();
        }
    }
}
