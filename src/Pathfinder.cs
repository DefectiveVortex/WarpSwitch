using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Tasks;

namespace WarpSwitch;

/// One way of reaching WARP: which server address and port, over which transport, under which disguise (SNI).
sealed class WarpPath
{
    public string Ip, Sni;
    public int Port;
    public bool Quic;
    public int Ms = -1;   // handshake time in the last scout; -1 = no answer

    public string Key => (Quic ? "quic" : "tcp") + "|" + Ip + "|" + Port + "|" + Sni;
    public string Short => (Quic ? "QUIC" : "TCP") + " " + Port;
    public override string ToString() => (Quic ? "QUIC " : "TCP ") + Ip + ":" + Port + " as " + Sni;

    public static WarpPath Parse(string key)
    {
        var f = (key ?? "").Split('|');
        if (f.Length != 4 || !IPAddress.TryParse(f[1], out _) || !int.TryParse(f[2], out int port)) return null;
        return new WarpPath { Quic = f[0] == "quic", Ip = f[1], Port = port, Sni = f[3] };
    }

    /// QUIC runs MASQUE over UDP (fastest, no TCP-inside-TCP); otherwise HTTP/2 over TCP+TLS.
    public string Args(int mtu, string config) =>
        "nativetun" + (Quic ? "" : " --http2") + " -s " + Sni + (Port != 443 ? " -P " + Port : "")
        + (mtu > 0 && !Quic ? " -m " + mtu : "") + " --always-reconnect"
        + (config == Setup.ConfigPath ? "" : " -c \"" + config + "\"");
}

/// Finds a way through whatever network we're on. Before connecting it knocks on a handful of WARP routes
/// at once, straight out the real adapter (a few handshakes, done in well under a second normally), and
/// orders them: what worked on this network last time, then QUIC if UDP gets through, then the TCP routes
/// that answered. If the network blocks or cuts one route, the tunnel moves down this list.
static class Pathfinder
{
    static readonly string[] Snis = { "www.cloudflare.com", "cloudflare.com", "one.one.one.one" };
    const int ProbeTimeoutMs = 2000;   // TCP + TLS
    const int QuicTimeoutMs = 1000;    // a QUIC server answers within one round trip, so silence comes quickly

    public sealed class Plan
    {
        public List<WarpPath> Paths;
        public int TcpOpen, Tried;
        public bool QuicOpen, Intercepted;
        public long Ms;
        public bool NothingAnswered => TcpOpen == 0 && !QuicOpen;
    }

    /// Other addresses that answer as the very same WARP server (same pinned key), found by scanning
    /// Cloudflare's MASQUE ranges. A network that blocks or keeps cutting the main address rarely blocks all of these.
    static string Alternate(Random rnd, ICollection<string> avoid)
    {
        string ip;
        do ip = rnd.Next(4) == 0 ? "162.159.199." + rnd.Next(2, 63) : "162.159.198." + rnd.Next(132, 255);
        while (avoid.Contains(ip));
        avoid.Add(ip);
        return ip;
    }

    public static async Task<Plan> ScoutAsync(Phys phys, string endpoint, byte[] pinnedKey, string remembered)
    {
        var rnd = new Random();
        var used = new List<string> { endpoint };
        string a1 = Alternate(rnd, used), a2 = Alternate(rnd, used), a3 = Alternate(rnd, used);
        string sni = Snis[0];
        var candidates = new List<WarpPath>
        {
            new() { Ip = endpoint, Port = 443, Sni = sni, Quic = true },
            new() { Ip = a1, Port = 443, Sni = sni, Quic = true },
            new() { Ip = endpoint, Port = 443, Sni = sni },
            new() { Ip = a1, Port = 443, Sni = sni },
            new() { Ip = endpoint, Port = 8443, Sni = sni },
            new() { Ip = a2, Port = 4443, Sni = sni },
            new() { Ip = endpoint, Port = 500, Sni = sni },
            new() { Ip = a3, Port = 1701, Sni = sni },
            new() { Ip = a1, Port = 4500, Sni = Snis[1] },
            new() { Ip = a2, Port = 8095, Sni = Snis[2] },
        };
        var mem = WarpPath.Parse(remembered);
        if (mem != null)
        {
            candidates.RemoveAll(c => c.Key == mem.Key);
            candidates.Insert(0, mem);
        }

        var sw = Stopwatch.StartNew();
        var results = await Task.WhenAll(candidates.Select(c => c.Quic ? QuicProbeAsync(phys.Address, c) : TlsProbeAsync(phys.Address, c, pinnedKey))).ConfigureAwait(false);
        var plan = new Plan { Tried = candidates.Count, Ms = sw.ElapsedMilliseconds };
        for (int i = 0; i < candidates.Count; i++)
        {
            candidates[i].Ms = results[i] >= 0 ? results[i] : -1;
            if (results[i] == -2) plan.Intercepted = true;
        }

        var open = candidates.Where(c => c.Ms >= 0).ToList();
        plan.QuicOpen = open.Any(c => c.Quic);
        plan.TcpOpen = open.Count(c => !c.Quic);
        int best = open.Count == 0 ? 0 : open.Min(c => c.Ms);
        var paths = new List<WarpPath>();
        if (mem != null && mem.Ms >= 0) paths.Add(mem);
        paths.AddRange(open.Where(c => c.Quic && !paths.Contains(c)).OrderBy(c => c.Ms));
        // TCP routes that answered keep their preference order, except ones that answered far slower than the rest.
        var tcp = open.Where(c => !c.Quic && !paths.Contains(c)).ToList();
        paths.AddRange(tcp.Where(c => c.Ms <= best * 3 + 150));
        paths.AddRange(tcp.Where(c => c.Ms > best * 3 + 150));
        // Silence everywhere may just mean the probes are what's filtered: keep the TCP routes as a last resort.
        paths.AddRange(candidates.Where(c => !c.Quic && c.Ms < 0 && results[candidates.IndexOf(c)] != -2));
        if (paths.Count == 0) paths.Add(new WarpPath { Ip = endpoint, Port = 443, Sni = sni });
        plan.Paths = paths;
        return plan;
    }

    /// TCP connect + TLS handshake with the disguise SNI. Returns ms, -1 for no answer / reset,
    /// -2 if the server's key isn't WARP's (something on the network is intercepting TLS).
    static async Task<int> TlsProbeAsync(IPAddress local, WarpPath p, byte[] pinnedKey)
    {
        var sw = Stopwatch.StartNew();
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            s.Bind(new IPEndPoint(local, 0));
            var work = Handshake();
            _ = work.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (await Task.WhenAny(work, Task.Delay(ProbeTimeoutMs)).ConfigureAwait(false) != work) return -1;
            return await work.ConfigureAwait(false);
        }
        catch { return -1; }
        finally { s.Close(); }

        async Task<int> Handshake()
        {
            var ep = new IPEndPoint(IPAddress.Parse(p.Ip), p.Port);
            await Task.Factory.FromAsync((cb, st) => s.BeginConnect(ep, cb, st), s.EndConnect, null).ConfigureAwait(false);
            byte[] key = null;
            using var ssl = new SslStream(new NetworkStream(s, false), false, (o, cert, chain, errors) => { key = cert?.GetPublicKey(); return true; });
            await ssl.AuthenticateAsClientAsync(p.Sni).ConfigureAwait(false);
            if (pinnedKey != null && key != null && !key.SequenceEqual(pinnedKey)) return -2;
            return (int)sw.ElapsedMilliseconds;
        }
    }

    /// Is UDP to this QUIC server open? A long-header packet with a reserved version makes any QUIC server
    /// answer with Version Negotiation, without starting a real handshake.
    static async Task<int> QuicProbeAsync(IPAddress local, WarpPath p)
    {
        var sw = Stopwatch.StartNew();
        var u = new UdpClient(new IPEndPoint(local, 0));
        try
        {
            var pkt = new byte[1200];
            pkt[0] = 0xC0; pkt[1] = 0x1A; pkt[2] = 0x2A; pkt[3] = 0x3A; pkt[4] = 0x4A;
            pkt[5] = 8;
            var id = new byte[8];
            new Random().NextBytes(id);
            Array.Copy(id, 0, pkt, 6, 8);
            await u.SendAsync(pkt, pkt.Length, new IPEndPoint(IPAddress.Parse(p.Ip), p.Port)).ConfigureAwait(false);
            var recv = u.ReceiveAsync();
            _ = recv.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            if (await Task.WhenAny(recv, Task.Delay(QuicTimeoutMs)).ConfigureAwait(false) != recv) return -1;
            var r = (await recv.ConfigureAwait(false)).Buffer;
            bool negotiation = r.Length >= 7 && (r[0] & 0x80) != 0 && r[1] == 0 && r[2] == 0 && r[3] == 0 && r[4] == 0;
            return negotiation ? (int)sw.ElapsedMilliseconds : -1;
        }
        catch { return -1; }
        finally { u.Close(); }
    }
}

/// Which route worked on which network, so the next connect there goes straight to it.
static class PathMemory
{
    static string FilePath => Path.Combine(Setup.Dir, "networks.ini");
    static readonly object gate = new();
    const int Keep = 40;

    static List<string[]> Load()
    {
        try { return File.ReadAllLines(FilePath).Select(l => l.Split('\t')).Where(f => f.Length == 3).ToList(); }
        catch { return new List<string[]>(); }
    }

    public static string Get(string network)
    {
        lock (gate) return Load().LastOrDefault(f => f[1] == network)?[2];
    }

    public static void Set(string network, WarpPath p)
    {
        if (string.IsNullOrEmpty(network) || p == null) return;
        lock (gate)
        {
            var rows = Load().Where(f => f[1] != network).ToList();
            rows.Add(new[] { DateTime.UtcNow.ToString("s"), network.Replace('\t', ' '), p.Key });
            try
            {
                Directory.CreateDirectory(Setup.Dir);
                File.WriteAllLines(FilePath, rows.Skip(Math.Max(0, rows.Count - Keep)).Select(f => string.Join("\t", f)));
            }
            catch { }
        }
    }
}
