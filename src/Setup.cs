using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

namespace WarpSwitch;

/// Unpacks the embedded usque + wintun and makes sure this PC has a WARP registration.
static class Setup
{
    /// Per-user: the WARP registration, settings, learned routes and logs.
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarpSwitch");

    /// Machine-wide and admin-only: the helper programs this app runs as administrator, the record of network
    /// changes to undo after a crash, and downloaded updates. (They used to live in the per-user folder, where
    /// any program running as you could swap usque.exe and get it run with admin rights.)
    public static readonly string SecureDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "WARP Switch");

    public static string BinDir => Path.Combine(SecureDir, "bin");
    public static string UsquePath => Path.Combine(BinDir, "usque.exe");
    public static string ConfigPath => Path.Combine(Dir, "config.json");
    public static string RouteConfigPath => Path.Combine(Dir, "route.json");

    static readonly (string Resource, string File)[] Binaries = { ("usque.exe.gz", "usque.exe"), ("wintun.dll.gz", "wintun.dll") };
    static readonly Dictionary<string, string> expected = new();
    static readonly object gate = new();

    /// This exe sits where only administrators can change it (installed under Program Files).
    public static bool IsInstalled => IsProtectedPath(Application.ExecutablePath);

    public static bool IsProtectedPath(string path)
    {
        string full;
        try { full = Path.GetFullPath(path); } catch { return false; }
        foreach (var root in new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        })
        {
            if (string.IsNullOrEmpty(root)) continue;
            if (full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public static void EnsureBinaries()
    {
        lock (gate)
        {
            SecureFolder.Ensure(SecureDir);
            Directory.CreateDirectory(BinDir);
            foreach (var (resource, name) in Binaries)
            {
                string path = Path.Combine(BinDir, name);
                if (File.Exists(path) && HashFile(path) == Expected(resource)) continue;
                Extract(resource, path);
                if (HashFile(path) != Expected(resource))
                    throw new UserError("Couldn't set up WARP's helper files (" + name + " failed its integrity check).");
            }
            // Copies from older versions, in the per-user folder, are no longer used.
            foreach (var old in new[] { "usque.exe", "wintun.dll", "bin.stamp" })
                try { File.Delete(Path.Combine(Dir, old)); } catch { }
        }
    }

    /// Opens both helpers so nothing can replace them while usque starts and loads wintun, after checking they're
    /// exactly the files this build ships. Keep it until usque has exited.
    public static IDisposable LockBinaries()
    {
        var held = new List<FileStream>();
        try
        {
            foreach (var (resource, name) in Binaries)
            {
                var fs = new FileStream(Path.Combine(BinDir, name), FileMode.Open, FileAccess.Read, FileShare.Read);
                held.Add(fs);
                if (Hash(fs) != Expected(resource))
                    throw new UserError("WARP's helper files were changed by another program. Connect again to restore them.");
            }
            return new Held(held);
        }
        catch (IOException)
        {
            foreach (var s in held) s.Dispose();
            throw new UserError("WARP's helper files are missing or in use. Connect again to restore them.");
        }
        catch
        {
            foreach (var s in held) s.Dispose();
            throw;
        }
    }

    sealed class Held : IDisposable
    {
        List<FileStream> streams;
        public Held(List<FileStream> s) => streams = s;
        public void Dispose()
        {
            var s = Interlocked.Exchange(ref streams, null);
            if (s != null) foreach (var f in s) f.Dispose();
        }
    }

    /// SHA-256 of what the embedded resource unpacks to: the only version of each helper we'll run.
    static string Expected(string resource)
    {
        lock (expected)
        {
            if (expected.TryGetValue(resource, out var h)) return h;
            using var src = Resource(resource);
            using var gz = new GZipStream(src, CompressionMode.Decompress);
            return expected[resource] = Hash(gz);
        }
    }

    static Stream Resource(string name) => typeof(Setup).Assembly.GetManifestResourceStream(name)
        ?? throw new UserError("This copy of WARP Switch is damaged (" + name + " is missing). Download it again.");

    public static string Hash(Stream s)
    {
        using var sha = SHA256.Create();
        if (s.CanSeek) s.Position = 0;
        return BitConverter.ToString(sha.ComputeHash(s)).Replace("-", "");
    }

    public static string HashFile(string path)
    {
        using var f = File.OpenRead(path);
        return Hash(f);
    }

    static void Extract(string resource, string dest)
    {
        using var src = Resource(resource);
        using var gz = new GZipStream(src, CompressionMode.Decompress);
        string tmp = dest + ".new";
        using (var f = File.Create(tmp)) gz.CopyTo(f);
        File.Copy(tmp, dest, true);
        File.Delete(tmp);
    }

    /// Reuse the registration made by the older .bat installer instead of creating a second WARP device.
    public static void MigrateOldConfig()
    {
        if (File.Exists(ConfigPath)) return;
        string old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "usque", "config.json");
        if (File.Exists(old)) File.Copy(old, ConfigPath);
    }

    public static void Register(CancellationToken cancel)
    {
        Directory.CreateDirectory(Dir);
        string output = "";
        for (int attempt = 0; attempt < 2 && !File.Exists(ConfigPath); attempt++)
        {
            if (attempt > 0 && cancel.WaitHandle.WaitOne(3000)) cancel.ThrowIfCancellationRequested();   // flaky networks: one more go
            output = Shell.Capture(UsquePath, "register -a -n \"" + Environment.MachineName + "\"", Dir, 60000, cancel).Output;
        }
        if (!File.Exists(ConfigPath))
        {
            string last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
            throw new UserError("Couldn't register this PC with WARP. " + Regex.Replace(last, @"^\S+ \S+ \S+ ", ""));
        }
    }

    /// Set the current registration aside (as config.json.bak) so the next connect makes a fresh one.
    public static void ResetRegistration()
    {
        if (File.Exists(ConfigPath))
        {
            string bak = ConfigPath + ".bak";
            File.Copy(ConfigPath, bak, true);
            File.Delete(ConfigPath);
        }
        DropRouteConfig();
    }

    public static string ReadEndpoint()
    {
        var m = Regex.Match(File.ReadAllText(ConfigPath), "\"endpoint_v4\"\\s*:\\s*\"([0-9.]+)\"");
        return m.Success ? m.Groups[1].Value : "162.159.198.2";
    }

    /// The endpoint's pinned public key (the EC point at the end of the PEM), so route probes can tell a real
    /// WARP server from a network box pretending to be one.
    public static byte[] ReadPinnedKey()
    {
        try
        {
            var m = Regex.Match(File.ReadAllText(ConfigPath), "BEGIN PUBLIC KEY-----(.*?)-----END", RegexOptions.Singleline);
            if (!m.Success) return null;
            var der = Convert.FromBase64String(Regex.Replace(m.Groups[1].Value.Replace("\\n", ""), @"\s", ""));
            return der.Length >= 65 ? der.Skip(der.Length - 65).ToArray() : null;
        }
        catch { return null; }
    }

    /// usque reads the server address from its config, so an alternate server gets a copy with just that changed
    /// (kept next to config.json, and deleted when WARP stops).
    public static string ConfigFor(string ip)
    {
        string json = File.ReadAllText(ConfigPath);
        if (Regex.IsMatch(json, "\"endpoint_h2_v4\"\\s*:\\s*\"" + Regex.Escape(ip) + "\"") && ReadEndpoint() == ip) return ConfigPath;
        json = Regex.Replace(json, "(\"endpoint(?:_h2)?_v4\"\\s*:\\s*\")[^\"]*(\")", "${1}" + ip + "${2}");
        File.WriteAllText(RouteConfigPath, json);
        return RouteConfigPath;
    }

    public static void DropRouteConfig()
    {
        try { File.Delete(RouteConfigPath); } catch { }
    }

    /// Writes through a temporary file, so a crash or power cut mid-write never leaves a half-written file.
    public static void WriteAllLinesAtomic(string path, IEnumerable<string> lines)
    {
        string tmp = path + ".tmp";
        File.WriteAllLines(tmp, lines);
        if (!File.Exists(path)) { File.Move(tmp, path); return; }
        try { File.Replace(tmp, path, null); }
        catch (IOException) { File.Copy(tmp, path, true); File.Delete(tmp); }
    }
}

/// A folder only SYSTEM and administrators can change (everyone can read).
static class SecureFolder
{
    public static void Ensure(string dir)
    {
        var admins = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
        var sec = new DirectorySecurity();
        sec.SetOwner(admins);
        sec.SetAccessRuleProtection(true, false);
        const InheritanceFlags all = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(admins, FileSystemRights.FullControl, all, PropagationFlags.None, AccessControlType.Allow));
        sec.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null), FileSystemRights.ReadAndExecute, all, PropagationFlags.None, AccessControlType.Allow));
        if (!Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir, sec);
            return;
        }
        // Someone may have created it first to keep control of it: take it back. If even that's refused, the
        // SHA-256 checks and locked handles in Setup still stop a swapped helper from being run.
        try { Directory.SetAccessControl(dir, sec); } catch { }
    }
}

sealed class Settings
{
    public bool ConnectOnLaunch, TrayHintShown;
    public bool? AnimationsOverride;          // null = follow Windows' "Animation effects"
    public bool Notifications = true;
    public bool AutoConnectUntrusted;         // connect by itself on networks that aren't in TrustedNetworks
    public bool KillSwitch;
    public bool EncryptDns;                   // Windows' DNS-over-HTTPS for Cloudflare's resolvers
    public bool CheckUpdates = true;
    public int Mtu;                           // 0 = usque's default (1280)
    public List<string> TrustedNetworks = new();
    public List<string> Bypass = new();       // IPv4 addresses, CIDR ranges or host names that skip WARP
    public string SkippedVersion = "";

    public bool Animations => AnimationsOverride ?? Native.ClientAreaAnimation();

    public bool IsTrusted(string network) => network != null && TrustedNetworks.Any(n => string.Equals(n, network, StringComparison.OrdinalIgnoreCase));

    static string DefaultPath => Path.Combine(Setup.Dir, "settings.ini");
    readonly string path;

    Settings(string path) => this.path = path;

    public static Settings Load(string path = null)
    {
        var s = new Settings(path ?? DefaultPath);
        try
        {
            foreach (var line in File.ReadAllLines(s.path))
            {
                var kv = line.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;
                string v = kv[1].Trim();
                bool on = v == "1";
                switch (kv[0].Trim())
                {
                    case "connect_on_launch": s.ConnectOnLaunch = on; break;
                    case "tray_hint_shown": s.TrayHintShown = on; break;
                    case "animations": s.AnimationsOverride = v == "" ? null : on; break;
                    case "notifications": s.Notifications = on; break;
                    case "auto_connect_untrusted": s.AutoConnectUntrusted = on; break;
                    case "kill_switch": s.KillSwitch = on; break;
                    case "encrypt_dns": s.EncryptDns = on; break;
                    case "check_updates": s.CheckUpdates = on; break;
                    case "skipped_version": s.SkippedVersion = v; break;
                    case "mtu": if (int.TryParse(v, out int mtu) && (mtu == 0 || mtu is >= 1280 and <= 1500)) s.Mtu = mtu; break;
                    case "trusted": if (v.Length > 0) s.TrustedNetworks.Add(v); break;
                    case "bypass": if (v.Length > 0) s.Bypass.Add(v); break;
                }
            }
        }
        catch { }
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            var lines = new List<string>
            {
                "connect_on_launch=" + Bit(ConnectOnLaunch),
                "tray_hint_shown=" + Bit(TrayHintShown),
                "animations=" + (AnimationsOverride == null ? "" : Bit(AnimationsOverride.Value)),
                "notifications=" + Bit(Notifications),
                "auto_connect_untrusted=" + Bit(AutoConnectUntrusted),
                "kill_switch=" + Bit(KillSwitch),
                "encrypt_dns=" + Bit(EncryptDns),
                "check_updates=" + Bit(CheckUpdates),
                "skipped_version=" + SkippedVersion,
                "mtu=" + Mtu,
            };
            lines.AddRange(TrustedNetworks.Select(n => "trusted=" + Clean(n)));
            lines.AddRange(Bypass.Select(b => "bypass=" + Clean(b)));
            Setup.WriteAllLinesAtomic(path, lines);
        }
        catch { }
    }

    static string Bit(bool b) => b ? "1" : "0";
    static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Trim();
}

/// "Start with Windows" via a logon task that runs elevated, so there is no UAC prompt at sign-in. Only an
/// installed copy may register it: a task that starts a program from a folder you can write to with admin
/// rights would let any program you run replace that file and get admin rights at your next sign-in.
static class Autostart
{
    const string TaskName = "WARP Switch";

    static dynamic Folder()
    {
        dynamic svc = Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));
        svc.Connect();
        return svc.GetFolder("\\");
    }

    /// The program the logon task starts, or null if there's no task.
    public static string TaskPath()
    {
        try
        {
            dynamic actions = Folder().GetTask(TaskName).Definition.Actions;
            return actions.Count > 0 ? (string)actions.Item(1).Path : "";
        }
        catch (COMException) { return null; }
        catch (FileNotFoundException) { return null; }
    }

    public static bool IsEnabled() => TaskPath() != null;

    public static bool Allowed => Setup.IsInstalled;

    public static void Set(bool on)
    {
        if (!on)
        {
            try { Folder().DeleteTask(TaskName, 0); } catch (COMException) { } catch (FileNotFoundException) { }
            return;
        }
        if (!Allowed) throw new UserError("Start with Windows needs the installed version of WARP Switch (so the sign-in task can't be hijacked). Install it with WarpSwitch-Setup.exe from the releases page.");
        string user = WindowsIdentity.GetCurrent().Name;
        Folder().RegisterTask(TaskName, Xml(user, Application.ExecutablePath), 6 /* TASK_CREATE_OR_UPDATE */, user, null, 3 /* TASK_LOGON_INTERACTIVE_TOKEN */);
    }

    /// An installed copy takes over a logon task that still starts an older, moved or portable copy.
    public static bool RepointIfNeeded()
    {
        string current = TaskPath();
        if (current == null || !Allowed) return false;
        try
        {
            if (string.Equals(Path.GetFullPath(current), Path.GetFullPath(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase)) return false;
        }
        catch (ArgumentException) { }
        Set(true);
        return true;
    }

    static string Xml(string user, string exe)
    {
        user = System.Security.SecurityElement.Escape(user);
        exe = System.Security.SecurityElement.Escape(exe);
        // Laptop-friendly: also starts on battery, and never gets stopped for running "too long".
        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Author>Vortex</Author><Description>Starts WARP Switch in the tray when you sign in.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{user}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id=""Author""><UserId>{user}</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{exe}</Command><Arguments>--tray</Arguments></Exec></Actions>
</Task>";
    }
}
