using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;

namespace WarpSwitch;

static class Program
{
    public const string Name = "WARP Switch";
    const string MutexName = @"Local\WarpSwitch.Instance.v1";
    public static readonly int ShowMessage = Native.RegisterWindowMessage("WarpSwitch.Show.v1");
    public static string Version => Updater.Current.ToString(3);

    /// Undoes this app's network changes if it's about to die (set by the main window).
    public static Action EmergencyCleanup;

    /// Optional switches, e.g. for shortcuts: WARP Switch.exe --connect. The numbers are part of the protocol
    /// between copies (and the installer sends Quit), so they never change.
    public enum Command { Show = 0, Connect = 1, Disconnect = 2, HotspotOn = 3, HotspotOff = 4, Quit = 5 }

    sealed class Options
    {
        public Command Command = Command.Show;
        public bool Tray, Updated, Uninstall, SetAutostart;
    }

    static Options Parse(string[] args)
    {
        var o = new Options();
        foreach (var a in args)
            switch (a.ToLowerInvariant())
            {
                case "--connect": o.Command = Command.Connect; break;
                case "--disconnect": o.Command = Command.Disconnect; break;
                case "--hotspot-on": o.Command = Command.HotspotOn; break;
                case "--hotspot-off": o.Command = Command.HotspotOff; break;
                case "--quit": o.Command = Command.Quit; break;
                case "--tray": o.Tray = true; break;
                case "--updated": o.Updated = true; break;
                case "--uninstall": o.Uninstall = true; break;
                case "--set-autostart": o.SetAutostart = true; break;
            }
        return o;
    }

    [STAThread]
    static int Main(string[] args)
    {
        var o = Parse(args);

        // Jobs for the installer, run elevated and without a window.
        if (o.Uninstall || o.SetAutostart)
        {
            if (!IsAdmin()) return Elevate(args, wait: true);
            try
            {
                if (o.Uninstall) Uninstall();
                else Autostart.Set(true);
                return 0;
            }
            catch (Exception ex)
            {
                Log("maintenance", ex);
                return 1;
            }
        }

        // Already running (maybe hidden in the tray)? Hand it the request instead of starting another copy.
        if (InstanceRunning())
        {
            Native.PostMessage(Native.HWND_BROADCAST, ShowMessage, (IntPtr)(int)o.Command, IntPtr.Zero);
            return 0;
        }
        if (o.Command is Command.Disconnect or Command.HotspotOff or Command.Quit) return 0; // nothing running, nothing to stop

        // The tunnel adapter, routes and DNS all need admin rights: relaunch elevated (one UAC prompt).
        if (!IsAdmin()) return Elevate(args, wait: false);

        using var mutex = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            Native.PostMessage(Native.HWND_BROADCAST, ShowMessage, (IntPtr)(int)o.Command, IntPtr.Zero);
            return 0;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (s, e) => Crash(e.Exception, fatal: false);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception, fatal: e.IsTerminating);
        Application.Run(new MainForm(startHidden: o.Tray || o.Command != Command.Show, o.Command, o.Updated));
        return 0;
    }

    static bool InstanceRunning()
    {
        try { Mutex.OpenExisting(MutexName).Dispose(); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; } // owned by the elevated copy
    }

    static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static int Elevate(string[] args, bool wait)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(Application.ExecutablePath, string.Join(" ", args.Select(Quote))) { UseShellExecute = true, Verb = "runas" });
            if (!wait || p == null) return 0;
            p.WaitForExit();
            return p.ExitCode;
        }
        catch (Win32Exception) { return 1; } // UAC declined
    }

    static string Quote(string a) => a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0 ? a : "\"" + a.Replace("\"", "\\\"") + "\"";

    /// Run by the uninstaller: stop a running copy (it disconnects and puts the network back), then remove the
    /// sign-in task, any leftover network changes, and the admin-only helper folder.
    static void Uninstall()
    {
        if (InstanceRunning())
        {
            Native.PostMessage(Native.HWND_BROADCAST, ShowMessage, (IntPtr)(int)Command.Quit, IntPtr.Zero);
            for (int i = 0; i < 120 && InstanceRunning(); i++) Thread.Sleep(250);
        }
        try { Autostart.Set(false); } catch { }
        try { KillSwitch.Remove(); } catch { }
        try { if (Recovery.Pending) Recovery.Restore(); } catch { }
        foreach (var p in Process.GetProcessesByName("usque"))
        {
            try { if (p.MainModule?.FileName?.StartsWith(Setup.BinDir, StringComparison.OrdinalIgnoreCase) == true) p.Kill(); } catch { }
            p.Dispose();
        }
        try { Directory.Delete(Setup.SecureDir, true); } catch { }
    }

    static void Log(string what, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Setup.Dir);
            File.AppendAllText(Path.Combine(Setup.Dir, "crash.log"), DateTime.Now + "  " + what + ": " + ex + Environment.NewLine + Environment.NewLine);
        }
        catch { }
    }

    static int crashing;

    static void Crash(Exception ex, bool fatal)
    {
        Log(fatal ? "fatal" : "error", ex);
        // About to die: don't leave DNS pointed at Cloudflare or the kill switch blocking everything until the
        // next launch (which may be days away if the app isn't set to start with Windows).
        if (fatal && Interlocked.Exchange(ref crashing, 1) == 0)
            try { EmergencyCleanup?.Invoke(); } catch { }
        try
        {
            MessageBox.Show("Something went wrong:\n\n" + ex?.Message + "\n\n" + (fatal ? "WARP Switch has to close; your normal internet settings were put back. " : "")
                + "Details were saved to crash.log in the app folder.", Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch { }
    }
}
