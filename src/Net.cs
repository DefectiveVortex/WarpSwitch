using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace WarpSwitch;

/// The real (physical) uplink: Wi-Fi or Ethernet with a gateway.
sealed class Phys
{
    public string Id, Name;
    public int Index;
    public IPAddress Gateway, Address;
    public bool HasIPv6;   // the real network hands out public IPv6 (so apps could skip the tunnel over it)
    public bool SameAs(Phys other) => other != null && other.Id == Id && other.Index == Index && other.Gateway.Equals(Gateway);
}

sealed class TraceInfo
{
    public string Ip = "", Loc = "", Colo = "";
    public bool Warp;
}

static class Net
{
    public const string TunName = "usque";
    static readonly string[] NotPhysical = { "wintun", "wireguard", "tailscale", "zerotier", "openvpn", "tap-windows", "wi-fi direct", "vpn", TunName };
    static readonly IPAddress Probe = IPAddress.Parse("1.1.1.1");

    public static Phys FindPhysical(string endpoint)
    {
        var found = new List<(Phys Phys, NetworkInterfaceType Type)>();
        foreach (var n in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (n.OperationalStatus != OperationalStatus.Up) continue;
            if (n.NetworkInterfaceType is NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) continue;
            string tag = (n.Name + " " + n.Description).ToLowerInvariant();
            if (NotPhysical.Any(tag.Contains)) continue;
            try
            {
                var ip = n.GetIPProperties();
                var v4 = ip.GetIPv4Properties();
                var gw = ip.GatewayAddresses.Select(g => g.Address)
                           .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !a.Equals(IPAddress.Any));
                var addr = ip.UnicastAddresses.Select(u => u.Address).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (v4 == null || gw == null || addr == null) continue;
                bool v6 = ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6)
                          && ip.UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal && !u.Address.IsIPv6SiteLocal);
                found.Add((new Phys { Id = n.Id, Name = n.Name, Index = v4.Index, Gateway = gw, Address = addr, HasIPv6 = v6 }, n.NetworkInterfaceType));
            }
            catch (NetworkInformationException) { }
        }
        if (found.Count == 0) return null;

        // Prefer whatever Windows already routes the endpoint through (that's the active uplink).
        if (IPAddress.TryParse(endpoint, out var ep))
        {
            int best = Native.BestInterface(ep);
            var hit = found.FirstOrDefault(f => f.Phys.Index == best).Phys;
            if (hit != null) return hit;
        }
        return found.OrderBy(f => f.Type == NetworkInterfaceType.Ethernet ? 0 : f.Type == NetworkInterfaceType.Wireless80211 ? 1 : 2).First().Phys;
    }

    public static NetworkInterface Tun() =>
        NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name.Equals(TunName, StringComparison.OrdinalIgnoreCase) && n.OperationalStatus == OperationalStatus.Up);

    public static int IndexOf(NetworkInterface n)
    {
        try { return n.GetIPProperties().GetIPv4Properties()?.Index ?? -1; }
        catch (NetworkInformationException) { return -1; }
    }

    static NetworkInterface ById(string id) => NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == id);

    /// Windows' Mobile Hotspot turns on IPv4 forwarding for the connection it shares, so this tells us
    /// whether hotspot devices are going out through that adapter.
    public static bool Forwarding(string id)
    {
        var n = ById(id);
        int idx = n == null ? -1 : IndexOf(n);
        return idx > 0 && Native.ForwardingEnabled(idx);
    }

    /// Keep the tunnel's own packets on the physical link so they don't loop into the tunnel.
    public static void PinEndpoint(string ep, Phys p)
    {
        Shell.Run("route.exe", "delete " + ep);
        Shell.Run("route.exe", $"add {ep} mask 255.255.255.255 {p.Gateway} metric 1 if {p.Index}");
    }

    public static void UnpinEndpoint(string ep) => Shell.Run("route.exe", "delete " + ep);

    public static bool RoutesActive(int tunIndex) => Native.BestInterface(Probe) == tunIndex;

    /// Two /1 routes beat the normal 0.0.0.0/0 default without deleting it; they vanish with the adapter.
    /// IPv6 goes in too, but only on networks that really have IPv6 (otherwise apps there could quietly skip
    /// the tunnel over it). On IPv4-only networks the tunnel stays IPv4-only, exactly as in version 1, which
    /// keeps Windows' hotspot sharing on its well-tested path.
    public static void RouteAllThrough(NetworkInterface tun, int tunIndex, bool uplinkHasIPv6)
    {
        Shell.Run("netsh.exe", $"interface ipv4 add route prefix=0.0.0.0/1 interface={tunIndex} nexthop=0.0.0.0 store=active");
        Shell.Run("netsh.exe", $"interface ipv4 add route prefix=128.0.0.0/1 interface={tunIndex} nexthop=0.0.0.0 store=active");
        if (!uplinkHasIPv6) return;
        bool v6 = false;
        try { v6 = tun.GetIPProperties().UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal); }
        catch (NetworkInformationException) { }
        if (!v6) return;
        Shell.Run("netsh.exe", $"interface ipv6 add route prefix=::/1 interface={tunIndex} store=active");
        Shell.Run("netsh.exe", $"interface ipv6 add route prefix=8000::/1 interface={tunIndex} store=active");
    }

    static readonly string[] Lan = { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" };

    /// Campus/office/home-LAN addresses (login portals, intranet sites, printers) keep going straight out
    /// the real network at full speed; WARP can't reach private addresses anyway. Returns what was added.
    public static List<string> BypassLan(Phys p)
    {
        var added = new List<string>();
        var existing = Shell.Capture("netsh.exe", "interface ipv4 show route", null, 10000).Output;
        foreach (var prefix in Lan)
        {
            // Never touch a route the network itself handed out for that range.
            if (existing.Split('\n').Any(l => l.Contains(" " + prefix + " "))) continue;
            if (Shell.Run("netsh.exe", $"interface ipv4 add route prefix={prefix} interface={p.Index} nexthop={p.Gateway} metric=5 store=active") == 0)
                added.Add(prefix);
        }
        return added;
    }

    public static void RemoveLanRoute(string prefix, string ifIndex, string gateway) =>
        Shell.Run("netsh.exe", $"interface ipv4 delete route prefix={prefix} interface={ifIndex} nexthop={gateway} store=active");

    static ConnectionProfile ProfileOf(Phys p)
    {
        if (p == null || !Guid.TryParse(p.Id, out var g)) return null;
        try { return NetworkInformation.GetConnectionProfiles().FirstOrDefault(c => c.NetworkAdapter != null && c.NetworkAdapter.NetworkAdapterId == g); }
        catch { return null; }
    }

    /// What Windows calls this network (the Wi-Fi name, or "Network 2" for cables). Used to remember what works where.
    public static string NetworkName(Phys p)
    {
        string name = null;
        try { name = ProfileOf(p)?.ProfileName; } catch { }
        return string.IsNullOrWhiteSpace(name) ? p.Name + " " + p.Gateway : name.Trim();
    }

    /// Windows' own check says this network is holding traffic back for a sign-in page.
    public static bool BehindLoginPage(Phys p)
    {
        try { return ProfileOf(p)?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.ConstrainedInternetAccess; }
        catch { return false; }
    }

    /// The network's own resolver returns poisoned answers, so resolve through Cloudflare instead.
    public static void SetCloudflareDns(int index)
    {
        Shell.Run("netsh.exe", $"interface ipv4 set dnsservers name={index} source=static address=1.1.1.1 register=none validate=no");
        Shell.Run("netsh.exe", $"interface ipv4 add dnsservers name={index} address=1.0.0.1 index=2 validate=no");
    }

    public static string ReadStaticDns(string id)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\" + id);
        return ((key?.GetValue("NameServer") as string) ?? "").Trim();
    }

    public static bool IsCloudflareDns(string value) => string.Join(",", Split(value)) == "1.1.1.1,1.0.0.1";

    static string[] Split(string servers) => (servers ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

    public static void RestoreDns(string id, string original)
    {
        var n = ById(id);
        int idx = n == null ? -1 : IndexOf(n);
        if (idx < 0) return;
        var servers = Split(original);
        if (servers.Length == 0)
        {
            Shell.Run("netsh.exe", $"interface ipv4 set dnsservers name={idx} source=dhcp");
            return;
        }
        Shell.Run("netsh.exe", $"interface ipv4 set dnsservers name={idx} source=static address={servers[0]} register=primary validate=no");
        for (int i = 1; i < servers.Length; i++)
            Shell.Run("netsh.exe", $"interface ipv4 add dnsservers name={idx} address={servers[i]} index={i + 1} validate=no");
    }

    /// Cheap liveness check: can a TCP handshake to Cloudflare DNS complete through the tunnel?
    public static async Task<bool> ProbeAsync()
    {
        foreach (var ip in new[] { "1.1.1.1", "1.0.0.1" })
        {
            using var c = new TcpClient();
            var connect = c.ConnectAsync(ip, 443);
            _ = connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (await Task.WhenAny(connect, Task.Delay(5000)).ConfigureAwait(false) == connect && connect.Status == TaskStatus.RanToCompletion)
                return true;
        }
        return false;
    }

    /// Cloudflare's "what do you see" page. Asked by IPv4 address normally (no DNS needed, so it answers the moment
    /// the tunnel is up, and it reports the IPv4 address people see), or by name to also prove DNS works.
    public static async Task<TraceInfo> TraceAsync(string host = "1.1.1.1")
    {
        bool byName = !IPAddress.TryParse(host, out _);
        using var handler = new HttpClientHandler { UseProxy = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(byName ? 8 : 4) };
        string body = await http.GetStringAsync("https://" + host + "/cdn-cgi/trace").ConfigureAwait(false);
        var t = new TraceInfo();
        foreach (var line in body.Split('\n'))
        {
            int i = line.IndexOf('=');
            if (i <= 0) continue;
            string v = line.Substring(i + 1).Trim();
            switch (line.Substring(0, i))
            {
                case "ip": t.Ip = v; break;
                case "loc": t.Loc = v; break;
                case "colo": t.Colo = v; break;
                case "warp": t.Warp = v is "on" or "plus"; break;
            }
        }
        return t;
    }
}

/// Remembers what we changed (endpoint route, DNS) on disk, so a crash or power cut can be undone next launch.
static class Recovery
{
    static string FilePath => Path.Combine(Setup.Dir, "state.ini");
    public static bool Pending => File.Exists(FilePath);

    static Dictionary<string, string> Load()
    {
        var d = new Dictionary<string, string>();
        try
        {
            foreach (var line in File.ReadAllLines(FilePath))
            {
                int i = line.IndexOf('=');
                if (i > 0) d[line.Substring(0, i)] = line.Substring(i + 1);
            }
        }
        catch (IOException) { }
        return d;
    }

    static readonly object gate = new();

    public static void Remember(string endpoint, Phys p)
    {
        lock (gate)
        {
            var d = Load();
            d["pin:" + endpoint] = "1";
            string key = "dns:" + p.Id;
            if (!d.ContainsKey(key))
            {
                string current = Net.ReadStaticDns(p.Id);
                d[key] = Net.IsCloudflareDns(current) ? "" : current; // 1.1.1.1 left by the old .bat buttons = really DHCP
            }
            Save(d);
        }
    }

    public static void RememberLan(IEnumerable<string> prefixes, Phys p)
    {
        lock (gate)
        {
            var d = Load();
            foreach (var prefix in prefixes) d["lan:" + prefix] = p.Index + "|" + p.Gateway;
            Save(d);
        }
    }

    static void Save(Dictionary<string, string> d)
    {
        Directory.CreateDirectory(Setup.Dir);
        File.WriteAllLines(FilePath, d.Select(kv => kv.Key + "=" + kv.Value));
    }

    public static void Restore()
    {
        lock (gate) RestoreLocked();
    }

    /// Moving to another network: take the LAN shortcuts off the old one.
    public static void DropLan()
    {
        lock (gate)
        {
            var d = Load();
            foreach (var kv in d.Where(kv => kv.Key.StartsWith("lan:")).ToList())
            {
                var v = kv.Value.Split('|');
                try { if (v.Length == 2) Net.RemoveLanRoute(kv.Key.Substring(4), v[0], v[1]); } catch { }
                d.Remove(kv.Key);
            }
            Save(d);
        }
    }

    static void RestoreLocked()
    {
        foreach (var kv in Load())
        {
            try
            {
                if (kv.Key == "endpoint") Net.UnpinEndpoint(kv.Value);                 // written by older versions
                else if (kv.Key.StartsWith("pin:")) Net.UnpinEndpoint(kv.Key.Substring(4));
                else if (kv.Key.StartsWith("dns:")) Net.RestoreDns(kv.Key.Substring(4), kv.Value);
                else if (kv.Key.StartsWith("lan:"))
                {
                    var v = kv.Value.Split('|');
                    if (v.Length == 2) Net.RemoveLanRoute(kv.Key.Substring(4), v[0], v[1]);
                }
            }
            catch { }
        }
        try { File.Delete(FilePath); } catch { }
        Native.DnsFlushResolverCache();
    }
}
