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

    const int MaxSwitches = 8;              // route changes without a good connection before giving up honestly
    const int GiveUpAfterSeconds = 150;     // backstop for being stuck trying (only counts while a network exists)
    const int RouteTimeoutSeconds = 15;     // a route that hasn't come up this long after usque starts dialing is blocked
    const int StartupTimeoutSeconds = 40;   // usque itself hasn't started dialing by now: a local problem, not the route
    const int FlapWindowSeconds = 180;      // this many drops inside the window = the network keeps cutting this route
    const int FlapDrops = 3;
    const int HealthEverySeconds = 5;

    readonly IntPtr job = Native.CreateKillOnCloseJob();
    readonly SemaphoreSlim op = new(1, 1);   // one connect/switch/network change/disconnect at a time
    readonly object routeLock = new();
    readonly Timer netDebounce;
    readonly Timer health;
    readonly Queue<DateTime> drops = new();
    readonly Settings settings;
    Process proc;
    IDisposable procLocks;
    Phys phys;
    string endpoint;
    byte[] pinnedKey;
    int routedTun = -1;
    volatile bool stopping, verifying, suspended, killSwitchOn;
    CancellationTokenSource startCancel;
    string lastIdleNetwork;

    List<WarpPath> plan = new();
    int planIdx, pathGen, pathFails, switches, upGeneration, healthTicks, healthBusy, localFailures;
    // trySince: when we started waiting for the current route to work (first dial, or the last drop).
    // MinValue while usque hasn't started dialing yet; spawnedAt says when it was launched.
    DateTime busySince = DateTime.MinValue, trySince = DateTime.MinValue, spawnedAt = DateTime.MinValue;
    DateTime lastTick = DateTime.MinValue, suspendedAt = DateTime.MinValue;

    public TunnelState State { get; private set; }
    public string Detail { get; private set; } = "";
    public bool DetailIsError { get; private set; }
    public TraceInfo Trace { get; private set; }
    public DateTime Since { get; private set; }
    public string TunId { get; private set; }
    public WarpPath Route { get; private set; }
    public string Network { get; private set; }
    public int Reroutes { get; private set; }     // route changes this session (shown in the UI)
    public int PingMs { get; private set; } = -1; // round trip through WARP to 1.1.1.1
    public string KillSwitchWarning { get; private set; }
    public bool OnBackupServer => Route != null && endpoint != null && Route.Ip != endpoint;
    public readonly LogBuffer Log = new(500, Path.Combine(Setup.Dir, "app.log"));
    public event Action Changed;
    /// While WARP is off: the PC joined another network (its name). For "connect on untrusted networks".
    public event Action<string> IdleNetworkChanged;

    public bool Busy => State is TunnelState.Starting or TunnelState.Connecting or TunnelState.Reconnecting or TunnelState.Stopping;
    bool Trying => State is TunnelState.Connecting or TunnelState.Reconnecting;

    public Tunnel(Settings settings)
    {
        this.settings = settings;
        Net.Log = Log.Add;
        netDebounce = new Timer(_ => _ = NetworkChangedAsync());
        NetworkChange.NetworkAddressChanged += (s, e) => netDebounce.Change(2500, Timeout.Infinite);
        health = new Timer(_ => _ = HealthTickAsync(), null, HealthEverySeconds * 1000, HealthEverySeconds * 1000);
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

    /// Undo leftovers from a crash, a power cut or the old .bat buttons. Returns true if a tunnel was running.
    public bool RecoverOnStartup()
    {
        bool wasRunning = KillStrays();
        if (Recovery.Pending) Recovery.Restore();
        else if (wasRunning) LegacyCleanup();
        KillSwitch.Remove();   // in case the record of it was lost
        Setup.DropRouteConfig();
        return wasRunning;
    }

    void LegacyCleanup()
    {
        string ep = File.Exists(Setup.ConfigPath) ? Setup.ReadEndpoint() : "162.159.198.2";
        Net.UnpinEndpoint(ep);
        var p = Net.FindPhysical();
        if (p != null && Net.IsCloudflareDns(Net.ReadStaticDns(p.Id))) Net.RestoreDns(p.Id, false, "");
        Native.DnsFlushResolverCache();
    }

    /// usque copies started by this app (now or by an older version) or by the old .bat buttons. Anything else
    /// named usque.exe belongs to someone else and is left alone.
    static bool KillStrays()
    {
        var ours = new[]
        {
            Setup.BinDir, Setup.Dir,
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "usque"),
        }.Select(d => d.TrimEnd('\\') + "\\").ToArray();
        bool any = false;
        foreach (var p in Process.GetProcessesByName("usque"))
        {
            try
            {
                string path = null;
                try { path = p.MainModule?.FileName; } catch { }
                if (path != null && !ours.Any(d => path.StartsWith(d, StringComparison.OrdinalIgnoreCase))) continue;
                any = true;
                p.Kill();
                p.WaitForExit(4000);
            }
            catch { }
            finally { p.Dispose(); }
        }
        return any;
    }

    /// The network the PC is on right now (for "trust this network").
    public static string CurrentNetwork()
    {
        try { return Net.NetworkName(Net.FindPhysical()); } catch { return null; }
    }

    // ---------------------------------------------------------------- connect

    public async Task ConnectAsync()
    {
        if (State != TunnelState.Off) return;
        await op.WaitAsync();
        var cancel = new CancellationTokenSource();
        try
        {
            if (State != TunnelState.Off) return;
            startCancel = cancel;
            stopping = false;
            switches = 0;
            localFailures = 0;
            Reroutes = 0;
            Route = null;
            PingMs = -1;
            KillSwitchWarning = null;
            ResetUsage();
            Set(TunnelState.Starting, "Getting ready…");
            await Task.Run(() =>
            {
                KillStrays();
                Setup.EnsureBinaries();
                Setup.MigrateOldConfig();
                if (!File.Exists(Setup.ConfigPath))
                {
                    Set(TunnelState.Starting, "Registering this PC with WARP…");
                    Setup.Register(cancel.Token);
                }
                endpoint = Setup.ReadEndpoint();
                pinnedKey = Setup.ReadPinnedKey();
                phys = Net.FindPhysical() ?? throw new UserError("No internet connection. Join a Wi-Fi network first.");
                Network = Net.NetworkName(phys);
                Log.Add("· uplink: " + phys.Name + " (" + Network + ") via " + phys.Gateway + ", endpoint " + endpoint);
            }, cancel.Token);
            if (stopping) return;
            Set(TunnelState.Starting, "Scouting routes through " + Network + "…");
            var scout = await ScoutAsync(phys);
            if (stopping) return;
            if (scout.NothingAnswered && Net.BehindLoginPage(phys))
            {
                OpenLoginPage();
                throw new UserError("This Wi-Fi needs you to sign in first. The sign-in page should open; connect again after.");
            }
            await Task.Run(() =>
            {
                Recovery.RememberDns(phys);
                Net.BypassRoutes(phys, settings.Bypass);
                Net.PinEndpoint(plan[0].Ip, phys);
            });
            if (stopping) return;
            Since = DateTime.MinValue;
            Set(TunnelState.Connecting, "Connecting through " + plan[0].Short + "…");
            await Task.Run(() => StartUsque(plan[0]));
        }
        catch (Exception ex)
        {
            if (stopping) return;   // cancelled: DisconnectAsync cleans up once we let go of op
            await Task.Run(CleanupNetwork);
            ReportError(ex);
        }
        finally
        {
            startCancel = null;
            cancel.Dispose();
            op.Release();
        }
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
        var locks = Setup.LockBinaries();   // verified, and nothing can swap them until usque is gone
        var p = new Process
        {
            StartInfo = new ProcessStartInfo(Setup.UsquePath, path.Args(settings.Mtu, config, Setup.ConfigPath))
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
        trySince = DateTime.MinValue;
        spawnedAt = DateTime.Now;
        Route = path;
        Log.Add("· route: " + path);
        Interlocked.Exchange(ref procLocks, locks)?.Dispose();
        proc = p;   // before Start: if usque dies instantly, OnExited must recognise it as the current process
        try { p.Start(); }
        catch
        {
            proc = null;
            Interlocked.Exchange(ref procLocks, null)?.Dispose();
            p.Dispose();
            throw;
        }
        Native.AssignProcessToJobObject(job, p.Handle);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
    }

    void OnLine(string line, int gen)
    {
        // usque logs every multicast/TTL-1 packet it can't forward; that's noise.
        if (string.IsNullOrEmpty(line) || line.Contains("dropping proxied packet")) return;
        string msg = Timestamp.Replace(line, "");
        Log.AddCollapsed(msg);
        if (stopping || gen != pathGen) return;   // a route we've already moved away from

        if (msg.StartsWith("Establishing MASQUE connection"))
        {
            // The route clock starts when usque starts dialing (not when it was launched), and restarts when a
            // working connection re-dials: an old start time would make the next health check switch at once.
            if (trySince == DateTime.MinValue || State == TunnelState.Connected) trySince = DateTime.Now;
            if (State == TunnelState.Connected) Set(TunnelState.Reconnecting, "Connection dropped, reconnecting…");
        }
        else if (msg.Contains("Connected to MASQUE server"))
        {
            localFailures = 0;
            Task.Run(() => OnTunnelUp(gen));
        }
        else if (msg.StartsWith("Failed to connect tunnel"))
        {
            if (suspended) return;
            // One refusal can be a blip; two in a row on the same route means this route is blocked here.
            if (Interlocked.Increment(ref pathFails) >= 2) _ = SwitchAsync(gen, "this route keeps getting refused");
            else if (Trying) Hint(Friendly(msg));
        }
        else if (msg.StartsWith("Tunnel connection lost"))
        {
            trySince = DateTime.Now;
            if (State == TunnelState.Connected) Set(TunnelState.Reconnecting, "Connection dropped, reconnecting…");
            if (suspended) return;
            int recent;
            lock (drops)
            {
                drops.Enqueue(DateTime.Now);
                while (drops.Count > 0 && (DateTime.Now - drops.Peek()).TotalSeconds > FlapWindowSeconds) drops.Dequeue();
                recent = drops.Count;
            }
            if (recent >= FlapDrops) _ = SwitchAsync(gen, "the network keeps cutting this route");
        }
        else if (Trying && LooksBad(msg)) Hint(Friendly(msg));
    }

    void OnTunnelUp(int gen)
    {
        try
        {
            lock (routeLock)
            {
                if (stopping || gen != pathGen) return;
                NetworkInterface tun = null;
                for (int i = 0; i < 50 && (tun = Net.Tun()) == null; i++) Thread.Sleep(100);
                if (tun == null) { Log.Add("· tunnel adapter not found"); return; }
                int idx = Net.IndexOf(tun);
                TunId = tun.Id;
                if (idx != routedTun || !Net.RoutesPresent(idx, phys?.HasIPv6 == true)) ApplyRouting(tun, idx);
            }
            if (stopping) return;
            // usque's handshake succeeding doesn't prove packets flow (the network can still cut them),
            // so stay in Connecting/Reconnecting until a real request comes back through the tunnel.
            int up = Interlocked.Increment(ref upGeneration);
            Set(State == TunnelState.Connecting ? TunnelState.Connecting : TunnelState.Reconnecting, "Checking that traffic gets through…");
            _ = VerifyAsync(up, gen);
        }
        catch (Exception ex) { Log.Add("· setting up routing failed: " + ex.GetBaseException().Message); }
    }

    void ApplyRouting(NetworkInterface tun, int tunIndex)
    {
        bool v6 = phys != null && phys.HasIPv6;
        Net.RouteAllThrough(tunIndex, v6);
        Net.SetCloudflareDns(tun.Id, tunIndex);
        if (phys != null) Net.SetCloudflareDns(phys.Id, phys.Index);
        Native.DnsFlushResolverCache();
        routedTun = tunIndex;
        Log.Add("· routing all traffic through the tunnel (if " + tunIndex + (v6 ? ", IPv4 + IPv6" : ", IPv4") + ")");
    }

    /// Ask Cloudflare what it sees through the tunnel. Only a real answer with warp=on counts as connected.
    async Task VerifyAsync(int up, int gen)
    {
        verifying = true;
        try
        {
            // Windows takes a moment to start using fresh routes; checking before then just fails "unreachable".
            for (int i = 0; i < 15 && routedTun > 0 && !Net.RoutesInEffect(routedTun); i++) await Task.Delay(200);
            string[] hosts = { "1.1.1.1", "1.0.0.1", "1.1.1.1", "www.cloudflare.com", "1.0.0.1" };
            int quickRetries = 0;
            for (int attempt = 0; attempt < hosts.Length;)
            {
                if (stopping || up != upGeneration || gen != pathGen) return;
                try
                {
                    var t = await Net.TraceAsync(hosts[attempt]);
                    Log.Add("· check: ip=" + t.Ip + " loc=" + t.Loc + " colo=" + t.Colo + " warp=" + (t.Warp ? "on" : "off"));
                    if (stopping || up != upGeneration || gen != pathGen) return;
                    if (t.Warp)
                    {
                        await OnVerifiedAsync(t);
                        return;
                    }
                    lock (routeLock)
                    {
                        var tun = Net.Tun();
                        if (!stopping && routedTun > 0 && tun != null) ApplyRouting(tun, routedTun);
                    }
                }
                catch (Exception ex)
                {
                    Log.Add("· check failed: " + ex.GetBaseException().Message);
                    // Routes still settling: try again shortly rather than burning an attempt.
                    if (Net.IsUnreachable(ex) && quickRetries++ < 8) { await Task.Delay(300); continue; }
                }
                attempt++;
                await Task.Delay(1500);
            }
        }
        finally { if (up == upGeneration) verifying = false; }
        if (stopping || up != upGeneration) return;
        await SwitchAsync(gen, "WARP's server answered, but no traffic came back through this route");
    }

    async Task OnVerifiedAsync(TraceInfo t)
    {
        Trace = t;
        switches = 0;
        pathFails = 0;
        localFailures = 0;
        // Windows may only have identified the network by now (it takes a few seconds after joining one).
        string name = await Task.Run(() => Net.NetworkName(phys));
        if (!string.IsNullOrEmpty(name)) Network = name;
        PathMemory.Set(Network, Route);
        if (Since == DateTime.MinValue) Since = DateTime.Now;
        bool first = State == TunnelState.Connecting;
        await Task.Run(() =>
        {
            if (settings.EncryptDns && first) SecureDns.Enable();
            if (first && settings.Bypass.Count > 0) Net.BypassRoutes(phys, settings.Bypass);   // host names, now via clean DNS
            UpdateKillSwitch();
        });
        if (stopping) return;
        Set(TunnelState.Connected);
        _ = MeasurePingAsync();
    }

    async Task MeasurePingAsync()
    {
        int ms = await Net.ProbeAsync();
        if (State != TunnelState.Connected) return;
        if (ms >= 0) PingMs = ms;
        Changed?.Invoke();
    }

    // ---------------------------------------------------------------- kill switch

    /// Turns the firewall block on (once a connection has been proven) or keeps it pointed at the current adapter.
    void UpdateKillSwitch()
    {
        if (!settings.KillSwitch || phys == null)
        {
            if (killSwitchOn || KillSwitch.Active) { KillSwitch.Remove(); killSwitchOn = false; Log.Add("· kill switch off"); }
            return;
        }
        if (State is not (TunnelState.Connecting or TunnelState.Connected or TunnelState.Reconnecting)) return;
        try
        {
            var allowed = new List<string> { endpoint + "/32" };
            if (Route != null) allowed.Add(Route.Ip + "/32");
            allowed.AddRange(Recovery.RoutedAround());
            KillSwitchWarning = KillSwitch.Apply(phys.Name, allowed);
            if (!killSwitchOn) Log.Add("· kill switch on for " + phys.Name + (KillSwitchWarning != null ? " (" + KillSwitchWarning + ")" : ""));
            killSwitchOn = true;
        }
        catch (Exception ex)
        {
            KillSwitchWarning = "The kill switch couldn't be turned on: " + ex.GetBaseException().Message;
            Log.Add("· " + KillSwitchWarning);
        }
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
            var now = await Task.Run(Net.FindPhysical);
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
                if (!killSwitchOn)
                {
                    await GiveUpLockedAsync($"This network blocked or cut all {MaxSwitches} routes to WARP. Your normal internet is back; try again soon.");
                    return;
                }
                // With the kill switch on, giving up would leave the PC unprotected: keep looking instead.
                switches = 0;
                planIdx = plan.Count;
                busySince = DateTime.Now;
            }
            Reroutes++;
            if (!now.SameAs(phys))
            {
                phys = now;
                Network = Net.NetworkName(now);
            }
            if (++planIdx >= plan.Count)
            {
                Set(TunnelState.Reconnecting, killSwitchOn ? "Kill switch on: internet is paused until WARP gets through. Looking for fresh routes…" : "Looking for fresh routes…");
                await ScoutAsync(now);
                if (stopping) return;
            }
            var next = plan[planIdx];
            Log.Add("· " + reason + "; switching to " + next + " (" + switches + "/" + MaxSwitches + ")");
            if (stopping) return;
            Set(TunnelState.Reconnecting, "Route blocked. Switching to " + next.Short + "…");
            await Task.Run(() => RestartOn(next, now));
        }
        catch (Exception ex)
        {
            if (stopping) return;
            Log.Add("· switching routes failed: " + ex);
            await GiveUpLockedAsync(ex is UserError ? ex.Message : "WARP stopped unexpectedly: " + ex.GetBaseException().Message + " Your normal internet is back.");
        }
        finally { op.Release(); }
    }

    /// usque never got as far as dialing (it crashed at launch or hung): that's this PC, not the network, so try
    /// the same route once more and then stop with the real reason instead of blaming eight routes in turn.
    async Task LocalFailureAsync(int gen, string why)
    {
        if (stopping) return;
        await op.WaitAsync();
        try
        {
            if (stopping || gen != pathGen) return;
            if (++localFailures >= 2)
            {
                Log.Add("· giving up: WARP's helper couldn't start (" + why + ")");
                await GiveUpLockedAsync("WARP's helper couldn't start: " + (why.Length > 60 ? why.Substring(0, 57).TrimEnd() + "…" : why) + " Details are in the log.");
                return;
            }
            Log.Add("· WARP's helper didn't start properly (" + why + "); restarting it on the same route");
            Hint("Restarting WARP's helper…");
            var route = Route ?? plan[planIdx];
            var uplink = phys ?? await Task.Run(Net.FindPhysical);
            if (uplink == null) return;
            await Task.Run(() => RestartOn(route, uplink));
        }
        catch (Exception ex)
        {
            if (!stopping) await GiveUpLockedAsync(ex is UserError ? ex.Message : "WARP's helper couldn't start: " + ex.GetBaseException().Message);
        }
        finally { op.Release(); }
    }

    void RestartOn(WarpPath next, Phys uplink)
    {
        var old = proc;
        proc = null;   // makes OnExited ignore this deliberate stop
        Stop(old);
        KillStrays();
        lock (routeLock) routedTun = -1;
        TunId = null;
        string oldIp = Route?.Ip;
        Recovery.RememberDns(uplink);
        Net.PinEndpoint(next.Ip, uplink);
        // The previous server's pin isn't needed any more (they used to pile up for the whole session).
        if (oldIp != null && oldIp != next.Ip)
        {
            Net.UnpinEndpoint(oldIp);
            Recovery.Forget("pin:" + oldIp);
        }
        StartUsque(next);
    }

    void Stop(Process p)
    {
        if (p != null)
        {
            try { if (!p.HasExited) { p.Kill(); p.WaitForExit(4000); } } catch { }
            p.Dispose();
        }
        Interlocked.Exchange(ref procLocks, null)?.Dispose();
    }

    /// Kill usque and undo every change to the network (routes, DNS, kill switch, encrypted DNS).
    void CleanupNetwork()
    {
        var p = proc;
        proc = null;
        Stop(p);
        KillStrays();
        lock (routeLock) routedTun = -1;
        killSwitchOn = false;
        KillSwitch.Remove();
        Recovery.Restore();
        Setup.DropRouteConfig();
    }

    /// Couldn't get traffic flowing: shut down cleanly and report it, instead of claiming "Connected". Needs <see cref="op"/>.
    async Task GiveUpLockedAsync(string message)
    {
        stopping = true;
        await Task.Run(CleanupNetwork);
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

    /// Every 5 s: switch away from a route that isn't coming up, give up if nothing works for too long, and while
    /// connected keep the routes in place and make sure traffic really flows (every ~30 s).
    async Task HealthTickAsync()
    {
        if (Interlocked.Exchange(ref healthBusy, 1) == 1) return;
        try
        {
            var now = DateTime.Now;
            // A long gap between ticks means the PC was asleep (the process was frozen) whether or not Windows told us.
            if (lastTick != DateTime.MinValue && (now - lastTick).TotalSeconds > HealthEverySeconds * 6) Woke("the PC was asleep for " + (int)(now - lastTick).TotalMinutes + " min");
            else if (suspended && (now - suspendedAt).TotalSeconds > 90) Woke("no resume notice arrived");
            lastTick = now;
            if (stopping || suspended) return;

            if (Trying)
            {
                if (Net.FindPhysical() == null)
                {
                    // Being offline isn't WARP's fault: don't run down the clocks.
                    busySince = now;
                    if (trySince != DateTime.MinValue) trySince = now;
                    spawnedAt = now;
                    Hint("Waiting for a network…");
                    return;
                }
                if (busySince != DateTime.MinValue && (now - busySince).TotalSeconds > GiveUpAfterSeconds)
                {
                    if (killSwitchOn)
                    {
                        busySince = now;
                        Hint("Kill switch on: internet is paused until WARP gets through. Still trying…");
                    }
                    else
                    {
                        await GiveUpAsync("WARP kept getting cut off on this network. Your normal internet is back; try again soon.");
                        return;
                    }
                }
                if (verifying) return;
                if (trySince == DateTime.MinValue)
                {
                    if (spawnedAt != DateTime.MinValue && (now - spawnedAt).TotalSeconds > StartupTimeoutSeconds)
                        await LocalFailureAsync(pathGen, "it didn't start connecting within " + StartupTimeoutSeconds + " seconds.");
                }
                else if ((now - trySince).TotalSeconds > RouteTimeoutSeconds)
                    await SwitchAsync(pathGen, "no answer on this route");
                return;
            }
            if (State != TunnelState.Connected) return;
            healthTicks++;

            // Leak watchdog: if something removed our routes (another VPN, a network reset), traffic would quietly
            // go around the tunnel while we still said "Connected".
            lock (routeLock)
            {
                bool v6 = phys?.HasIPv6 == true;
                if (routedTun > 0 && !stopping && !Net.RoutesPresent(routedTun, v6))
                {
                    Log.Add("· routes into the tunnel were missing; putting them back");
                    Net.RouteAllThrough(routedTun, v6);
                }
            }

            if (healthTicks % 6 != 0)
            {
                if (healthTicks % 2 == 0) await MeasurePingAsync();
                return;
            }
            // Three quick probes must all fail before we call the tunnel dead (avoids false alarms).
            int gen = pathGen;
            for (int i = 0; i < 3; i++)
            {
                if (stopping || suspended || State != TunnelState.Connected) return;
                int ms = await Net.ProbeAsync();
                if (ms >= 0) { PingMs = ms; return; }
            }
            if (stopping || suspended || State != TunnelState.Connected) return;
            Log.Add("· the tunnel stopped passing traffic");
            await SwitchAsync(gen, "the tunnel stopped passing traffic");
        }
        catch (Exception ex) { Log.Add("· health check failed: " + ex.GetBaseException().Message); }
        finally { healthBusy = 0; }
    }

    void OnExited(Process p, int gen)
    {
        if (stopping || p != proc) return;
        string last = Friendly(Log.LastToolLine());
        Log.Add("· WARP's helper exited unexpectedly: " + last);
        // Never got as far as dialing: a problem on this PC (wintun, antivirus, config), not the route.
        if (trySince == DateTime.MinValue) _ = LocalFailureAsync(gen, string.IsNullOrEmpty(last) ? "it closed right away." : last);
        else _ = SwitchAsync(gen, "WARP's helper stopped");
    }

    // ---------------------------------------------------------------- sleep / wake

    public void Suspend()
    {
        suspended = true;
        suspendedAt = DateTime.Now;
        Log.Add("· the PC is going to sleep");
    }

    public void Resume() => Woke("Windows says the PC woke up");

    /// After sleep, every clock that measures "how long have we been trying" is meaningless: on 10-14 the app gave
    /// up 25 s after waking because it counted 2h23m of sleep as failed reconnecting. Reset them, forget drops
    /// that happened while the network went down for sleep, and look at the network afresh.
    void Woke(string why)
    {
        bool was = suspended;
        suspended = false;
        var now = DateTime.Now;
        lastTick = now;
        if (State == TunnelState.Off && !was) return;
        Log.Add("· woke up (" + why + ")");
        if (busySince != DateTime.MinValue) busySince = now;
        if (trySince != DateTime.MinValue) trySince = now;
        if (spawnedAt != DateTime.MinValue) spawnedAt = now;
        lock (drops) drops.Clear();
        switches = 0;
        pathFails = 0;
        healthTicks = 0;
        netDebounce.Change(3000, Timeout.Infinite);   // the PC may wake up somewhere else
    }

    // ---------------------------------------------------------------- disconnect

    public async Task DisconnectAsync()
    {
        if (State is TunnelState.Off or TunnelState.Stopping) return;
        bool cancelling = State == TunnelState.Starting;
        stopping = true;
        try { startCancel?.Cancel(); } catch (ObjectDisposedException) { }
        Set(TunnelState.Stopping, cancelling ? "Cancelling…" : "Disconnecting…");
        await op.WaitAsync();
        try
        {
            await Task.Run(CleanupNetwork);
            TunId = null;
            Trace = null;
            Route = null;
            PingMs = -1;
            Since = DateTime.MinValue;
            Set(TunnelState.Off);
        }
        finally { op.Release(); }
    }

    /// Synchronous last-chance cleanup for Windows shutdown / sign-out, a crash, or quitting.
    public void ShutdownNow()
    {
        stopping = true;
        try { proc?.Kill(); } catch { }
        try { CleanupNetwork(); } catch { }
    }

    /// Settings that act on a live connection (kill switch, encrypted DNS, bypass list) were changed.
    public async Task ApplySettingsAsync()
    {
        if (State == TunnelState.Off) return;
        await op.WaitAsync();
        try
        {
            if (stopping || State is TunnelState.Off or TunnelState.Starting or TunnelState.Stopping || phys == null) return;
            await Task.Run(() =>
            {
                Recovery.DropRoutes();
                Net.BypassRoutes(phys, settings.Bypass);
                if (settings.EncryptDns) SecureDns.Enable();
                else foreach (var server in new[] { "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001" })
                    RestoreDoh(server);
                if (State == TunnelState.Connected || killSwitchOn || !settings.KillSwitch) UpdateKillSwitch();
            });
            Changed?.Invoke();
        }
        catch (Exception ex) { Log.Add("· applying settings failed: " + ex.GetBaseException().Message); }
        finally { op.Release(); }
    }

    static void RestoreDoh(string server)
    {
        // Only servers we changed have a record; the record goes once they're back.
        var key = "doh:" + server;
        string recorded = Recovery.Get(key);
        if (recorded == null) return;
        SecureDns.Restore(server, recorded);
        Recovery.Forget(key);
    }

    // ---------------------------------------------------------------- network changes

    /// Joined a different network (or plugged in Ethernet): scout it and start on its best route.
    /// Same network, new gateway: re-pin and move the LAN shortcuts over. No network: wait.
    async Task NetworkChangedAsync()
    {
        try
        {
            if (State == TunnelState.Off) { CheckIdleNetwork(); return; }
            if (stopping || suspended || endpoint == null || State is not (TunnelState.Connecting or TunnelState.Connected or TunnelState.Reconnecting)) return;
            await op.WaitAsync();
            try
            {
                if (stopping || State is not (TunnelState.Connecting or TunnelState.Connected or TunnelState.Reconnecting)) return;
                var now = await Task.Run(Net.FindPhysical);
                if (now == null)
                {
                    if (State == TunnelState.Connected) Set(TunnelState.Reconnecting, "Waiting for a network…");
                    return;
                }
                if (now.SameAs(phys)) return;
                string name = await Task.Run(() => Net.NetworkName(now));
                if (phys != null && now.Id == phys.Id && name == Network)
                {
                    Log.Add("· network address changed: now via " + now.Gateway);
                    await Task.Run(() =>
                    {
                        if (Route != null) Net.PinEndpoint(Route.Ip, now);
                        Recovery.DropRoutes();
                        Net.BypassRoutes(now, settings.Bypass);
                    });
                    phys = now;
                    if (killSwitchOn) await Task.Run(UpdateKillSwitch);
                    return;
                }

                Log.Add("· network changed: now " + now.Name + " (" + name + ") via " + now.Gateway);
                await Task.Run(Recovery.DropRoutes);
                phys = now;
                Network = name;
                switches = 0;
                Trace = null;
                Set(TunnelState.Reconnecting, "New network. Scouting routes through " + name + "…");
                await ScoutAsync(now);
                if (stopping) return;
                await Task.Run(() =>
                {
                    Net.BypassRoutes(now, settings.Bypass);
                    RestartOn(plan[0], now);
                    if (killSwitchOn) UpdateKillSwitch();
                });
                Set(TunnelState.Reconnecting, "Connecting through " + plan[0].Short + "…");
            }
            finally { op.Release(); }
        }
        catch (Exception ex)
        {
            // This runs on a timer thread: an exception escaping here used to be able to take the whole app down.
            Log.Add("· handling the network change failed: " + ex.GetBaseException().Message);
        }
    }

    void CheckIdleNetwork()
    {
        string name = CurrentNetwork();
        if (name == lastIdleNetwork) return;
        lastIdleNetwork = name;
        if (name != null) IdleNetworkChanged?.Invoke(name);
    }

    // ---------------------------------------------------------------- data used

    readonly object usageLock = new();
    long usageBaseIn, usageBaseOut, usageLastIn, usageLastOut;
    string usageTunId;
    NetworkInterface usageNic;

    void ResetUsage()
    {
        lock (usageLock) { usageBaseIn = usageBaseOut = usageLastIn = usageLastOut = 0; usageTunId = null; usageNic = null; }
    }

    /// Bytes through the tunnel this session. Every route switch gets a fresh adapter whose counters start at
    /// zero, so earlier adapters' totals are carried over.
    public (long In, long Out) Usage()
    {
        lock (usageLock)
        {
            string id = TunId;
            if (id != usageTunId)
            {
                usageBaseIn += usageLastIn;
                usageBaseOut += usageLastOut;
                usageLastIn = usageLastOut = 0;
                usageTunId = id;
                usageNic = id == null ? null : NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Id == id);
            }
            if (usageNic != null)
            {
                try
                {
                    var s = usageNic.GetIPStatistics();
                    usageLastIn = s.BytesReceived;
                    usageLastOut = s.BytesSent;
                }
                catch (NetworkInformationException) { usageNic = null; }
            }
            return (usageBaseIn + usageLastIn, usageBaseOut + usageLastOut);
        }
    }

    /// Puts the tunnel in a made-up state for screenshots and UI tests (nothing is started).
    internal void Preview(TunnelState state, string detail = "", bool error = false, TraceInfo trace = null, WarpPath route = null,
        string network = null, TimeSpan uptime = default, int ping = -1, int reroutes = 0)
    {
        State = state;
        Detail = detail;
        DetailIsError = error;
        Trace = trace;
        Route = route;
        Network = network;
        Since = uptime == default ? DateTime.MinValue : DateTime.Now - uptime;
        PingMs = ping;
        Reroutes = reroutes;
        endpoint = "162.159.198.2";
    }

    // ---------------------------------------------------------------- messages

    static bool LooksBad(string msg)
    {
        string m = msg.ToLowerInvariant();
        return m.Contains("fail") || m.Contains("error") || m.Contains("timeout") || m.Contains("refused") || m.Contains("unreachable") || m.Contains("reset");
    }

    internal static string Friendly(string msg)
    {
        string m = (msg ?? "").ToLowerInvariant();
        if (m.Contains("forcibly closed") || m.Contains("reset by peer") || m.Contains("connection reset") || m.Contains("eof")
            || m.Contains("closed pipe") || m.Contains("connection was aborted")) return "The network cut the connection. Retrying…";
        if (m.Contains("timeout") || m.Contains("timed out") || m.Contains("failed to respond") || m.Contains("did not properly respond")) return "WARP isn't answering. Retrying…";
        if (m.Contains("no such host") || m.Contains("lookup")) return "Couldn't look up WARP's address. Retrying…";
        if (m.Contains("unreachable")) return "No route to WARP. Check your Wi-Fi. Retrying…";
        if (m.Contains("certificate")) return "Something on this network is intercepting the connection. Trying another route…";
        if (m.Contains("wintun") || m.Contains("adapter") || m.Contains("tun device")) return "Couldn't create the tunnel adapter. " + Trim(msg);
        return Trim(msg);
    }

    static string Trim(string s) => string.IsNullOrEmpty(s) ? "" : s.Length <= 110 ? s : s.Substring(0, 107) + "…";
}
