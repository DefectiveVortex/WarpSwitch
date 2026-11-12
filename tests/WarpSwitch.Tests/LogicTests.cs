using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using Xunit;

namespace WarpSwitch.Tests;

public class WarpPathTests
{
    [Fact]
    public void KeyRoundTrips()
    {
        var p = new WarpPath { Quic = false, Ip = "162.159.198.2", Port = 8443, Sni = "www.cloudflare.com" };
        var back = WarpPath.Parse(p.Key);
        Assert.Equal(p.Key, back.Key);
        Assert.Equal("TCP 8443", back.Short);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("tcp|not-an-ip|443|www.cloudflare.com")]
    [InlineData("tcp|1.2.3.4|99999|www.cloudflare.com")]
    [InlineData("udp|1.2.3.4|443|www.cloudflare.com")]
    [InlineData("tcp|1.2.3.4|443|evil.com --insecure")]
    [InlineData("tcp|1.2.3.4|443|a\"b")]
    public void RejectsBadOrInjectedKeys(string key) => Assert.Null(WarpPath.Parse(key));

    [Fact]
    public void ArgsForQuicOnDefaultConfig()
    {
        var p = new WarpPath { Quic = true, Ip = "162.159.198.2", Port = 443, Sni = "www.cloudflare.com" };
        Assert.Equal("nativetun -s www.cloudflare.com --always-reconnect", p.Args(1350, @"C:\c.json", @"C:\c.json"));
    }

    [Fact]
    public void ArgsForTcpOnAlternateServer()
    {
        var p = new WarpPath { Quic = false, Ip = "162.159.198.189", Port = 4443, Sni = "www.cloudflare.com" };
        string a = p.Args(1350, @"C:\data\route.json", @"C:\data\config.json");
        Assert.Contains("--http2", a);
        Assert.Contains("-P 4443", a);
        Assert.Contains("-m 1350", a);
        Assert.Contains("-c \"C:\\data\\route.json\"", a);
    }
}

public class PathfinderTests
{
    static WarpPath Tcp(string ip, int port) => new() { Ip = ip, Port = port, Sni = "www.cloudflare.com" };
    static WarpPath Quic(string ip) => new() { Ip = ip, Port = 443, Sni = "www.cloudflare.com", Quic = true };

    [Fact]
    public void RememberedRouteGoesFirstWhenItAnswers()
    {
        var mem = Tcp("162.159.198.2", 8443);
        var c = new List<WarpPath> { mem, Quic("162.159.198.2"), Tcp("162.159.198.2", 443) };
        var plan = Pathfinder.Order(c, new[] { 40, 20, 30 }, mem, "162.159.198.2");
        Assert.Same(mem, plan.Paths[0]);
        Assert.True(plan.QuicOpen);
    }

    [Fact]
    public void QuicBeforeTcpAndSlowTcpLast()
    {
        var fast = Tcp("1.1.1.10", 443); var slow = Tcp("1.1.1.11", 8443); var q = Quic("1.1.1.12");
        var plan = Pathfinder.Order(new List<WarpPath> { fast, slow, q }, new[] { 50, 900, 30 }, null, "1.1.1.10");
        Assert.Equal(new[] { q, fast, slow }, plan.Paths);
    }

    [Fact]
    public void SilentTcpKeptAsLastResortButInterceptedDropped()
    {
        var ok = Tcp("1.1.1.10", 443); var silent = Tcp("1.1.1.11", 500); var mitm = Tcp("1.1.1.12", 8443); var deadQuic = Quic("1.1.1.13");
        var plan = Pathfinder.Order(new List<WarpPath> { ok, silent, mitm, deadQuic }, new[] { 60, -1, Pathfinder.Intercepted, -1 }, null, "1.1.1.10");
        Assert.Equal(new[] { ok, silent }, plan.Paths);
        Assert.True(plan.Intercepted);
        Assert.False(plan.QuicOpen);
    }

    [Fact]
    public void NothingUsableFallsBackToTheEndpoint()
    {
        var plan = Pathfinder.Order(new List<WarpPath> { Quic("1.1.1.13") }, new[] { -1 }, null, "162.159.198.2");
        Assert.True(plan.NothingAnswered);
        Assert.Single(plan.Paths);
        Assert.Equal("162.159.198.2", plan.Paths[0].Ip);
    }

    [Fact]
    public void CandidatesPutMemoryFirstWithoutDuplicates()
    {
        var mem = Tcp("162.159.198.2", 8443);
        var c = Pathfinder.Candidates("162.159.198.2", mem, new Random(1));
        Assert.Same(mem, c[0]);
        Assert.Single(c, x => x.Key == mem.Key);
        Assert.All(c.Where(x => x.Ip != "162.159.198.2"), x => Assert.StartsWith("162.159.19", x.Ip));
    }
}

public class NetTests
{
    [Theory]
    [InlineData("203.0.113.5/24", "203.0.113.0/24", false)]
    [InlineData("1.2.3.4", "1.2.3.4/32", false)]
    [InlineData("  10.0.0.1   # the printer", "10.0.0.1/32", false)]
    [InlineData("Printer.Example.com", "printer.example.com", true)]
    public void ParsesBypassEntries(string line, string expected, bool host)
    {
        Assert.Equal(expected, Net.ParseBypass(line, out bool isHost));
        Assert.Equal(host, isHost);
    }

    [Theory]
    [InlineData("0.0.0.0/0")]
    [InlineData("1.2.3.4/33")]
    [InlineData("300.1.1.1")]
    [InlineData("not a host")]
    [InlineData("# just a comment")]
    [InlineData("")]
    public void RejectsBadBypassEntries(string line) => Assert.Null(Net.ParseBypass(line, out _));

    [Fact]
    public void PicksTheConnectedWifiProfileNotTheFirstSavedOne()
    {
        // The real list from the bug: 15 saved networks on one adapter, the stale campus one first.
        var profiles = new List<(string, bool)> { (null, false), (null, false), ("Student", true), (null, false), (null, false) };
        Assert.Equal(2, Net.PickProfile(profiles));
    }

    [Fact]
    public void FallsBackToTheProfileThatIsUp()
    {
        Assert.Equal(1, Net.PickProfile(new List<(string, bool)> { (null, false), (null, true) }));
        Assert.Equal(0, Net.PickProfile(new List<(string, bool)> { (null, false) }));
        Assert.Equal(-1, Net.PickProfile(new List<(string, bool)> { (null, false), (null, false) }));
    }

    [Fact]
    public void ParsesCidr()
    {
        Assert.Equal((IPAddress.Parse("128.0.0.0"), 1), Net.ParseCidr("128.0.0.0/1"));
        Assert.Equal((IPAddress.Parse("8000::"), 1), Net.ParseCidr("8000::/1"));
        Assert.Equal(32, Net.ParseCidr("1.2.3.4").Length);
    }

    [Fact]
    public void RecognisesCloudflareDns()
    {
        Assert.True(Net.IsCloudflareDns("1.1.1.1,1.0.0.1"));
        Assert.True(Net.IsCloudflareDns("1.1.1.1 1.0.0.1"));
        Assert.False(Net.IsCloudflareDns("192.168.1.1"));
        Assert.False(Net.IsCloudflareDns(""));
    }

    [Fact]
    public void SpotsUnreachableErrors()
    {
        Assert.True(Net.IsUnreachable(new Exception("outer", new SocketException((int)SocketError.HostUnreachable))));
        Assert.False(Net.IsUnreachable(new Exception("outer", new SocketException((int)SocketError.TimedOut))));
    }
}

public class RecoveryTests : IDisposable
{
    readonly string dir = Path.Combine(Path.GetTempPath(), "warpswitch-tests-" + Guid.NewGuid().ToString("N"));
    readonly string saved = Recovery.FilePath;

    public RecoveryTests()
    {
        Directory.CreateDirectory(dir);
        Recovery.FilePath = Path.Combine(dir, "state.ini");
    }

    public void Dispose()
    {
        Recovery.FilePath = saved;
        try { Directory.Delete(dir, true); } catch { }
    }

    [Theory]
    [InlineData("pin:162.159.198.2", "12|10.30.0.1")]
    [InlineData("pin:162.159.198.2", "1")]
    [InlineData("lan:10.0.0.0/8", "12|10.30.0.1")]
    [InlineData("dns:{77C2B6E0-8BD4-4CEA-BF80-BA69FD72155B}", "")]
    [InlineData("dns6:{77C2B6E0-8BD4-4CEA-BF80-BA69FD72155B}", "fd00::1,2001:db8::53")]
    [InlineData("doh:1.1.1.1", "0|0|1|https://cloudflare-dns.com/dns-query")]
    [InlineData("doh:2606:4700:4700::1111", "0|0|0|")]
    [InlineData("kill", "1")]
    [InlineData("endpoint", "162.159.198.2")]
    public void AcceptsWhatTheAppWrites(string key, string value) => Assert.True(Recovery.Valid(key, value));

    [Theory]
    [InlineData("pin:1.2.3.4 mask 0.0.0.0", "1")]
    [InlineData("lan:10.0.0.0/8", "12|10.0.0.1 & calc")]
    [InlineData("lan:10.0.0.0", "12|10.0.0.1")]
    [InlineData("dns:not-a-guid", "")]
    [InlineData("dns:{77C2B6E0-8BD4-4CEA-BF80-BA69FD72155B}", "8.8.8.8 validate=no")]
    [InlineData("doh:1.1.1.1", "1|1|1|https://x y")]
    [InlineData("kill", "yes")]
    [InlineData("whatever", "1")]
    public void RejectsAnythingElse(string key, string value) => Assert.False(Recovery.Valid(key, value));

    [Fact]
    public void ParseDropsInvalidLines()
    {
        var d = Recovery.Parse(new[] { "pin:162.159.198.2=1", "pin:junk=1", "garbage", "kill=1" });
        Assert.Equal(new[] { "kill", "pin:162.159.198.2" }, d.Keys.OrderBy(k => k));
    }

    [Fact]
    public void RemembersAndForgets()
    {
        Recovery.Set("kill", "1");
        Recovery.Set("pin:162.159.198.2", "12|10.30.0.1");
        Assert.Equal("1", Recovery.Get("kill"));
        Recovery.Forget("kill");
        Assert.Null(Recovery.Get("kill"));
        Assert.True(Recovery.Pending);
    }

    [Fact]
    public void UndoneEntriesGoAndFailedDnsRestoresStayForNextLaunch()
    {
        // A route that doesn't exist (nothing to delete) and DNS for an adapter Windows has never heard of.
        File.WriteAllLines(Recovery.FilePath, new[]
        {
            "lan:203.0.113.0/24=99999|192.0.2.1",
            "dns:{00000000-1111-2222-3333-444444444444}=192.0.2.53",
        });
        Recovery.Restore();
        var left = File.ReadAllLines(Recovery.FilePath);
        Assert.Equal(new[] { "dns:{00000000-1111-2222-3333-444444444444}=192.0.2.53" }, left);
    }
}

public class SettingsTests
{
    [Fact]
    public void RoundTripsEverything()
    {
        string path = Path.Combine(Path.GetTempPath(), "warpswitch-settings-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            var s = Settings.Load(path);
            Assert.True(s.Notifications);
            Assert.True(s.CheckUpdates);
            Assert.Null(s.AnimationsOverride);
            s.ConnectOnLaunch = true;
            s.AnimationsOverride = false;
            s.AutoConnectUntrusted = true;
            s.KillSwitch = true;
            s.EncryptDns = true;
            s.Notifications = false;
            s.Mtu = 1350;
            s.TrustedNetworks = new List<string> { "HOME 1_5GHz", "Wi=Fi=Name" };
            s.Bypass = new List<string> { "10.1.0.0/16", "printer.example.com" };
            s.SkippedVersion = "2.2.0";
            s.Save();

            var b = Settings.Load(path);
            Assert.True(b.ConnectOnLaunch);
            Assert.False(b.AnimationsOverride);
            Assert.True(b.AutoConnectUntrusted && b.KillSwitch && b.EncryptDns);
            Assert.False(b.Notifications);
            Assert.Equal(1350, b.Mtu);
            Assert.Equal(new[] { "HOME 1_5GHz", "Wi=Fi=Name" }, b.TrustedNetworks);
            Assert.Equal(new[] { "10.1.0.0/16", "printer.example.com" }, b.Bypass);
            Assert.Equal("2.2.0", b.SkippedVersion);
            Assert.True(b.IsTrusted("home 1_5ghz"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void IgnoresOutOfRangeMtu()
    {
        string path = Path.Combine(Path.GetTempPath(), "warpswitch-settings-" + Guid.NewGuid().ToString("N") + ".ini");
        try
        {
            File.WriteAllText(path, "mtu=999\n");
            Assert.Equal(0, Settings.Load(path).Mtu);
        }
        finally { File.Delete(path); }
    }
}

public class LogBufferTests
{
    [Fact]
    public void CollapsesRepeatedToolLines()
    {
        var log = new LogBuffer(100);
        for (int i = 0; i < 376; i++) log.AddCollapsed("Error writing to IP connection: closed pipe");
        log.Add("· Reconnecting");
        var lines = log.Since(0, out _, out _);
        Assert.Equal(3, lines.Length);
        Assert.EndsWith("repeated 375 more times", lines[1]);
    }

    [Fact]
    public void SinceReturnsOnlyNewLinesAndResetsWhenBehind()
    {
        var log = new LogBuffer(5);
        log.Add("a"); log.Add("b");
        var first = log.Since(0, out long pos, out bool reset);
        Assert.Equal(2, first.Length);
        Assert.False(reset);
        log.Add("c");
        Assert.Single(log.Since(pos, out pos, out _));
        for (int i = 0; i < 10; i++) log.Add("x" + i);
        var all = log.Since(pos, out _, out reset);
        Assert.True(reset);
        Assert.Equal(5, all.Length);
    }

    [Fact]
    public void LastToolLineSkipsAppLinesAndNotes()
    {
        var log = new LogBuffer(10);
        log.AddCollapsed("Failed to create TUN device: wintun");
        log.AddCollapsed("Failed to create TUN device: wintun");
        log.Add("· Off");
        Assert.Equal("Failed to create TUN device: wintun", log.LastToolLine());
    }

    [Fact]
    public void RotatesTheFileWhileRunning()
    {
        string dir = Path.Combine(Path.GetTempPath(), "warpswitch-log-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "app.log");
        try
        {
            using (var log = new LogBuffer(10, file))
                for (int i = 0; i < 4000; i++) log.Add(i + " " + new string('x', 100));
            Assert.True(File.Exists(file + ".1"));
            Assert.True(new FileInfo(file).Length <= LogBuffer.MaxFileBytes + 200);
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}

public class RangesTests
{
    static uint V(string ip) { var b = IPAddress.Parse(ip).GetAddressBytes(); return (uint)b[0] << 24 | (uint)b[1] << 16 | (uint)b[2] << 8 | b[3]; }

    static bool Blocked(List<string> ranges, string ip) => ranges.Any(r =>
    {
        var f = r.Split('-');
        return V(f[0]) <= V(ip) && V(ip) <= V(f[1]);
    });

    [Fact]
    public void KillSwitchBlocksTheInternetButNotLanOrWarp()
    {
        var block = Ranges.Complement4(KillSwitch.AlwaysAllowed4.Concat(new[] { "203.0.113.7/32" }));
        foreach (var ip in new[] { "8.8.8.8", "1.1.1.1", "142.250.1.1", "0.0.0.1", "255.255.255.254", "162.159.200.1" })
            Assert.True(Blocked(block, ip), ip + " should be blocked");
        foreach (var ip in new[] { "10.30.0.1", "192.168.1.1", "172.20.0.5", "169.254.1.1", "224.0.0.251", "255.255.255.255", "162.159.198.2", "162.159.199.18", "162.159.192.1", "203.0.113.7" })
            Assert.False(Blocked(block, ip), ip + " should be allowed");
    }

    [Fact]
    public void ComplementOfNothingIsEverything()
    {
        Assert.Equal(new[] { "0.0.0.0-255.255.255.255" }, Ranges.Complement4(new string[0]));
        Assert.Equal(new[] { "0.0.0.0-9.255.255.255", "11.0.0.0-255.255.255.255" }, Ranges.Complement4(new[] { "10.0.0.0/8" }));
    }

    [Fact]
    public void Ipv6LeavesLinkLocalAndLanAlone()
    {
        var block = Ranges.Complement6(KillSwitch.AlwaysAllowed6);
        Assert.Contains("::-::", block);
        Assert.Contains(block, r => r.StartsWith("::2-"));
        Assert.DoesNotContain(block, r => r.StartsWith("fe80"));
        Assert.Contains(block, r => r.StartsWith("fe00::-"));
    }
}

public class UpdaterTests
{
    [Theory]
    [InlineData("v2.1.0", "2.1.0")]
    [InlineData("2.3", "2.3.0")]
    [InlineData("v10.0.12", "10.0.12")]
    public void ParsesReleaseTags(string tag, string version) => Assert.Equal(Version.Parse(version), Updater.ParseTag(tag));

    [Theory]
    [InlineData("v2.2.0-beta")]
    [InlineData("latest")]
    [InlineData(null)]
    public void IgnoresOtherTags(string tag) => Assert.Null(Updater.ParseTag(tag));

    [Fact]
    public void FindsTheInstallerInSha256Sums()
    {
        string sums = "aaaabbbbccccddddeeeeffff0000111122223333444455556666777788889999  WarpSwitch-2.2.0-portable.exe\r\n"
                    + "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef *WarpSwitch-Setup-2.2.0.exe\r\n";
        var found = Updater.FindInstaller(sums);
        Assert.NotNull(found);
        Assert.Equal("WarpSwitch-Setup-2.2.0.exe", found.Value.Name);
        Assert.Equal("0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF0123456789ABCDEF", found.Value.Sha);
        Assert.Null(Updater.FindInstaller("deadbeef  WarpSwitch-Setup-2.2.0.exe"));
        Assert.Null(Updater.FindInstaller("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef  ..\\evil.exe"));
    }

    [Fact]
    public void KnowsItsOwnVersion() => Assert.True(Updater.Current >= new Version(2, 1, 0));
}

public class MessageTests
{
    [Theory]
    [InlineData("Error writing to IP connection: connect-ip: failed to send datagram capsule: io: read/write on closed pipe", "The network cut the connection. Retrying…")]
    [InlineData("dial tcp 1.2.3.4:443: connectex: A connection attempt failed because the connected party did not properly respond", "WARP isn't answering. Retrying…")]
    [InlineData("x509: certificate signed by unknown authority", "Something on this network is intercepting the connection. Trying another route…")]
    public void TranslatesUsqueErrors(string raw, string friendly) => Assert.Equal(friendly, Tunnel.Friendly(raw));

    [Fact]
    public void FormatsDataSizes()
    {
        Assert.Equal("1 KB", MainForm.Bytes(300));
        Assert.Equal("5.0 MB", MainForm.Bytes(5L * 1024 * 1024));
        Assert.Equal("1.50 GB", MainForm.Bytes(3L * 512 * 1024 * 1024));
    }
}

public class SetupTests
{
    [Fact]
    public void OnlyProgramFilesCountsAsInstalled()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Assert.True(Setup.IsProtectedPath(Path.Combine(pf, "WARP Switch", "WarpSwitch.exe")));
        Assert.False(Setup.IsProtectedPath(pf + " Evil\\WarpSwitch.exe"));
        Assert.False(Setup.IsProtectedPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "WARP Switch.exe")));
        Assert.False(Setup.IsProtectedPath(@"C:\Program Files\..\Users\x\WarpSwitch.exe"));
    }

    [Fact]
    public void WritesFilesAtomically()
    {
        string path = Path.Combine(Path.GetTempPath(), "warpswitch-atomic-" + Guid.NewGuid().ToString("N"));
        try
        {
            Setup.WriteAllLinesAtomic(path, new[] { "one" });
            Setup.WriteAllLinesAtomic(path, new[] { "two", "three" });
            Assert.Equal(new[] { "two", "three" }, File.ReadAllLines(path));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void EmbedsTheSignedWintun()
    {
        using var src = typeof(Setup).Assembly.GetManifestResourceStream("wintun.dll.gz");
        Assert.NotNull(src);
        using var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress);
        Assert.Equal("E5DA8447DC2C320EDC0FC52FA01885C103DE8C118481F683643CACC3220DAFCE", Setup.Hash(gz));
    }

    [Fact]
    public void EmbedsThePinnedUsque()
    {
        using var src = typeof(Setup).Assembly.GetManifestResourceStream("usque.exe.gz");
        Assert.NotNull(src);
        using var gz = new System.IO.Compression.GZipStream(src, System.IO.Compression.CompressionMode.Decompress);
        Assert.Equal("122F2E40208E81BB814FCB4BE4FBBBDC9246860F1F3784F72AE4512B97667E91", Setup.Hash(gz));   // usque 4.2.1 windows/amd64
    }

    [Fact]
    public void JoinCodeEscapesSpecialCharacters()
    {
        Assert.Equal(@"WIFI:T:WPA;S:a\;b\,c;P:p\:w\\x\"";;", Hotspot.JoinCode("a;b,c", "p:w\\x\""));
    }
}

public class QrCodeTests
{
    [Theory]
    [InlineData(14, 1)]   // version 1 at medium holds 14 bytes
    [InlineData(15, 2)]
    [InlineData(106, 6)]
    public void PicksTheSmallestVersion(int bytes, int version)
    {
        var q = QrCode.Encode(new byte[bytes].Select(_ => (byte)'A').ToArray(), QrCode.Ecc.Medium);
        Assert.Equal(version, q.Version);
        Assert.Equal(version * 4 + 17, q.Size);
    }

    [Fact]
    public void HasFinderPatternsAndTheDarkModule()
    {
        var q = QrCode.Encode(Hotspot.JoinCode("VORTEX-PC 4821", "abcdefgh23"));
        foreach (var (cx, cy) in new[] { (3, 3), (q.Size - 4, 3), (3, q.Size - 4) })
        {
            Assert.True(q[cx, cy]);                // centre
            Assert.False(q[cx + 2, cy]);           // light ring
            Assert.True(q[cx + 3, cy]);            // dark ring
        }
        Assert.True(q[8, q.Size - 8]);
    }

    [Fact]
    public void FormatInfoCopiesAgree()
    {
        var q = QrCode.Encode("hello", QrCode.Ecc.Low);
        int a = 0, b = 0;
        for (int i = 0; i <= 5; i++) a |= (q[8, i] ? 1 : 0) << i;
        for (int i = 0; i < 6; i++) b |= (q[q.Size - 1 - i, 8] ? 1 : 0) << i;
        Assert.Equal(a, b);
    }
}
