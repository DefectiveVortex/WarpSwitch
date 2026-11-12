using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Numerics;
using System.Runtime.InteropServices;

namespace WarpSwitch;

/// Optional kill switch: Windows Firewall rules that block everything leaving the physical adapter except WARP
/// itself, the local network and the bypass list, so nothing leaks while the tunnel is down (route switches,
/// drops, network changes). Traffic inside the tunnel uses the tunnel's own adapter and isn't affected.
/// The rules survive a crash on purpose (that's what a kill switch is for); they're removed on disconnect,
/// on quit, by the uninstaller, and at the next launch.
static class KillSwitch
{
    const string Group = "WARP Switch";
    const string Name4 = "WARP Switch kill switch (IPv4)", Name6 = "WARP Switch kill switch (IPv6)";
    static readonly object gate = new();
    static string applied;   // interface + ranges currently in force, so re-applying the same thing is free

    /// Never blocked: private/link-local/CGNAT ranges (the LAN, sign-in pages, the router), multicast and
    /// broadcast (DHCP), and Cloudflare's WARP server range (162.159.192.0/21 covers every address Pathfinder tries).
    internal static readonly string[] AlwaysAllowed4 =
    {
        "10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "100.64.0.0/10", "127.0.0.0/8",
        "224.0.0.0/4", "255.255.255.255/32", "162.159.192.0/21",
    };
    internal static readonly string[] AlwaysAllowed6 = { "::1/128", "fe80::/10", "fc00::/7", "ff00::/8" };

    public static bool Active { get { lock (gate) return applied != null; } }

    /// Blocks <paramref name="interfaceAlias"/> (the physical adapter, e.g. "Wi-Fi") except for the always-allowed
    /// ranges plus <paramref name="alsoAllowed"/> (IPv4 CIDRs: the WARP endpoint, bypass routes). Returns a warning
    /// for the user, or null.
    public static string Apply(string interfaceAlias, IEnumerable<string> alsoAllowed)
    {
        string block4 = string.Join(",", Ranges.Complement4(AlwaysAllowed4.Concat(alsoAllowed ?? Enumerable.Empty<string>())));
        string block6 = string.Join(",", Ranges.Complement6(AlwaysAllowed6));
        string key = interfaceAlias + "|" + block4;
        lock (gate)
        {
            if (key == applied) return null;
            Recovery.Set("kill", "1");
            dynamic policy = Policy();
            RemoveRules(policy);
            AddRule(policy, Name4, interfaceAlias, block4);
            AddRule(policy, Name6, interfaceAlias, block6);
            applied = key;
            return FirewallOff(policy) ? "Windows Firewall is turned off, so the kill switch can't block anything." : null;
        }
    }

    public static void Remove()
    {
        lock (gate)
        {
            applied = null;
            try { RemoveRules(Policy()); } catch (Exception ex) { Net.Log("· couldn't remove kill switch rules: " + ex.Message); }
        }
        try { Recovery.Forget("kill"); } catch { }
    }

    static dynamic Policy() => Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FwPolicy2"));

    static void AddRule(dynamic policy, string name, string alias, string remoteAddresses)
    {
        dynamic rule = Activator.CreateInstance(Type.GetTypeFromProgID("HNetCfg.FWRule"));
        rule.Name = name;
        rule.Description = "Blocks traffic that would go around WARP while WARP Switch's kill switch is on. Removed when you disconnect.";
        rule.Grouping = Group;
        rule.Protocol = 256;                    // any
        rule.Direction = 2;                     // NET_FW_RULE_DIR_OUT
        rule.Action = 0;                        // NET_FW_ACTION_BLOCK (block beats allow in Windows Firewall)
        rule.RemoteAddresses = remoteAddresses;
        rule.Interfaces = new object[] { alias };
        rule.Profiles = 0x7FFFFFFF;             // domain, private and public
        rule.Enabled = true;
        policy.Rules.Add(rule);
    }

    static void RemoveRules(dynamic policy)
    {
        foreach (var name in new[] { Name4, Name6 })
            for (int i = 0; i < 20; i++)   // duplicates are possible after a crash mid-update
            {
                try { _ = policy.Rules.Item(name); }
                catch (COMException) { break; }
                catch (System.IO.FileNotFoundException) { break; }
                policy.Rules.Remove(name);
            }
    }

    static bool FirewallOff(dynamic policy)
    {
        try
        {
            int current = policy.CurrentProfileTypes;
            foreach (int profile in new[] { 1, 2, 4 })
                if ((current & profile) != 0 && !(bool)policy.FirewallEnabled[profile]) return true;
        }
        catch { }
        return false;
    }
}

/// Address-range arithmetic for firewall rules, which take ranges ("a-b") rather than "everything except".
static class Ranges
{
    static (BigInteger Lo, BigInteger Hi) Span(string cidr, int bits)
    {
        var (ip, len) = Net.ParseCidr(cidr);
        var bytes = ip.GetAddressBytes();
        var v = new BigInteger(bytes.Reverse().Concat(new byte[] { 0 }).ToArray());   // big-endian → unsigned
        var size = BigInteger.One << (bits - len);
        var lo = v / size * size;
        return (lo, lo + size - 1);
    }

    static string Text(BigInteger v, int bits)
    {
        var raw = v.ToByteArray();   // little-endian, maybe with a sign byte
        var b = new byte[bits / 8];
        for (int i = 0; i < b.Length && i < raw.Length; i++) b[b.Length - 1 - i] = raw[i];
        return new IPAddress(b).ToString();
    }

    /// The parts of the address space not covered by <paramref name="allowed"/>, as "first-last" ranges.
    static List<string> Complement(IEnumerable<string> allowed, int bits)
    {
        var spans = allowed.Select(c => Span(c, bits)).OrderBy(s => s.Lo).ToList();
        var max = (BigInteger.One << bits) - 1;
        var result = new List<string>();
        BigInteger next = 0;
        foreach (var (lo, hi) in spans)
        {
            if (lo > next) result.Add(Text(next, bits) + "-" + Text(lo - 1, bits));
            if (hi + 1 > next) next = hi + 1;
        }
        if (next <= max) result.Add(Text(next, bits) + "-" + Text(max, bits));
        return result;
    }

    public static List<string> Complement4(IEnumerable<string> allowed) => Complement(allowed, 32);
    public static List<string> Complement6(IEnumerable<string> allowed) => Complement(allowed, 128);
}
