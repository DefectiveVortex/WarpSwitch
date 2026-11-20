using System;
using System.Collections.Generic;
using System.Drawing;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace WarpSwitch;

static class Native
{
    public static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string msg);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, int msg, int action, IntPtr changeInfo);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilter(int msg, int flag);
    [DllImport("user32.dll")] static extern bool SystemParametersInfo(uint action, uint uiParam, ref bool value, uint winIni);
    [DllImport("dnsapi.dll")] public static extern bool DnsFlushResolverCache();
    [DllImport("iphlpapi.dll")] static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);
    [DllImport("iphlpapi.dll")] static extern int GetBestInterfaceEx(byte[] sockaddr, out uint bestIfIndex);
    [DllImport("iphlpapi.dll")] static extern int GetIpInterfaceEntry(IntPtr row);
    [DllImport("iphlpapi.dll")] static extern int GetIpForwardTable2(ushort family, out IntPtr table);
    [DllImport("iphlpapi.dll")] static extern void FreeMibTable(IntPtr memory);
    [DllImport("iphlpapi.dll")] static extern void InitializeIpForwardEntry(IntPtr row);
    [DllImport("iphlpapi.dll")] static extern int CreateIpForwardEntry2(IntPtr row);
    [DllImport("iphlpapi.dll")] static extern int DeleteIpForwardEntry2(IntPtr row);
    [DllImport("iphlpapi.dll")] static extern int SetInterfaceDnsSettings(Guid iface, IntPtr settings);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public const int ERROR_OBJECT_ALREADY_EXISTS = 5010;

    /// Lets a non-elevated second launch tell this (elevated) window to show itself.
    public static void AllowMessage(IntPtr hwnd, int msg) => ChangeWindowMessageFilterEx(hwnd, msg, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);

    /// Explorer announces a restart with "TaskbarCreated" so tray icons can come back, but Windows filters that
    /// broadcast out of elevated programs unless they opt in. Without this the icon vanishes for good.
    public static void AllowTaskbarCreated() => ChangeWindowMessageFilter(RegisterWindowMessage("TaskbarCreated"), 1 /* MSGFLT_ADD */);

    /// Windows' "Animation effects" accessibility setting.
    public static bool ClientAreaAnimation()
    {
        bool on = true;
        return !SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, ref on, 0) || on;
    }

    /// The display scale this (system-DPI-aware) app runs at: 1.25 at 125 %. Control.DeviceDpi can't be used for
    /// this: on .NET Framework 4.8 it says 96 unless the app opts in to WinForms' own high-DPI mode.
    public static float DisplayScale()
    {
        try
        {
            using var g = Graphics.FromHwnd(IntPtr.Zero);
            return Math.Max(1f, g.DpiX / 96f);
        }
        catch { return 1f; }
    }

    public static void StyleTitleBar(IntPtr hwnd, bool dark, Color caption)
    {
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);        // DWMWA_USE_IMMERSIVE_DARK_MODE
        int rgb = caption.R | (caption.G << 8) | (caption.B << 16);
        DwmSetWindowAttribute(hwnd, 35, ref rgb, 4);       // DWMWA_CAPTION_COLOR (Windows 11; ignored on 10)
        DwmSetWindowAttribute(hwnd, 34, ref rgb, 4);       // DWMWA_BORDER_COLOR: no bright outline around the dark window
    }

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string appName, string idList);

    /// Dark scroll bars on a stock control (a text box), like Explorer's in dark mode.
    public static void DarkScrollBars(IntPtr hwnd)
    {
        try { SetWindowTheme(hwnd, "DarkMode_Explorer", null); } catch { }
    }

    /// Windows 11's rounded window corners for pop-ups that don't get them by default (menus). Ignored on 10.
    public static void RoundCorners(IntPtr hwnd, bool small = true)
    {
        int pref = small ? 3 : 2;                         // DWMWCP_ROUNDSMALL / DWMWCP_ROUND
        DwmSetWindowAttribute(hwnd, 33, ref pref, 4);      // DWMWA_WINDOW_CORNER_PREFERENCE
    }

    // ---------------------------------------------------------------- interfaces

    // MIB_IPINTERFACE_ROW: Family @0, InterfaceIndex @16, ForwardingEnabled @41, Metric @148 (checked against Get-NetIPInterface).
    static T WithInterfaceRow<T>(int ifIndex, AddressFamily family, Func<IntPtr, T> read, T fallback)
    {
        IntPtr row = Marshal.AllocHGlobal(512);
        try
        {
            for (int i = 0; i < 512; i += 8) Marshal.WriteInt64(row, i, 0);
            Marshal.WriteInt16(row, 0, (short)(family == AddressFamily.InterNetworkV6 ? 23 : 2));
            Marshal.WriteInt32(row, 16, ifIndex);
            return GetIpInterfaceEntry(row) == 0 ? read(row) : fallback;
        }
        finally { Marshal.FreeHGlobal(row); }
    }

    /// Per-interface IPv4 forwarding flag (what Get-NetIPInterface shows). .NET's
    /// IPv4InterfaceProperties.IsForwardingEnabled doesn't report this correctly.
    public static bool ForwardingEnabled(int ifIndex) => WithInterfaceRow(ifIndex, AddressFamily.InterNetwork, r => Marshal.ReadByte(r, 41) != 0, false);

    public static int InterfaceMetric(int ifIndex) => WithInterfaceRow(ifIndex, AddressFamily.InterNetwork, r => Marshal.ReadInt32(r, 148), 0);

    /// Index of the interface Windows would use to reach <paramref name="ip"/>, or -1.
    public static int BestInterface(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return GetBestInterface(BitConverter.ToUInt32(ip.GetAddressBytes(), 0), out uint idx) == 0 ? (int)idx : -1;
        var sa = new byte[28];
        sa[0] = 23;   // AF_INET6
        Array.Copy(ip.GetAddressBytes(), 0, sa, 8, 16);
        return GetBestInterfaceEx(sa, out uint idx6) == 0 ? (int)idx6 : -1;
    }

    // ---------------------------------------------------------------- routes (instead of shelling out to route.exe / netsh)

    // MIB_IPFORWARD_ROW2, 104 bytes: InterfaceIndex @8, DestinationPrefix.Prefix @12, .PrefixLength @40,
    // NextHop @44, Metric @84. Offsets checked against Get-NetRoute on a live table.
    const int RowSize = 104, OffIndex = 8, OffDest = 12, OffPrefixLen = 40, OffNextHop = 44, OffMetric = 84;

    public sealed class Route
    {
        public int IfIndex, PrefixLength, Metric;
        public IPAddress Prefix, NextHop;
        public override string ToString() => Prefix + "/" + PrefixLength + " via " + NextHop + " if " + IfIndex;
    }

    static IPAddress ReadSockaddr(IntPtr p)
    {
        ushort fam = (ushort)Marshal.ReadInt16(p);
        var b = new byte[fam == 23 ? 16 : 4];
        Marshal.Copy(p + (fam == 23 ? 8 : 4), b, 0, b.Length);
        return new IPAddress(b);
    }

    static void WriteSockaddr(IntPtr p, IPAddress ip)
    {
        for (int i = 0; i < 28; i += 4) Marshal.WriteInt32(p, i, 0);
        var b = ip.GetAddressBytes();
        bool v6 = ip.AddressFamily == AddressFamily.InterNetworkV6;
        Marshal.WriteInt16(p, 0, (short)(v6 ? 23 : 2));
        Marshal.Copy(b, 0, p + (v6 ? 8 : 4), b.Length);
    }

    static ushort Family(AddressFamily f) => (ushort)(f == AddressFamily.InterNetworkV6 ? 23 : 2);

    /// Walks the routing table; <paramref name="visit"/> gets each route and a pointer to its row (valid during the call).
    static void ForEachRoute(AddressFamily family, Action<Route, IntPtr> visit)
    {
        int rc = GetIpForwardTable2(Family(family), out IntPtr table);
        if (rc != 0) throw new System.ComponentModel.Win32Exception(rc);
        try
        {
            int n = Marshal.ReadInt32(table);
            for (int i = 0; i < n; i++)
            {
                IntPtr row = table + 8 + i * RowSize;
                visit(new Route
                {
                    IfIndex = Marshal.ReadInt32(row, OffIndex),
                    Prefix = ReadSockaddr(row + OffDest),
                    PrefixLength = Marshal.ReadByte(row, OffPrefixLen),
                    NextHop = ReadSockaddr(row + OffNextHop),
                    Metric = Marshal.ReadInt32(row, OffMetric),
                }, row);
            }
        }
        finally { FreeMibTable(table); }
    }

    public static List<Route> Routes(AddressFamily family)
    {
        var list = new List<Route>();
        ForEachRoute(family, (r, _) => list.Add(r));
        return list;
    }

    /// Effective metric (route + interface) of each interface's best IPv4 default route: how Windows ranks uplinks.
    /// Our own routes never count here (they're /1s and host routes, never 0.0.0.0/0).
    public static Dictionary<int, int> DefaultRouteMetrics()
    {
        var best = new Dictionary<int, int>();
        foreach (var r in Routes(AddressFamily.InterNetwork))
        {
            if (r.PrefixLength != 0) continue;
            int m = r.Metric + InterfaceMetric(r.IfIndex);
            if (!best.TryGetValue(r.IfIndex, out int cur) || m < cur) best[r.IfIndex] = m;
        }
        return best;
    }

    /// Adds an active-store (non-persistent) route. Returns a Win32 error code; 0 or ERROR_OBJECT_ALREADY_EXISTS mean it's there.
    public static int AddRoute(IPAddress prefix, int prefixLength, int ifIndex, IPAddress nextHop, int metric)
    {
        IntPtr row = Marshal.AllocHGlobal(RowSize);
        try
        {
            InitializeIpForwardEntry(row);
            Marshal.WriteInt64(row, 0, 0);   // no LUID: Windows uses the index
            Marshal.WriteInt32(row, OffIndex, ifIndex);
            WriteSockaddr(row + OffDest, prefix);
            Marshal.WriteByte(row, OffPrefixLen, (byte)prefixLength);
            WriteSockaddr(row + OffNextHop, nextHop);
            Marshal.WriteInt32(row, OffMetric, metric);
            return CreateIpForwardEntry2(row);
        }
        finally { Marshal.FreeHGlobal(row); }
    }

    /// Deletes every route that matches; returns how many went.
    public static int DeleteRoutes(AddressFamily family, Func<Route, bool> match)
    {
        int n = 0;
        ForEachRoute(family, (r, row) => { if (match(r) && DeleteIpForwardEntry2(row) == 0) n++; });
        return n;
    }

    // ---------------------------------------------------------------- DNS

    /// Static DNS servers for one address family on one adapter (comma-separated); "" goes back to automatic.
    /// Returns a Win32 error code, or -1 where the API doesn't exist (Windows 10 before 2004).
    public static int SetDns(Guid adapter, bool ipv6, string servers)
    {
        // DNS_INTERFACE_SETTINGS (version 1), 64 bytes: Version @0, Flags @8, NameServer @24.
        IntPtr s = Marshal.AllocHGlobal(64);
        IntPtr ns = Marshal.StringToHGlobalUni(servers ?? "");
        try
        {
            for (int i = 0; i < 64; i += 8) Marshal.WriteInt64(s, i, 0);
            Marshal.WriteInt32(s, 0, 1);
            Marshal.WriteInt64(s, 8, 0x2 | (ipv6 ? 0x1 : 0));   // DNS_SETTING_NAMESERVER (| DNS_SETTING_IPV6)
            Marshal.WriteIntPtr(s, 24, ns);
            return SetInterfaceDnsSettings(adapter, s);
        }
        catch (EntryPointNotFoundException) { return -1; }
        finally
        {
            Marshal.FreeHGlobal(ns);
            Marshal.FreeHGlobal(s);
        }
    }

    // ---------------------------------------------------------------- job object: usque dies with this app even if the app crashes or is killed

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr attrs, string name);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int size);
    [DllImport("kernel32.dll")] public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic;
        public IO_COUNTERS Io;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    public static IntPtr CreateKillOnCloseJob()
    {
        IntPtr job = CreateJobObject(IntPtr.Zero, null);
        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
        info.Basic.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        SetInformationJobObject(job, 9 /* ExtendedLimitInformation */, ref info, Marshal.SizeOf(info));
        return job;
    }
}
