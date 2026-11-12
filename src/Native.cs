using System;
using System.Drawing;
using System.Net;
using System.Runtime.InteropServices;

namespace WarpSwitch;

static class Native
{
    public static readonly IntPtr HWND_BROADCAST = (IntPtr)0xffff;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int RegisterWindowMessage(string msg);
    [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll")] static extern bool ChangeWindowMessageFilterEx(IntPtr hWnd, int msg, int action, IntPtr changeInfo);
    [DllImport("dnsapi.dll")] public static extern bool DnsFlushResolverCache();
    [DllImport("iphlpapi.dll")] static extern int GetBestInterface(uint destAddr, out uint bestIfIndex);
    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    [DllImport("iphlpapi.dll")] static extern int GetIpInterfaceEntry(IntPtr row);

    /// Per-interface IPv4 forwarding flag (what Get-NetIPInterface shows). .NET's
    /// IPv4InterfaceProperties.IsForwardingEnabled doesn't report this correctly.
    public static bool ForwardingEnabled(int ifIndex)
    {
        // MIB_IPINTERFACE_ROW: Family @0 (AF_INET), InterfaceIndex @16, ForwardingEnabled @41.
        IntPtr row = Marshal.AllocHGlobal(512);
        try
        {
            for (int i = 0; i < 512; i += 8) Marshal.WriteInt64(row, i, 0);
            Marshal.WriteInt16(row, 0, 2);
            Marshal.WriteInt32(row, 16, ifIndex);
            return GetIpInterfaceEntry(row) == 0 && Marshal.ReadByte(row, 41) != 0;
        }
        finally { Marshal.FreeHGlobal(row); }
    }

    /// Lets a non-elevated second launch tell this (elevated) window to show itself.
    public static void AllowMessage(IntPtr hwnd, int msg) => ChangeWindowMessageFilterEx(hwnd, msg, 1 /* MSGFLT_ALLOW */, IntPtr.Zero);

    /// Index of the interface Windows would use to reach <paramref name="ip"/>, or -1.
    public static int BestInterface(IPAddress ip)
    {
        uint addr = BitConverter.ToUInt32(ip.GetAddressBytes(), 0);
        return GetBestInterface(addr, out uint idx) == 0 ? (int)idx : -1;
    }

    public static void StyleTitleBar(IntPtr hwnd, bool dark, Color caption)
    {
        int on = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref on, 4);        // DWMWA_USE_IMMERSIVE_DARK_MODE
        int rgb = caption.R | (caption.G << 8) | (caption.B << 16);
        DwmSetWindowAttribute(hwnd, 35, ref rgb, 4);       // DWMWA_CAPTION_COLOR (Windows 11; ignored on 10)
        DwmSetWindowAttribute(hwnd, 34, ref rgb, 4);       // DWMWA_BORDER_COLOR: no bright outline around the dark window
    }

    // ---- job object: usque dies with this app even if the app crashes or is killed ----
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
