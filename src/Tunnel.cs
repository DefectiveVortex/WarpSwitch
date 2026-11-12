using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace WarpSwitch;

enum TunnelState { Off, Starting, Connecting, Connected, Reconnecting, Stopping }

/// Runs usque's system-wide TUN mode (MASQUE with a disguised SNI, which gets past DPI) and does the
/// routing/DNS work. Redundancy: every connect scouts several routes (server address x port x QUIC/TCP),
/// and whenever the network blocks, cuts or starves the current route, the tunnel moves to the next one.
/// "Connected" is only ever claimed after real traffic has come back through the tunnel.
sealed class Tunnel
{
    static readonly Regex Timestamp = new(@"^\d{4}/\d\d/\d\d \d\d:\d\d:\d\d \S+ ", RegexOptions.Compiled);

    const int MaxSwitches = 8;            // route changes without a good connection before giving up honestly
    const int GiveUpAfterSeconds = 150;   // backstop for being stuck trying (only counts while a network exists)
    const int RouteTimeoutSeconds = 15;   // a route that hasn't come up by now is treated as blocked
    const int FlapWindowSeconds = 180;    // this many drops inside the window = the network keeps cutting this route
    const int FlapDrops = 3;

    readonly IntPtr job = Native.CreateKillOnCloseJob();
    readonly SemaphoreSlim op = new(1, 1);   // one connect/switch/disconnect at a time
    readonly object routeLock = new();
    readonly Timer netDebounce;
    readonly Timer health;
    readonly Queue<DateTime> drops = new();
    readonly Settings settings;
    Process proc;
    Phys phys;
    string endpoint;
    byte[] pinnedKey;
    int routedTun = -1;
    volatile bool stopping, verifying;

    List<WarpPath> plan = new();
    int planIdx, pathGen, pathFails, switches, upGeneration, healthTicks, healthBusy;
    DateTime busySince = DateTime.MinValue, trySince = DateTime.MinValue;

    public TunnelState State { get; private set; }
    public string Detail { get; private set; } = "";
    public bool DetailIsError { get; private set; }
    public TraceInfo Trace { get; private set; }
    public DateTime Since { get; private set; }
    public string TunId { get; private set; }
    public WarpPath Route { get; private set; }
    public string Network { get; private set; }
    public int Reroutes { get; private set; }     // route changes this session (shown in the UI)
    public bool OnBackupServer => Route != null && endpoint != null && Route.Ip != endpoint;
    public readonly LogBuffer Log = new(500, Path.Combine(Setup.Dir, "app.log"));
    public event Action Changed;

    public bool Busy => State is TunnelState.Starting or TunnelState.Connecting or TunnelState.Reconnecting or TunnelState.Stopping;
    bool Trying => State is TunnelState.Connecting or TunnelState.Reconnecting;

    public Tunnel(Settings settings)
    {
        this.settings = settings;
        netDebounce = new Timer(_ => OnNetworkChanged());
        NetworkChange.NetworkAddressChanged += (s, e) => netDebounce.Change(2500, Timeout.Infinite);
        health = new Timer(_ => _ = HealthTickAsync(), null, 5000, 5000);
    }

    void Set(TunnelState state, string detail = "", bool error = false)
    {
        bool wasTrying = Trying;
        bool nowTrying = state is TunnelState.Connecting or TunnelState.Reconnecting;
        if (nowTrying && !wasTrying) busySince = DateTime.Now;
        if (!nowTrying) busySince = DateTime.MinValue;
        State = state;
        Detail = detail ?? "";
        DetailIsError = error;
        Log.Add("· " + state + (Detail.Length > 0 ? ": " + Detail : ""));
        Changed?.Invoke();
    }

    void Hint(string detail)
    {
        if (Detail == detail) return;
        Detail = detail;
        DetailIsError = false;
        Changed?.Invoke();
    }

    public void ReportError(Exception ex) => Set(TunnelState.Off, ex is UserError ? ex.Message : "Setup failed: " + ex.Message, true);

    /// Undo leftovers from a crash or from the old .bat buttons. Returns true if a tunnel was running.
    public bool RecoverOnStartup()
    {
        bool wasRunning = KillStrays();
        if (Recovery.Pending) Recovery.Restore();
        else if (wasRunning) LegacyCleanup();
        Setup.DropRouteConfig();
        return wasRunning;
    }

    void LegacyCleanup()
    {
        string ep = File.Exists(Setup.ConfigPath) ? Setup.ReadEndpoint() : "162.159.198.2";
        Net.UnpinEndpoint(ep);
        var p = Net.FindPhysical(ep);
        if (p != null && Net.IsCloudflareDns(Net.ReadStaticDns(p.Id))) Net.RestoreDns(p.Id, "");
        Native.DnsFlushResolverCache();
    }

    static bool KillStrays()
    {
        bool any = false;
        foreach (var p in Process.GetProcessesByName("usque"))
        {
            any = true;
            try { p.Kill(); p.WaitForExit(4000); } catch { }
            p.Dispose();
        }
        return any;
    }

    // ---------------------------------------------------------------- connect

    public async Task ConnectAsync()
    {
        if (State != TunnelState.Off) return;
        await op.WaitAsync();
        try
        {
            if (State != TunnelState.Off) return;
            stopping = false;
            switches = 0;
            Reroutes = 0;
            Route = null;
            Set(TunnelState.Starting, "Getting ready…");
            await Task.Run(() =>
            {
                KillStrays();
                Setup.EnsureBinaries();
                Setup.MigrateOldConfig();
                if (!File.Exists(Setup.ConfigPath))
                {
                    Set(TunnelState.Starting, "Registering this PC with WARP…");
                    Setup.Register();
                }
                endpoint = Setup.ReadEndpoint();
                pinnedKey = Setup.ReadPinnedKey();
                phys = Net.FindPhysical(endpoint) ?? throw new UserError("No internet connection. Join a Wi-Fi network first.");
                Network = Net.NetworkName(phys);
                Log.Add("· uplink: " + phys.Name + " (" + Network + ") via " + phys.Gateway + ", endpoint " + endpoint);
            });
            if (stopping) return;
            Set(TunnelState.Starting, "Scouting routes through " + Network + "…");
            var scout = await ScoutAsync(phys);
            if (stopping) return;
            if (scout.NothingAnswered && Net.BehindLoginPage(phys))
            {
                OpenLoginPage();
                throw new UserError("This Wi-Fi wants you to sign in first. The sign-in page should open in your browser; connect again once you're in.");
            }
            await Task.Run(() =>
            {
                Recovery.Remember(plan[0].Ip, phys);
                Recovery.RememberLan(Net.BypassLan(phys), phys);
                Net.PinEndpoint(plan[0].Ip, phys);
            });
            if (stopping) return;
            Since = DateTime.MinValue;
            Set(TunnelState.Connecting, "Connecting through " + plan[0].Short + "…");
            await Task.Run(() => StartUsque(plan[0]));
        }
        catch (Exception ex)
        {
            await Task.Run(() => { KillStrays(); Recovery.Restore(); Setup.DropRouteConfig(); });
            ReportError(ex);
        }
        finally { op.Release(); }
    }

    async Task<Pathfinder.Plan> ScoutAsync(Phys p)
    {
        var scout = await Pathfinder.ScoutAsync(p, endpoint, pinnedKey, PathMemory.Get(Network));
        plan = scout.Paths;
        planIdx = 0;
        Log.Add($"· scouted {scout.Tried} routes in {scout.Ms} ms: {scout.TcpOpen} TCP open, QUIC {(scout.QuicOpen ? "open" : "blocked")}"
            + (scout.Intercepted ? ", some routes are being intercepted" : "") + "; trying " + string.Join(", ", plan.Take(4)));
        return scout;
    }

    static void OpenLoginPage()
    {
        try { Process.Start(new ProcessStartInfo("http://www.msftconnecttest.com/redirect") { UseShellExecute = true }); } catch { }
    }

    void StartUsque(WarpPath path)
    {
        string config = Setup.ConfigFor(path.Ip);
        int gen = Interlocked.Increment(ref pathGen);
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(Setup.UsquePath, path.Args(settings.Mtu, config))
            {
                WorkingDirectory = Setup.Dir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        p.OutputDataReceived += (s, e) => OnLine(e.Data, gen);
        p.ErrorDataReceived += (s, e) => OnLine(e.Data, gen);
        p.Exited += (s, e) => OnExited(p, gen);
        lock (routeLock) routedTun = -1;
        pathFails = 0;
        verifying = false;
        lock (drops) drops.Clear();
        trySince = DateTime.Now;
        Route = path;
        Log.Add("· route: " + path);
        p.Start();
        Native.AssignProcessToJobObject(job, p.Handle);
        proc = p;
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    void OnLine(string line, int gen)
    {
        // usque logs every multicast/TTL-1 packet it can't forward; that's noise.
        if (string.IsNullOrEmpty(line) || line.Contains("dropping proxied packet")) return;
        string msg = Timestamp.Replace(line, "");
        Log.Add(msg);
        if (stopping || gen != pathGen) return;   // a route we've already moved away from

        if (msg.Contains("Connected to MASQUE server"))
            Task.Run(() => OnTunnelUp(gen));
        else if (msg.StartsWith("Failed to connect tunnel"))
        {
            // One refusal can be a blip; two in a row on the same route means this route is blocked here.
            if (Interlocked.Increment(ref pathFails) >= 2) _ = SwitchAsync(gen, "this route keeps getting refused");
            else if (Trying) Hint(Friendly(msg));
        }
        else if (msg.StartsWith("Tunnel connection lost"))
        {
            trySince = DateTime.Now;
            if (State == TunnelState.Connected) Set(TunnelState.Reconnecting, "Connection dropped, reconnecting…");
            int recent;
            lock (drops)
            {
                drops.Enqueue(DateTime.Now);
                while (drops.Count > 0 && (DateTime.Now - drops.Peek()).TotalSeconds > FlapWindowSeconds) drops.Dequeue();
                recent = drops.Count;
            }
            if (recent >= FlapDrops) _ = SwitchAsync(gen, "the network keeps cutting this route");
        }
        else if (msg.StartsWith("Establishing MASQUE connection"))
        {
            if (State == TunnelState.Connected) Set(TunnelState.Reconnecting, "Connection dropped, reconnecting…");
        }
        else if (Trying && LooksBad(msg)) Hint(Friendly(msg));
    }

    void OnTunnelUp(int gen)
    {
        lock (routeLock)
        {
            if (stopping || gen != pathGen) return;
            NetworkInterface tun = null;
            for (int i = 0; i < 50 && (tun = Net.Tun()) == null; i++) Thread.Sleep(100);
            if (tun == null) { Log.Add("· tunnel adapter not found"); return; }
            int idx = Net.IndexOf(tun);
            TunId = tun.Id;
            if (idx != routedTun || !Net.RoutesActive(idx)) ApplyRouting(tun, idx);
        }
        if (stopping) return;
        // usque's handshake succeeding doesn't prove packets flow (the network can still cut them),
        // so stay in Connecting/Reconnecting until a real request comes back through the tunnel.
        int up = Interlocked.Increment(ref upGeneration);
        Set(State == TunnelState.Connecting ? TunnelState.Connecting : TunnelState.Reconnecting, "Checking that traffic gets through…");
        _ = VerifyAsync(up, gen);
    }

    void ApplyRouting(NetworkInterface tun, int tunIndex)
    {
        Net.RouteAllThrough(tun, tunIndex, phys != null && phys.HasIPv6);
        Net.SetCloudflareDns(tunIndex);
        if (phys != null) Net.SetCloudflareDns(phys.Index);
        Native.DnsFlushResolverCache();
        routedTun = tunIndex;
        Log.Add("· routing all traffic through the tunnel (if " + tunIndex + ")");
    }

    /// Ask Cloudflare what it sees through the tunnel. Only a real answer with warp=on counts as connected.
    async Task VerifyAsync(int up, int gen)
    {
        verifying = true;
        try
        {
            string[] hosts = { "1.1.1.1", "1.0.0.1", "1.1.1.1", "www.cloudflare.com", "1.0.0.1" };
            for (int attempt = 0; attempt < hosts.Length; attempt++)
            {
                if (stopping || up != upGeneration || gen != pathGen) return;
                try
                {
                    var t = await Net.TraceAsync(hosts[attempt]);
                    Log.Add("· check: ip=" + t.Ip + " loc=" + t.Loc + " colo=" + t.Colo + " warp=" + (t.Warp ? "on" : "off"));
                    if (stopping || up != upGeneration || gen != pathGen) return;
                    if (t.Warp)
                    {
                        Trace = t;
                        switches = 0;
                        pathFails = 0;
                        PathMemory.Set(Network, Route);
                        if (Since == DateTime.MinValue) Since = DateTime.Now;
                        Set(TunnelState.Connected);
                        return;
                    }
                    lock (routeLock)
                    {
                        var tun = Net.Tun();
                        if (!stopping && routedTun > 0 && tun != null) ApplyRouting(tun, routedTun);
                    }
                }
                catch (Exception ex) { Log.Add("· check failed: " + ex.GetBaseException().Message); }
                await Task.Delay(1500);
            }
        }
        finally { if (up == upGeneration) verifying = false; }
        if (stopping || up != upGeneration) return;
        await SwitchAsync(gen, "WARP's server answered, but no traffic came back through this route");
    }

    // ---------------------------------------------------------------- failover

    /// The current route isn't working: move to the next one in the plan (re-scouting when the plan runs out),
    /// or stop and say so plainly once too many routes have failed.
    async Task SwitchAsync(int gen, string reason)
    {
        if (stopping) return;
        await op.WaitAsync();
        try
        {
            if (stopping || gen != pathGen) return;   // already moved on
            var now = await Task.Run(() => Net.FindPhysical(endpoint));
            if (now == null)
            {
                // No network at all: nothing to switch to. Keep waiting; usque reconnects once one appears.
                if (State == TunnelState.Connected) Set(TunnelState.Reconnecting, "Waiting for a network…");
                else Hint("Waiting for a network…");
                return;
            }
            Trace = null;
            if (++switches > MaxSwitches)
            {
                await GiveUpLockedAsync($"Couldn't get WARP through this network right now: it blocked or cut all {MaxSwitches} routes tried. Your normal internet is back. Try again in a minute.");
                return;
            }
            Reroutes++;
            if (!now.SameAs(phys))
            {
                phys = now;
                Network = Net.NetworkName(now);
            }
            if (++planIdx >= plan.Count)
            {
                Set(TunnelState.Reconnecting, "Looking for fresh routes…");
                await ScoutAsync(now);
            }
            var next = plan[planIdx];
            Log.Add("· " + reason + "; switching to " + next + " (" + switches + "/" + MaxSwitches + ")");
            Set(TunnelState.Reconnecting, "Route blocked. Switching to " + next.Short + "…");
            await Task.Run(() => RestartOn(next, now));
        }
        finally { op.Release(); }
    }

    void RestartOn(WarpPath next, Phys uplink)
    {
        var old = proc;
        proc = null;   // makes OnExited ignore this deliberate stop
        try { if (old != null && !old.HasExited) { old.Kill(); old.WaitForExit(4000); } } catch { }
        KillStrays();
        lock (routeLock) routedTun = -1;
        TunId = null;
        Recovery.Remember(next.Ip, uplink);
        Net.PinEndpoint(next.Ip, uplink);
        StartUsque(next);
    }

    /// Couldn't get traffic flowing: shut down cleanly and report it, instead of claiming "Connected". Needs <see cref="op"/>.
    async Task GiveUpLockedAsync(string message)
    {
        stopping = true;
        await Task.Run(() =>
        {
            var p = proc;
            proc = null;
            try { if (p != null && !p.HasExited) { p.Kill(); p.WaitForExit(4000); } } catch { }
            KillStrays();
            lock (routeLock) routedTun = -1;
            Recovery.Restore();
            Setup.DropRouteConfig();
        });
        TunId = null;
        Trace = null;
        Route = null;
        Since = DateTime.MinValue;
        Set(TunnelState.Off, message, true);
    }

    async Task GiveUpAsync(string message)
    {
        if (stopping) return;
        await op.WaitAsync();
        try { if (!stopping) await GiveUpLockedAsync(message); }
        finally { op.Release(); }
    }

    /// Every 5 s: switch away from a route that isn't coming up, give up if nothing works for too long,
    /// and while connected, make sure traffic really flows (every ~30 s).
    async Task HealthTickAsync()
    {
        if (stopping || Interlocked.Exchange(ref healthBusy, 1) == 1) return;
        try
        {
            if (Trying)
            {
                if (Net.FindPhysical(endpoint) == null)
                {
                    busySince = DateTime.Now;   // being offline isn't WARP's fault: don't run down the clock
                    trySince = DateTime.Now;
                    Hint("Waiting for a network…");
                    return;
                }
                if (busySince != DateTime.MinValue && (DateTime.Now - busySince).TotalSeconds > GiveUpAfterSeconds)
                {
                    await GiveUpAsync("Couldn't get WARP working on this network right now (the connection kept getting cut). Your normal internet is back. Try again in a minute.");
                    return;
                }
                if (!verifying && (DateTime.Now - trySince).TotalSeconds > RouteTimeoutSeconds)
                    await SwitchAsync(pathGen, "no answer on this route");
                return;
            }
            if (State != TunnelState.Connected || ++healthTicks % 6 != 0) return;
            // Three quick probes must all fail before we call the tunnel dead (avoids false alarms).
            int gen = pathGen;
            for (int i = 0; i < 3; i++)
            {
                if (stopping || State != TunnelState.Connected) return;
                if (await Net.ProbeAsync()) return;
            }
            if (stopping || State != TunnelState.Connected) return;
            Log.Add("· the tunnel stopped passing traffic");
            await SwitchAsync(gen, "the tunnel stopped passing traffic");
        }
        finally { healthBusy = 0; }
    }

    void OnExited(Process p, int gen)
    {
        if (stopping || p != proc) return;
        Log.Add("· WARP's helper exited unexpectedly: " + Friendly(Log.LastToolLine()));
        _ = SwitchAsync(gen, "WARP's helper stopped");
    }

    // ---------------------------------------------------------------- disconnect

    public async Task DisconnectAsync()
    {
        if (State is TunnelState.Off or TunnelState.Stopping) return;
        stopping = true;
        Set(TunnelState.Stopping, "Disconnecting…");
        await op.WaitAsync();
        try
        {
            await Task.Run(() =>
            {
                var p = proc;
                proc = null;
                try { if (p != null && !p.HasExited) { p.Kill(); p.WaitForExit(4000); } } catch { }
                KillStrays();
                lock (routeLock) routedTun = -1;
                Recovery.Restore();
                Setup.DropRouteConfig();
            });
            TunId = null;
            Trace = null;
            Route = null;
            Since = DateTime.MinValue;
            Set(TunnelState.Off);
        }
        finally { op.Release(); }
    }

    /// Synchronous last-chance cleanup for Windows shutdown / sign-out.
    public void ShutdownNow()
    {
        stopping = true;
        try { proc?.Kill(); } catch { }
        KillStrays();
        Recovery.Restore();
        Setup.DropRouteConfig();
    }

    // ---------------------------------------------------------------- network changes

    /// Joined a different network (or plugged in Ethernet): scout it and start on its best route.
    /// Same network, new address: just re-pin. No network: wait.
    void OnNetworkChanged()
    {
        if (stopping || endpoint == null || State is not (TunnelState.Connecting or TunnelState.Connected or TunnelState.Reconnecting)) return;
        Phys now;
        string name;
        lock (routeLock)
        {
            now = Net.FindPhysical(endpoint);
            if (now == null)
            {
                if (State == TunnelState.Connected) Set(TunnelState.Reconnecting, "Waiting for a network…");
                return;
            }
            if (now.SameAs(phys)) return;
            name = Net.NetworkName(now);
            if (name == Network && phys != null && now.Id == phys.Id)
            {
                Log.Add("· network address changed: now via " + now.Gateway);
                if (Route != null) { Recovery.Remember(Route.Ip, now); Net.PinEndpoint(Route.Ip, now); }
                phys = now;
                return;
            }
        }
        _ = RepathAsync(now, name);
    }

    async Task RepathAsync(Phys now, string name)
    {
        await op.WaitAsync();
        try
        {
            if (stopping) return;
            Log.Add("· network changed: now " + now.Name + " (" + name + ") via " + now.Gateway);
            await Task.Run(Recovery.DropLan);
            phys = now;
            Network = name;
            switches = 0;
            Trace = null;
            Set(TunnelState.Reconnecting, "New network. Scouting routes through " + name + "…");
            await ScoutAsync(now);
            if (stopping) return;
            await Task.Run(() =>
            {
                Recovery.RememberLan(Net.BypassLan(now), now);
                RestartOn(plan[0], now);
            });
            Set(TunnelState.Reconnecting, "Connecting through " + plan[0].Short + "…");
        }
        finally { op.Release(); }
    }

    static bool LooksBad(string msg)
    {
        string m = msg.ToLowerInvariant();
        return m.Contains("fail") || m.Contains("error") || m.Contains("timeout") || m.Contains("refused") || m.Contains("unreachable") || m.Contains("reset");
    }

    static string Friendly(string msg)
    {
        string m = (msg ?? "").ToLowerInvariant();
        if (m.Contains("forcibly closed") || m.Contains("reset by peer") || m.Contains("connection reset") || m.Contains("eof")) return "The network cut the connection. Retrying…";
        if (m.Contains("timeout") || m.Contains("timed out")) return "WARP isn't answering. Retrying…";
        if (m.Contains("no such host") || m.Contains("lookup")) return "Couldn't look up WARP's address. Retrying…";
        if (m.Contains("unreachable")) return "No route to WARP. Check your Wi-Fi. Retrying…";
        if (m.Contains("certificate")) return "Something on this network is intercepting the connection. Trying another route…";
        if (m.Contains("wintun") || m.Contains("adapter")) return "Couldn't create the tunnel adapter. " + Trim(msg);
        return Trim(msg);
    }

    static string Trim(string s) => string.IsNullOrEmpty(s) ? "" : s.Length <= 110 ? s : s.Substring(0, 107) + "…";
}
