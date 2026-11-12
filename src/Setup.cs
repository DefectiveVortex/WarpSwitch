using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace WarpSwitch;

/// Unpacks the embedded usque + wintun and makes sure this PC has a WARP registration.
static class Setup
{
    public static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WarpSwitch");
    public static string UsquePath => Path.Combine(Dir, "usque.exe");
    public static string ConfigPath => Path.Combine(Dir, "config.json");
    const string Stamp = "usque 4.2.1 + wintun 0.14.1";
    static readonly object gate = new();

    public static void EnsureBinaries()
    {
        lock (gate)
        {
            Directory.CreateDirectory(Dir);
            string stampFile = Path.Combine(Dir, "bin.stamp");
            string wintun = Path.Combine(Dir, "wintun.dll");
            if (File.Exists(UsquePath) && File.Exists(wintun) && File.Exists(stampFile) && File.ReadAllText(stampFile) == Stamp) return;
            Extract("usque.exe.gz", UsquePath);
            Extract("wintun.dll.gz", wintun);
            File.WriteAllText(stampFile, Stamp);
        }
    }

    static void Extract(string resource, string dest)
    {
        using var src = typeof(Setup).Assembly.GetManifestResourceStream(resource)
            ?? throw new UserError("This copy of WARP Switch is damaged (" + resource + " is missing). Download it again.");
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

    public static void Register()
    {
        string output = "";
        for (int attempt = 0; attempt < 2 && !File.Exists(ConfigPath); attempt++)
        {
            if (attempt > 0) System.Threading.Thread.Sleep(3000);   // flaky networks: one more go before giving up
            output = Shell.Capture(UsquePath, "register -a -n \"" + Environment.MachineName + "\"", Dir, 60000).Output;
        }
        if (!File.Exists(ConfigPath))
        {
            string last = output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0) ?? "";
            throw new UserError("Couldn't register this PC with WARP. " + Regex.Replace(last, @"^\S+ \S+ \S+ ", ""));
        }
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

    public static string RouteConfigPath => Path.Combine(Dir, "route.json");

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
}

sealed class Settings
{
    public bool ConnectOnLaunch, TrayHintShown, Animations = true;
    public int Mtu;   // 0 = automatic
    static string FilePath => Path.Combine(Setup.Dir, "settings.ini");

    public static Settings Load()
    {
        var s = new Settings();
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                var kv = line.Split(new[] { '=' }, 2);
                if (kv.Length != 2) continue;
                string v = kv[1].Trim();
                bool on = v == "1";
                switch (kv[0].Trim())
                {
                    case "connect_on_launch": s.ConnectOnLaunch = on; break;
                    case "tray_hint_shown": s.TrayHintShown = on; break;
                    case "animations": s.Animations = on; break;
                    case "mtu": int.TryParse(v, out s.Mtu); break;
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
            Directory.CreateDirectory(Setup.Dir);
            File.WriteAllLines(FilePath, new[]
            {
                "connect_on_launch=" + (ConnectOnLaunch ? 1 : 0),
                "tray_hint_shown=" + (TrayHintShown ? 1 : 0),
                "animations=" + (Animations ? 1 : 0),
                "mtu=" + Mtu,
            });
        }
        catch { }
    }
}

/// "Start with Windows" via a logon task that runs elevated, so there is no UAC prompt at sign-in.
static class Autostart
{
    const string TaskName = "WARP Switch";

    public static bool IsEnabled() => Shell.Run("schtasks.exe", "/Query /TN \"" + TaskName + "\"") == 0;

    public static void Set(bool on)
    {
        if (!on)
        {
            Shell.Run("schtasks.exe", "/Delete /F /TN \"" + TaskName + "\"");
            return;
        }
        string user = SecurityElement.Escape(WindowsIdentity.GetCurrent().Name);
        string exe = SecurityElement.Escape(System.Windows.Forms.Application.ExecutablePath);
        // Laptop-friendly: also starts on battery, and never gets stopped for running "too long".
        string xml = $@"<?xml version=""1.0"" encoding=""UTF-16""?>
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
        string tmp = Path.Combine(Path.GetTempPath(), "warpswitch-task.xml");
        File.WriteAllText(tmp, xml, Encoding.Unicode);
        try { Shell.Run("schtasks.exe", "/Create /F /TN \"" + TaskName + "\" /XML \"" + tmp + "\""); }
        finally { File.Delete(tmp); }
    }
}
