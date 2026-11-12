using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace WarpSwitch;

/// "Encrypt DNS outside the tunnel": turns on Windows' own DNS-over-HTTPS for Cloudflare's resolvers (their
/// "auto-upgrade" flag), so the moments the tunnel is down (route switches, drops) don't send plain-text lookups
/// across the network. Windows 11's DNS client does the work; the original flags are recorded and put back.
static class SecureDns
{
    static readonly string[] Servers = { "1.1.1.1", "1.0.0.1", "2606:4700:4700::1111", "2606:4700:4700::1001" };
    const string Template = "https://cloudflare-dns.com/dns-query";

    /// Windows 11 (build 22000+) has the DoH client and its PowerShell cmdlets.
    public static bool Supported
    {
        get
        {
            try
            {
                using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
                return int.TryParse(k?.GetValue("CurrentBuildNumber") as string, out int build) && build >= 22000;
            }
            catch { return false; }
        }
    }

    static string PowerShell => Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");

    static (int Code, string Output) Run(string script) =>
        Shell.Capture(PowerShell, "-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"" + script.Replace("\"", "\\\"") + "\"", null, 30000);

    /// Recorded per server as "autoUpgrade|udpFallback|existed|template" (1/0 flags).
    internal static Dictionary<string, string> ParseOriginals(string output)
    {
        var d = new Dictionary<string, string>();
        foreach (var line in (output ?? "").Split('\n').Select(l => l.Trim()))
        {
            var f = line.Split('|');
            if (f.Length == 5 && Servers.Contains(f[0])) d[f[0]] = string.Join("|", f.Skip(1));
        }
        return d;
    }

    public static void Enable()
    {
        if (!Supported) return;
        string list = string.Join(",", Servers.Select(s => "'" + s + "'"));
        var (code, output) = Run(
            "$ErrorActionPreference='Stop'; foreach ($s in @(" + list + ")) { " +
            "$c = Get-DnsClientDohServerAddress -ServerAddress $s -ErrorAction SilentlyContinue; " +
            "if ($c) { '{0}|{1}|{2}|1|{3}' -f $s,[int]$c.AutoUpgrade,[int]$c.AllowFallbackToUdp,$c.DohTemplate; " +
            "Set-DnsClientDohServerAddress -ServerAddress $s -AutoUpgrade $true -AllowFallbackToUdp $false | Out-Null } " +
            "else { '{0}|0|0|0|' -f $s; Add-DnsClientDohServerAddress -ServerAddress $s -DohTemplate '" + Template + "' -AutoUpgrade $true -AllowFallbackToUdp $false | Out-Null } }");
        foreach (var kv in ParseOriginals(output)) Recovery.SetIfMissing("doh:" + kv.Key, kv.Value);
        if (code != 0) Net.Log("· encrypted DNS: " + output.Trim());
        Native.DnsFlushResolverCache();
    }

    public static void Restore(string server, string recorded)
    {
        var f = recorded.Split('|');
        string script = f[2] == "0"
            ? $"Remove-DnsClientDohServerAddress -ServerAddress '{server}' -ErrorAction SilentlyContinue"
            : $"Set-DnsClientDohServerAddress -ServerAddress '{server}' -AutoUpgrade ${(f[0] == "1" ? "true" : "false")} -AllowFallbackToUdp ${(f[1] == "1" ? "true" : "false")}";
        var (code, output) = Run(script);
        if (code != 0) throw new IOException("couldn't restore encrypted-DNS settings for " + server + ": " + output.Trim());
    }
}
