using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BrightBridge
{
    // One VCP control (brightness, contrast, ...) on one monitor.
    class VcpState
    {
        public int Current = -1;   // last value we know the monitor holds; -1 = not read yet
        public int Target = -1;    // value we want it to hold; -1 = nothing pending
        public int Max = 100;
        public bool Supported = true;
        public int PendingDelta;   // accumulated while Current was still unknown
        public int Failures;       // consecutive I/O failures; reset by a success or a rescan
    }

    class MonitorEntry
    {
        public IntPtr Handle;
        public string Description;
        public string Device;
        public Dictionary<byte, VcpState> Vcp = new Dictionary<byte, VcpState>();

        public VcpState State(byte code)
        {
            VcpState s;
            if (!Vcp.TryGetValue(code, out s)) { s = new VcpState(); Vcp[code] = s; }
            return s;
        }
    }

    // DDC/CI transport. Every monitor call is slow (~55 ms round trip on the G3223Q),
    // so all of it happens on the worker thread and rapid key presses coalesce into
    // one write of the latest value rather than queueing up.
    class DdcEngine : IDisposable
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PHYSICAL_MONITOR
        {
            public IntPtr hPhysicalMonitor;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string szPhysicalMonitorDescription;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int left, top, right, bottom; }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct MONITORINFOEX
        {
            public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szDevice;
        }

        delegate bool MonitorEnumProc(IntPtr hMon, IntPtr hdc, IntPtr lprc, IntPtr data);

        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc cb, IntPtr data);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfoW(IntPtr hMon, ref MONITORINFOEX mi);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMon, ref uint count);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMon, uint count, [Out] PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool DestroyPhysicalMonitors(uint count, PHYSICAL_MONITOR[] arr);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetVCPFeatureAndVCPFeatureReply(IntPtr h, byte code, out uint type, out uint cur, out uint max);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool SetVCPFeature(IntPtr h, byte code, uint value);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool GetCapabilitiesStringLength(IntPtr h, out uint len);
        [DllImport("dxva2.dll", SetLastError = true)] static extern bool CapabilitiesRequestAndCapabilitiesReply(IntPtr h, StringBuilder sb, uint len);

        // A monitor waking, sleeping or blanking can leave DDC/CI calls blocked for
        // seconds. Ten attempts at roughly one per second rides out a wake-up
        // without writing a display off as permanently broken.
        const int MaxFailures = 10;

        readonly object gate = new object();
        List<MonitorEntry> monitors = new List<MonitorEntry>();
        readonly HashSet<byte> primedCodes = new HashSet<byte>();
        readonly Thread worker;
        readonly AutoResetEvent wake = new AutoResetEvent(false);
        volatile bool running = true;
        volatile bool rescanRequested;
        string filter;

        public event Action<string> Log;

        public DdcEngine(string targetFilter)
        {
            filter = targetFilter == null ? "" : targetFilter.Trim();
            RescanCore();   // safe here: the worker does not exist yet
            worker = new Thread(WorkerLoop);
            worker.IsBackground = true;
            worker.Name = "ddc-worker";
            worker.Start();
        }

        void Trace(string msg) { var h = Log; if (h != null) h(msg); }

        public List<MonitorEntry> Monitors { get { lock (gate) return new List<MonitorEntry>(monitors); } }

        bool Matches(MonitorEntry m)
        {
            if (filter.Length == 0) return true;
            return m.Description != null && m.Description.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// Ask for a rescan. Deliberately does not do one: enumerating and
        /// releasing physical monitors blocks for seconds while a display is
        /// powering down, and doing that on the UI thread freezes the message
        /// pump. Windows then kills the process as unresponsive during the power
        /// transition - logged as "Hang type: Quiesce". The worker owns every
        /// monitor handle from birth to death, and nothing else touches them.
        /// </summary>
        public void Rescan()
        {
            rescanRequested = true;
            wake.Set();
        }

        /// <summary>The system changed under us - drop everything and re-enumerate.</summary>
        public void Resync(string reason)
        {
            Trace("resync: " + reason);
            Rescan();
        }

        void RescanCore()
        {
            var found = new List<MonitorEntry>();
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, delegate(IntPtr hMon, IntPtr hdc, IntPtr lprc, IntPtr data)
            {
                var mi = new MONITORINFOEX();
                mi.cbSize = Marshal.SizeOf(typeof(MONITORINFOEX));
                GetMonitorInfoW(hMon, ref mi);
                uint n = 0;
                if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMon, ref n) || n == 0) return true;
                var arr = new PHYSICAL_MONITOR[n];
                if (!GetPhysicalMonitorsFromHMONITOR(hMon, n, arr)) return true;
                foreach (var pm in arr)
                {
                    var e = new MonitorEntry();
                    e.Handle = pm.hPhysicalMonitor;
                    e.Description = pm.szPhysicalMonitorDescription;
                    e.Device = mi.szDevice;
                    found.Add(e);
                }
                return true;
            }, IntPtr.Zero);

            List<MonitorEntry> old;
            lock (gate)
            {
                old = monitors;
                monitors = found;
                // Fresh entries carry fresh VcpState, so a monitor written off as
                // unsupported during a wake-up gets a clean hearing here.
                foreach (var m in found)
                    if (Matches(m))
                        foreach (var code in primedCodes) m.State(code);
            }
            foreach (var m in old)
            {
                var one = new PHYSICAL_MONITOR[1];
                one[0].hPhysicalMonitor = m.Handle;
                try { DestroyPhysicalMonitors(1, one); } catch { }
            }
            Trace("rescan: " + found.Count + " physical monitor(s)");
        }

        public string ReadCapabilities(MonitorEntry m)
        {
            uint len;
            if (!GetCapabilitiesStringLength(m.Handle, out len) || len == 0) return null;
            var sb = new StringBuilder((int)len + 1);
            if (!CapabilitiesRequestAndCapabilitiesReply(m.Handle, sb, len)) return null;
            return sb.ToString();
        }

        // Synchronous read - only for CLI paths, never from the UI thread.
        public bool ReadVcp(MonitorEntry m, byte code, out int current, out int max)
        {
            uint type, cur, mx;
            current = 0; max = 0;
            if (!GetVCPFeatureAndVCPFeatureReply(m.Handle, code, out type, out cur, out mx)) return false;
            current = (int)cur; max = (int)mx;
            return true;
        }

        public bool WriteVcp(MonitorEntry m, byte code, int value)
        {
            return SetVCPFeature(m.Handle, code, (uint)value);
        }

        // Move to the next multiple of grid rather than adding blindly, so a
        // monitor sitting on 18 lands on 20 / 30 / 40 like the Windows flyout
        // instead of tracking its own offset 28 / 38 / 48 forever.
        static int StepOnGrid(int from, int direction, int grid)
        {
            if (direction > 0) return ((int)Math.Floor(from / (double)grid) + 1) * grid;
            return ((int)Math.Ceiling(from / (double)grid) - 1) * grid;
        }

        /// <summary>
        /// Queue an adjustment on every matching monitor. Returns the resulting
        /// percentage for the OSD, or -1 if no value is known yet.
        /// </summary>
        /// <param name="snapTo">grid to land on, or 0 to add <paramref name="amount"/> as-is</param>
        public int Adjust(byte code, int amount, bool absolute, int snapTo)
        {
            int osd = -1;
            lock (gate)
            {
                foreach (var m in monitors)
                {
                    if (!Matches(m)) continue;
                    var st = m.State(code);
                    if (!st.Supported) continue;

                    if (st.Current < 0)
                    {
                        // Not primed yet: remember the intent, the worker applies it after the read.
                        if (absolute) { st.Target = amount; st.PendingDelta = 0; }
                        else st.PendingDelta += amount;
                        continue;
                    }

                    int baseline = st.Target >= 0 ? st.Target : st.Current;
                    int next;
                    if (absolute) next = amount;
                    else if (snapTo > 0) next = StepOnGrid(baseline, amount, snapTo);
                    else next = baseline + amount;
                    if (next < 0) next = 0;
                    if (next > st.Max) next = st.Max;
                    st.Target = next;
                    if (osd < 0) osd = st.Max > 0 ? (int)Math.Round(next * 100.0 / st.Max) : next;
                }
            }
            wake.Set();
            return osd;
        }

        public int KnownValue(byte code)
        {
            lock (gate)
            {
                foreach (var m in monitors)
                {
                    if (!Matches(m)) continue;
                    var st = m.State(code);
                    int v = st.Target >= 0 ? st.Target : st.Current;
                    if (v >= 0) return st.Max > 0 ? (int)Math.Round(v * 100.0 / st.Max) : v;
                }
            }
            return -1;
        }

        /// <summary>Prime a control so the first key press reacts instantly.</summary>
        public void Prime(byte code)
        {
            lock (gate)
            {
                primedCodes.Add(code);
                foreach (var m in monitors)
                    if (Matches(m)) m.State(code);
            }
            wake.Set();
        }

        void WorkerLoop()
        {
            while (running)
            {
                wake.WaitOne(1000);
                if (!running) break;

                if (rescanRequested)
                {
                    rescanRequested = false;
                    RescanCore();
                }

                List<MonitorEntry> snapshot;
                lock (gate) snapshot = new List<MonitorEntry>(monitors);

                foreach (var m in snapshot)
                {
                    if (!Matches(m)) continue;

                    List<byte> codes;
                    lock (gate) codes = new List<byte>(m.Vcp.Keys);

                    foreach (var code in codes)
                    {
                        VcpState st;
                        lock (gate) st = m.State(code);
                        if (!st.Supported) continue;

                        bool needRead;
                        lock (gate) needRead = st.Current < 0;
                        if (needRead)
                        {
                            int cur, max;
                            bool ok = ReadVcp(m, code, out cur, out max);
                            lock (gate)
                            {
                                if (!ok)
                                {
                                    // A display that is asleep, blanked or still
                                    // waking fails here and recovers moments later.
                                    // Latching it off on the first failure is what
                                    // left a monitor dead until a manual rescan.
                                    st.Failures++;
                                    if (st.Failures >= MaxFailures)
                                    {
                                        st.Supported = false;
                                        Trace("vcp 0x" + code.ToString("X2") + " not answering on " + m.Description
                                              + " after " + st.Failures + " tries; retrying on next resync");
                                    }
                                    continue;
                                }
                                if (st.Failures > 0)
                                    Trace("vcp 0x" + code.ToString("X2") + " recovered on " + m.Description
                                          + " after " + st.Failures + " failure(s)");
                                st.Failures = 0;
                                st.Current = cur;
                                st.Max = max <= 0 ? 100 : max;
                                if (st.PendingDelta != 0)
                                {
                                    int next = (st.Target >= 0 ? st.Target : st.Current) + st.PendingDelta;
                                    st.PendingDelta = 0;
                                    if (next < 0) next = 0;
                                    if (next > st.Max) next = st.Max;
                                    st.Target = next;
                                }
                            }
                        }

                        int want, have;
                        lock (gate) { want = st.Target; have = st.Current; }
                        if (want < 0 || want == have) continue;

                        bool written = WriteVcp(m, code, want);
                        lock (gate)
                        {
                            if (written)
                            {
                                st.Failures = 0;
                                st.Current = want;
                                // Only clear the target if nothing new arrived while we wrote.
                                if (st.Target == want) st.Target = -1;
                            }
                            else
                            {
                                Trace("write vcp 0x" + code.ToString("X2") + " = " + want + " FAILED on " + m.Description);
                                st.Target = -1;
                                st.Current = -1;   // force a re-read; the handle may be stale
                                st.Failures++;
                                if (st.Failures >= MaxFailures) st.Supported = false;
                            }
                        }
                    }
                }
            }
        }

        public void Dispose()
        {
            running = false;
            wake.Set();

            // Only reclaim handles once the worker has certainly stopped using
            // them. If it is still blocked in a slow DDC call, leave them to the
            // OS rather than free memory out from under it.
            bool stopped = false;
            try { stopped = worker == null || worker.Join(5000); } catch { }
            if (!stopped) { Trace("worker still busy at shutdown; leaving monitor handles to the OS"); return; }

            List<MonitorEntry> old;
            lock (gate) { old = monitors; monitors = new List<MonitorEntry>(); }
            foreach (var m in old)
            {
                var one = new PHYSICAL_MONITOR[1];
                one[0].hPhysicalMonitor = m.Handle;
                try { DestroyPhysicalMonitors(1, one); } catch { }
            }
        }
    }
}
