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

    /// Optional switches, e.g. for shortcuts: WARP Switch.exe --connect
    public enum Command { Show = 0, Connect = 1, Disconnect = 2, HotspotOn = 3, HotspotOff = 4, Quit = 5 }

    static Command Parse(string[] args)
    {
        foreach (var a in args)
            switch (a.ToLowerInvariant())
            {
                case "--connect": return Command.Connect;
                case "--disconnect": return Command.Disconnect;
                case "--hotspot-on": return Command.HotspotOn;
                case "--hotspot-off": return Command.HotspotOff;
                case "--quit": return Command.Quit;
            }
        return Command.Show;
    }

    [STAThread]
    static void Main(string[] args)
    {
        var command = Parse(args);

        // Already running (maybe hidden in the tray)? Hand it the request instead of starting another copy.
        if (InstanceRunning())
        {
            Native.PostMessage(Native.HWND_BROADCAST, ShowMessage, (IntPtr)(int)command, IntPtr.Zero);
            return;
        }
        if (command is Command.Disconnect or Command.HotspotOff or Command.Quit) return; // nothing running, nothing to stop

        // The tunnel adapter, routes and DNS all need admin rights: relaunch elevated (one UAC prompt).
        if (!IsAdmin())
        {
            try { Process.Start(new ProcessStartInfo(Application.ExecutablePath, string.Join(" ", args)) { UseShellExecute = true, Verb = "runas" }); }
            catch (Win32Exception) { } // UAC declined
            return;
        }

        using var mutex = new Mutex(true, MutexName, out bool created);
        if (!created)
        {
            Native.PostMessage(Native.HWND_BROADCAST, ShowMessage, IntPtr.Zero, IntPtr.Zero);
            return;
        }

        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.ThreadException += (s, e) => Crash(e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (s, e) => Crash(e.ExceptionObject as Exception);
        Application.Run(new MainForm(startHidden: args.Contains("--tray") || command != Command.Show, command));
    }

    static bool InstanceRunning()
    {
        try { Mutex.OpenExisting(MutexName).Dispose(); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
        catch (UnauthorizedAccessException) { return true; } // owned by the elevated copy
    }

    static bool IsAdmin() => new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    static void Crash(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Setup.Dir);
            File.AppendAllText(Path.Combine(Setup.Dir, "crash.log"), DateTime.Now + "  " + ex + Environment.NewLine + Environment.NewLine);
        }
        catch { }
        MessageBox.Show("Something went wrong:\n\n" + ex?.Message + "\n\nDetails were saved to crash.log in the app folder.", Name, MessageBoxButtons.OK, MessageBoxIcon.Error);
    }
}
