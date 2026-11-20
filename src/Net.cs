using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace WarpSwitch;

/// The real (physical) uplink: Wi-Fi or Ethernet with a gateway.
sealed class Phys
{
    public string Id, Name;   // Name = what Windows calls the adapter ("Wi-Fi")
    public int Index;
    public IPAddress Gateway, Address;
    public bool HasIPv6;      // the real network hands out public IPv6 (so apps could skip the tunnel over it)
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
    public const string CloudflareDns4 = "1.1.1.1,1.0.0.1";
    public const string CloudflareDns6 = "2606:4700:4700::1111,2606:4700:4700::1001";
    static readonly string[] NotPhysical = { "wintun", "wireguard", "tailscale", "zerotier", "openvpn", "tap-windows", "wi-fi direct", "vpn", TunName };
    static readonly IPAddress Probe4 = IPAddress.Parse("1.1.1.1");

    /// Where routing/DNS hiccups get reported (the tunnel points this at its log).
    public static Action<string> Log = _ => { };

    public static Phys FindPhysical()
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
                var addr = ip.UnicastAddresses.Select(u => u.Address)
                             .Where(a => a.AddressFamily == AddressFamily.InterNetwork)
                             .OrderBy(a => a.GetAddressBytes()[0] == 169 ? 1 : 0)   // a real address over a 169.254 fallback
                             .FirstOrDefault();
                if (v4 == null || gw == null || addr == null) continue;
                bool v6 = ip.GatewayAddresses.Any(g => g.Address.AddressFamily == AddressFamily.InterNetworkV6)
                          && ip.UnicastAddresses.Any(u => u.Address.AddressFamily == AddressFamily.InterNetworkV6 && !u.Address.IsIPv6LinkLocal && !u.Address.IsIPv6SiteLocal);
                found.Add((new Phys { Id = n.Id, Name = n.Name, Index = v4.Index, Gateway = gw, Address = addr, HasIPv6 = v6 }, n.NetworkInterfaceType));
            }
            catch (NetworkInformationException) { }
        }
        if (found.Count == 0) return null;

        // Rank uplinks the way Windows does: by their default route's metric. (This used to ask Windows which
        // adapter reaches the WARP server, but once traffic moved to a backup server that answer was the tunnel,
        // and a "prefer Ethernet" guess took over.)
        Dictionary<int, int> metrics;
        try { metrics = Native.DefaultRouteMetrics(); } catch { metrics = new Dictionary<int, int>(); }
        return found.OrderBy(f => metrics.TryGetValue(f.Phys.Index, out int m) ? m : int.MaxValue)
                    .ThenBy(f => f.Type == NetworkInterfaceType.Ethernet ? 0 : f.Type == NetworkInterfaceType.Wireless80211 ? 1 : 2)
                    .First().Phys;
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

    // ---------------------------------------------------------------- routes

    /// Adds a route through the IP Helper API (no process launches); falls back to netsh if Windows refuses.
    static void AddRoute(string cidr, int ifIndex, IPAddress nextHop, int metric)
    {
        var (prefix, length) = ParseCidr(cidr);
        int rc;
        try { rc = Native.AddRoute(prefix, length, ifIndex, nextHop, metric); }
        catch (Exception ex) { rc = ex.HResult; }
        if (rc == 0 || rc == Native.ERROR_OBJECT_ALREADY_EXISTS) return;
        string fam = prefix.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
        int code = Shell.Run("netsh.exe", $"interface {fam} add route prefix={cidr} interface={ifIndex} nexthop={nextHop} metric={metric} store=active");
        Log($"· route {cidr} via if {ifIndex}: API said {rc}, netsh said {code}");
    }

    static void DeleteRoutes(string cidr, int ifIndex = -1)
    {
        var (prefix, length) = ParseCidr(cidr);
        try { Native.DeleteRoutes(prefix.AddressFamily, r => r.PrefixLength == length && r.Prefix.Equals(prefix) && (ifIndex < 0 || r.IfIndex == ifIndex)); }
        catch (Exception ex)
        {
            Log("· route cleanup via API failed (" + ex.Message + "), using netsh");
            string fam = prefix.AddressFamily == AddressFamily.InterNetworkV6 ? "ipv6" : "ipv4";
            Shell.Run("netsh.exe", $"interface {fam} delete route prefix={cidr}" + (ifIndex >= 0 ? " interface=" + ifIndex : "") + " store=active");
        }
    }

    internal static (IPAddress Prefix, int Length) ParseCidr(string cidr)
    {
        var parts = cidr.Split('/');
        var ip = IPAddress.Parse(parts[0]);
        int len = parts.Length > 1 ? int.Parse(parts[1]) : (ip.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32);
        return (ip, len);
    }

    /// Keep the tunnel's own packets on the physical link so they don't loop into the tunnel.
    public static void PinEndpoint(string ep, Phys p)
    {
        Recovery.RememberPin(ep, p);
        DeleteRoutes(ep + "/32");
        AddRoute(ep + "/32", p.Index, p.Gateway, 1);
    }

    public static void UnpinEndpoint(string ep)
    {
        if (IPAddress.TryParse(ep, out _)) DeleteRoutes(ep + "/32");
    }

    /// Are our /1 routes into the tunnel all in the routing table (IPv4, and IPv6 if the uplink has it)?
    /// Read from the table itself, so a missing IPv6 address on the tunnel or a bypass entry can't make it look
    /// like they're gone and trigger endless re-adding.
    public static bool RoutesPresent(int tunIndex, bool ipv6)
    {
        try
        {
            bool Has(AddressFamily f) => Native.Routes(f).Count(r => r.IfIndex == tunIndex && r.PrefixLength == 1) >= 2;
            return Has(AddressFamily.InterNetwork) && (!ipv6 || Has(AddressFamily.InterNetworkV6));
        }
        catch { return true; }   // can't tell: don't thrash
    }

    /// Has Windows started sending traffic to the tunnel yet (fresh routes take a moment)?
    public static bool RoutesInEffect(int tunIndex) => Native.BestInterface(Probe4) == tunIndex;

    /// Two /1 routes beat the normal 0.0.0.0/0 default without deleting it; they vanish with the adapter.
    /// On networks with IPv6, ::/1 and 8000::/1 go to the tunnel too, whether or not the tunnel has its IPv6
    /// address yet: if it doesn't, IPv6 simply fails fast and apps use IPv4, instead of quietly going out
    /// the real network unprotected.
    public static void RouteAllThrough(int tunIndex, bool uplinkHasIPv6)
    {
        AddRoute("0.0.0.0/1", tunIndex, IPAddress.Any, 256);
        AddRoute("128.0.0.0/1", tunIndex, IPAddress.Any, 256);
        if (!uplinkHasIPv6) return;
        AddRoute("::/1", tunIndex, IPAddress.IPv6Any, 256);
        AddRoute("8000::/1", tunIndex, IPAddress.IPv6Any, 256);
    }

    static readonly string[] Lan = { "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16" };

    /// Campus/office/home-LAN addresses (login portals, intranet sites, printers) keep going straight out the real
    /// network at full speed (WARP can't reach private addresses anyway), and so does the user's bypass list.
    /// Returns the prefixes now routed around the tunnel.
    public static List<string> BypassRoutes(Phys p, IEnumerable<string> userEntries)
    {
        List<Native.Route> existing;
        try { existing = Native.Routes(AddressFamily.InterNetwork); } catch { existing = new List<Native.Route>(); }
        var ours = Recovery.RoutedAround();
        var done = new List<string>();
        foreach (var prefix in Lan.Concat(ResolveBypass(userEntries)).Distinct())
        {
            var (ip, len) = ParseCidr(prefix);
            // Never touch a route the network itself handed out for that range (ours get cleaned up at launch).
            if (!ours.Contains(prefix) && existing.Any(r => r.PrefixLength == len && r.Prefix.Equals(ip))) continue;
            Recovery.RememberRoute(prefix, p);
            AddRoute(prefix, p.Index, p.Gateway, 5);
            done.Add(prefix);
        }
        return done;
    }

    public static void RemoveRoute(string prefix, int ifIndex) => DeleteRoutes(prefix, ifIndex);

    /// One line of the bypass list → an IPv4 CIDR ("203.0.113.0/24"), a host name to resolve, or null if invalid.
    internal static string ParseBypass(string line, out bool isHost)
    {
        isHost = false;
        string s = (line ?? "").Trim();
        int hash = s.IndexOf('#');
        if (hash >= 0) s = s.Substring(0, hash).Trim();
        if (s.Length == 0) return null;
        var m = Regex.Match(s, @"^(\d{1,3}(?:\.\d{1,3}){3})(?:/(\d{1,2}))?$");
        if (m.Success)
        {
            if (!IPAddress.TryParse(m.Groups[1].Value, out var ip)) return null;
            int len = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 32;
            if (len < 1 || len > 32) return null;   // 0.0.0.0/0 would bypass everything
            uint mask = len == 32 ? uint.MaxValue : ~(uint.MaxValue >> len);
            var b = ip.GetAddressBytes();
            uint v = ((uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3]) & mask;
            return $"{v >> 24}.{(v >> 16) & 255}.{(v >> 8) & 255}.{v & 255}/{len}";
        }
        if (Regex.IsMatch(s, @"^(?=.{1,253}$)([a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?\.)+[a-z]{2,63}$", RegexOptions.IgnoreCase))
        {
            isHost = true;
            return s.ToLowerInvariant();
        }
        return null;
    }

    static IEnumerable<string> ResolveBypass(IEnumerable<string> entries)
    {
        var result = new List<string>();
        foreach (var e in entries ?? Enumerable.Empty<string>())
        {
            string v = ParseBypass(e, out bool host);
            if (v == null) continue;
            if (!host) { result.Add(v); continue; }
            try
            {
                foreach (var a in Dns.GetHostAddresses(v).Where(a => a.AddressFamily == AddressFamily.InterNetwork).Take(16))
                    result.Add(a + "/32");
            }
            catch (Exception ex) { Log("· bypass: couldn't look up " + v + " (" + ex.GetBaseException().Message + ")"); }
            if (result.Count > 256) break;
        }
        return result;
    }

    // ---------------------------------------------------------------- network identity

    /// The network this adapter is on right now. Windows keeps a profile for every network the adapter has ever
    /// joined (15 Wi-Fi networks on one adapter is normal) and lists them all, so taking the first match named
    /// the wrong network: home and campus both came out as "CSE_Student".
    static ConnectionProfile ProfileOf(Phys p)
    {
        if (p == null || !Guid.TryParse(p.Id, out var g)) return null;
        try
        {
            var mine = NetworkInformation.GetConnectionProfiles().Where(c => c.NetworkAdapter != null && c.NetworkAdapter.NetworkAdapterId == g).ToList();
            int pick = PickProfile(mine.Select(c => (ConnectedSsid(c), Level(c) != NetworkConnectivityLevel.None)).ToList());
            return pick < 0 ? null : mine[pick];
        }
        catch { return null; }
    }

    static string ConnectedSsid(ConnectionProfile c)
    {
        try { return c.IsWlanConnectionProfile ? c.WlanConnectionProfileDetails.GetConnectedSsid() : null; }
        catch { return null; }
    }

    static NetworkConnectivityLevel Level(ConnectionProfile c)
    {
        try { return c.GetNetworkConnectivityLevel(); }
        catch { return NetworkConnectivityLevel.None; }
    }

    /// Which profile is the live one: the Wi-Fi profile with a connected SSID, else the one that isn't
    /// disconnected, else the only one. -1 if it can't be told.
    internal static int PickProfile(IList<(string ConnectedSsid, bool Up)> profiles)
    {
        for (int i = 0; i < profiles.Count; i++) if (!string.IsNullOrEmpty(profiles[i].ConnectedSsid)) return i;
        for (int i = 0; i < profiles.Count; i++) if (profiles[i].Up) return i;
        return profiles.Count == 1 ? 0 : -1;
    }

    /// What Windows calls this network (the Wi-Fi name, or "Network 2" for cables). Used to remember what works where.
    public static string NetworkName(Phys p)
    {
        if (p == null) return null;
        string name = null;
        try
        {
            var prof = ProfileOf(p);
            name = prof?.ProfileName;
            if (string.IsNullOrWhiteSpace(name)) name = prof == null ? null : ConnectedSsid(prof);
        }
        catch { }
        return string.IsNullOrWhiteSpace(name) ? p.Name + " " + p.Gateway : name.Trim();
    }

    /// Wi-Fi networks Windows remembers, by the same name NetworkName gives them (for "auto-connect on…").
    public static List<string> KnownWifiNetworks()
    {
        var names = new List<string>();
        try
        {
            foreach (var c in NetworkInformation.GetConnectionProfiles())
            {
                try
                {
                    if (!c.IsWlanConnectionProfile) continue;
                    string n = c.ProfileName?.Trim();
                    if (!string.IsNullOrEmpty(n) && !names.Contains(n, StringComparer.OrdinalIgnoreCase)) names.Add(n);
                }
                catch { }
            }
        }
        catch { }
        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    /// Windows' own check says this network is holding traffic back for a sign-in page.
    public static bool BehindLoginPage(Phys p)
    {
        try { return ProfileOf(p)?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.ConstrainedInternetAccess; }
        catch { return false; }
    }

    // ---------------------------------------------------------------- DNS

    /// The network's own resolver returns poisoned answers, so resolve through Cloudflare instead. Both address
    /// families: on dual-stack networks Windows also asks the IPv6 servers the router advertised, which would
    /// otherwise leak lookups to the network (and get the poisoned answers anyway).
    public static void SetCloudflareDns(string adapterId, int index)
    {
        SetDns(adapterId, index, false, CloudflareDns4);
        SetDns(adapterId, index, true, CloudflareDns6);
    }

    static void SetDns(string adapterId, int index, bool v6, string servers)
    {
        if (Guid.TryParse(adapterId, out var g))
        {
            int rc = Native.SetDns(g, v6, servers);
            if (rc == 0) return;
            Log($"· DNS via API said {rc}, using netsh");
        }
        string fam = v6 ? "ipv6" : "ipv4";
        var list = Split(servers);
        if (list.Length == 0)
        {
            Shell.Run("netsh.exe", $"interface {fam} set dnsservers name={index} source=dhcp");
            return;
        }
        Shell.Run("netsh.exe", $"interface {fam} set dnsservers name={index} source=static address={list[0]} register=none validate=no");
        for (int i = 1; i < list.Length; i++)
            Shell.Run("netsh.exe", $"interface {fam} add dnsservers name={index} address={list[i]} index={i + 1} validate=no");
    }

    static string DnsKey(string id, bool v6) => @"SYSTEM\CurrentControlSet\Services\" + (v6 ? "Tcpip6" : "Tcpip") + @"\Parameters\Interfaces\" + id;

    public static string ReadStaticDns(string id, bool v6 = false)
    {
        using var key = Registry.LocalMachine.OpenSubKey(DnsKey(id, v6));
        return ((key?.GetValue("NameServer") as string) ?? "").Trim();
    }

    public static bool IsCloudflareDns(string value) => string.Join(",", Split(value)) == CloudflareDns4;

    static string[] Split(string servers) => (servers ?? "").Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

    /// Puts an adapter's DNS back. If the adapter is gone (a USB Wi-Fi stick that was unplugged), the setting is
    /// written where Windows reads it when the adapter returns, instead of being skipped and forgotten.
    public static void RestoreDns(string id, bool v6, string original)
    {
        var n = ById(id);
        int idx = n == null ? -1 : IndexOf(n);
        if (idx >= 0)
        {
            SetDns(id, idx, v6, string.Join(",", Split(original)));
            return;
        }
        using var key = Registry.LocalMachine.OpenSubKey(DnsKey(id, v6), writable: true)
            ?? throw new IOException("adapter " + id + " is unknown to Windows");
        key.SetValue("NameServer", string.Join(",", Split(original)));
    }

    // ---------------------------------------------------------------- checks through the tunnel

    /// Cheap liveness check: can a TCP handshake to Cloudflare DNS complete through the tunnel? Returns how long
    /// it took (a round trip through WARP, shown as the ping), or -1.
    public static async Task<int> ProbeAsync()
    {
        foreach (var ip in new[] { "1.1.1.1", "1.0.0.1" })
        {
            var clock = Stopwatch.StartNew();
            using var c = new TcpClient();
            var connect = c.ConnectAsync(ip, 443);
            _ = connect.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (await Task.WhenAny(connect, Task.Delay(5000)).ConfigureAwait(false) == connect && connect.Status == TaskStatus.RanToCompletion)
                return (int)clock.ElapsedMilliseconds;
        }
        return -1;
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

    /// The tunnel's routes aren't live yet (Windows is still bringing the adapter up).
    public static bool IsUnreachable(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
            if (e is SocketException se && se.SocketErrorCode is SocketError.HostUnreachable or SocketError.NetworkUnreachable)
                return true;
        return false;
    }
}

/// Remembers what we changed (endpoint routes, LAN routes, DNS, kill switch, DoH) on disk, so a crash or power cut
/// can be undone next launch. Kept in the admin-only folder; every entry is validated before it's acted on.
static class Recovery
{
    internal static string FilePath = Path.Combine(Setup.SecureDir, "state.ini");
    static string LegacyPath => Path.Combine(Setup.Dir, "state.ini");   // where versions before 2.1 kept it
    static readonly object gate = new();

    public static bool Pending => File.Exists(FilePath) || File.Exists(LegacyPath);

    internal static Dictionary<string, string> Parse(IEnumerable<string> lines)
    {
        var d = new Dictionary<string, string>();
        foreach (var line in lines)
        {
            int i = line.IndexOf('=');
            if (i <= 0) continue;
            string k = line.Substring(0, i), v = line.Substring(i + 1);
            if (Valid(k, v)) d[k] = v;
        }
        return d;
    }

    static bool IsIp(string s) => IPAddress.TryParse(s, out _) && Regex.IsMatch(s, @"^[0-9a-fA-F.:]+$");
    static bool IsIfGateway(string v) { var f = v.Split('|'); return f.Length == 2 && int.TryParse(f[0], out int i) && i > 0 && IsIp(f[1]); }
    static bool IsCidr4(string s) => Regex.IsMatch(s, @"^\d{1,3}(\.\d{1,3}){3}/\d{1,2}$") && IsIp(s.Split('/')[0]);
    static bool IsServerList(string s) => s.Length == 0 || s.Split(new[] { ',', ' ' }, StringSplitOptions.RemoveEmptyEntries).All(IsIp);

    /// Only shapes this app writes; anything else in the file is ignored rather than passed to Windows.
    internal static bool Valid(string key, string value)
    {
        if (key == "endpoint") return IsIp(value);
        if (key == "kill") return value == "1";
        if (key.StartsWith("pin:")) return IsIp(key.Substring(4)) && (value == "1" || IsIfGateway(value));
        if (key.StartsWith("lan:")) return IsCidr4(key.Substring(4)) && IsIfGateway(value);
        if (key.StartsWith("dns:")) return Guid.TryParse(key.Substring(4), out _) && IsServerList(value);
        if (key.StartsWith("dns6:")) return Guid.TryParse(key.Substring(5), out _) && IsServerList(value);
        if (key.StartsWith("doh:")) return IsIp(key.Substring(4)) && Regex.IsMatch(value, @"^[01]\|[01]\|[01]\|(https://[^\s|]+)?$");
        return false;
    }

    static Dictionary<string, string> Load(string path)
    {
        try { return Parse(File.ReadAllLines(path)); }
        catch (FileNotFoundException) { return new Dictionary<string, string>(); }
        catch (DirectoryNotFoundException) { return new Dictionary<string, string>(); }
        catch (IOException) { return new Dictionary<string, string>(); }
        catch (UnauthorizedAccessException) { return new Dictionary<string, string>(); }
    }

    static void Save(Dictionary<string, string> d)
    {
        if (d.Count == 0)
        {
            try { File.Delete(FilePath); } catch { }
            return;
        }
        string dir = Path.GetDirectoryName(FilePath);
        if (!Directory.Exists(dir))
        {
            if (FilePath.StartsWith(Setup.SecureDir, StringComparison.OrdinalIgnoreCase)) SecureFolder.Ensure(dir);
            else Directory.CreateDirectory(dir);
        }
        Setup.WriteAllLinesAtomic(FilePath, d.Select(kv => kv.Key + "=" + kv.Value));
    }

    static void Update(Action<Dictionary<string, string>> change)
    {
        lock (gate)
        {
            var d = Load(FilePath);
            change(d);
            Save(d);
        }
    }

    /// Fold the pre-2.1 per-user file into the protected one.
    public static void MigrateLegacy()
    {
        if (!File.Exists(LegacyPath)) return;
        var old = Load(LegacyPath);
        Update(d => { foreach (var kv in old) if (!d.ContainsKey(kv.Key)) d[kv.Key] = kv.Value; });
        try { File.Delete(LegacyPath); } catch { }
    }

    public static void RememberPin(string endpoint, Phys p) => Update(d => d["pin:" + endpoint] = p.Index + "|" + p.Gateway);

    public static void RememberRoute(string prefix, Phys p) => Update(d => d["lan:" + prefix] = p.Index + "|" + p.Gateway);

    /// The adapter's own DNS settings (both families), recorded once before we first change them.
    public static void RememberDns(Phys p) => Update(d =>
    {
        if (!d.ContainsKey("dns:" + p.Id))
        {
            string current = Net.ReadStaticDns(p.Id);
            d["dns:" + p.Id] = Net.IsCloudflareDns(current) ? "" : current; // 1.1.1.1 left by the old .bat buttons = really DHCP
        }
        if (!d.ContainsKey("dns6:" + p.Id)) d["dns6:" + p.Id] = Net.ReadStaticDns(p.Id, true);
    });

    public static void Set(string key, string value)
    {
        if (Valid(key, value)) Update(d => d[key] = value);
    }

    public static void SetIfMissing(string key, string value)
    {
        if (Valid(key, value)) Update(d => { if (!d.ContainsKey(key)) d[key] = value; });
    }

    public static bool Has(string key) => Get(key) != null;

    public static string Get(string key) { lock (gate) return Load(FilePath).TryGetValue(key, out var v) ? v : null; }

    public static void Forget(string key) => Update(d => d.Remove(key));

    /// Routes currently kept off the tunnel (LAN + bypass list).
    public static List<string> RoutedAround() { lock (gate) return Load(FilePath).Keys.Where(k => k.StartsWith("lan:")).Select(k => k.Substring(4)).ToList(); }

    public static void Restore()
    {
        lock (gate)
        {
            MigrateLegacy();
            var d = Load(FilePath);
            var failed = new Dictionary<string, string>();
            foreach (var kv in d)
            {
                try { Undo(kv.Key, kv.Value); }
                catch (Exception ex)
                {
                    Net.Log("· couldn't undo " + kv.Key + ": " + ex.GetBaseException().Message);
                    if (kv.Key.StartsWith("dns") || kv.Key.StartsWith("doh:")) failed[kv.Key] = kv.Value;   // worth another try next launch
                }
            }
            Save(failed);
        }
        Native.DnsFlushResolverCache();
    }

    static void Undo(string key, string value)
    {
        if (key == "endpoint") Net.UnpinEndpoint(value);                 // written by older versions
        else if (key == "kill") KillSwitch.Remove();
        else if (key.StartsWith("pin:")) Net.UnpinEndpoint(key.Substring(4));
        else if (key.StartsWith("lan:")) Net.RemoveRoute(key.Substring(4), int.Parse(value.Split('|')[0]));
        else if (key.StartsWith("dns:")) Net.RestoreDns(key.Substring(4), false, value);
        else if (key.StartsWith("dns6:")) Net.RestoreDns(key.Substring(5), true, value);
        else if (key.StartsWith("doh:")) SecureDns.Restore(key.Substring(4), value);
    }

    /// Moving to another network (or changing the bypass list): take the routes around the tunnel off.
    public static void DropRoutes()
    {
        lock (gate)
        {
            var d = Load(FilePath);
            foreach (var kv in d.Where(kv => kv.Key.StartsWith("lan:")).ToList())
            {
                try { Net.RemoveRoute(kv.Key.Substring(4), int.Parse(kv.Value.Split('|')[0])); } catch { }
                d.Remove(kv.Key);
            }
            Save(d);
        }
    }
}
