using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace WarpSwitch;

/// Checks GitHub for a newer release and installs it. Talks to github.com's own release pages and downloads
/// rather than the REST API, whose 60-requests-an-hour limit is shared by everyone behind one campus NAT.
/// The installer only runs after its SHA-256 matches the release's SHA256SUMS.txt.
sealed class Updater
{
    public const string Repo = "DefectiveVortex/WarpSwitch";
    public static string ReleasesUrl => "https://github.com/" + Repo + "/releases";
    public static readonly Version Current = Normalize(typeof(Updater).Assembly.GetName().Version);

    public Version Available { get; private set; }
    public string Tag { get; private set; }
    public bool Busy { get; private set; }
    public int Progress { get; private set; } = -1;   // percent while downloading
    public DateTime LastCheck { get; private set; }
    public event Action Changed;
    string installerName, installerSha;

    internal void Preview(Version available) => Available = available;

    static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build));

    /// "v2.1.0" → 2.1.0. Pre-release tags ("v2.2.0-beta") aren't offered.
    internal static Version ParseTag(string tag)
    {
        var m = Regex.Match(tag ?? "", @"^v?(\d+)\.(\d+)(?:\.(\d+))?$");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    /// The installer's line in a sha256sum-style file.
    internal static (string Name, string Sha)? FindInstaller(string sums)
    {
        foreach (var line in (sums ?? "").Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^([0-9a-fA-F]{64})\s+\*?(WarpSwitch-Setup-[0-9A-Za-z.\-]+\.exe)$");
            if (m.Success) return (m.Groups[2].Value, m.Groups[1].Value.ToUpperInvariant());
        }
        return null;
    }

    static HttpClient Client(bool followRedirects, int timeoutSeconds = 20)
    {
        var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = followRedirects }) { Timeout = TimeSpan.FromSeconds(timeoutSeconds) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("WARP-Switch/" + Current + " (+https://github.com/" + Repo + ")");
        return http;
    }

    static string DownloadUrl(string tag, string file) =>
        "https://github.com/" + Repo + "/releases/download/" + Uri.EscapeDataString(tag) + "/" + Uri.EscapeDataString(file);

    /// Is a newer release out, with its installer already uploaded? (Right after a release is published its files
    /// are still being built; that counts as "not yet" and the next check picks it up.)
    public async Task<bool> CheckAsync()
    {
        LastCheck = DateTime.Now;
        string tag;
        using (var http = Client(false))
        using (var resp = await http.GetAsync("https://github.com/" + Repo + "/releases/latest").ConfigureAwait(false))
        {
            var m = Regex.Match(resp.Headers.Location?.ToString() ?? "", @"/releases/tag/([^/?#]+)$");
            if (!m.Success) return false;   // no releases yet
            tag = Uri.UnescapeDataString(m.Groups[1].Value);
        }
        var version = ParseTag(tag);
        if (version == null || version <= Current)
        {
            if (Available != null) { Available = null; Changed?.Invoke(); }
            return false;
        }
        string sums;
        using (var http = Client(true))
        {
            try { sums = await http.GetStringAsync(DownloadUrl(tag, "SHA256SUMS.txt")).ConfigureAwait(false); }
            catch (HttpRequestException) { return false; }
        }
        var installer = FindInstaller(sums);
        if (installer == null) return false;
        Tag = tag;
        installerName = installer.Value.Name;
        installerSha = installer.Value.Sha;
        Available = version;
        Changed?.Invoke();
        return true;
    }

    /// Downloads and verifies the installer, then starts it silently. It closes this app (which disconnects and
    /// puts the network back), installs, and starts the new version, reconnecting if <paramref name="reconnect"/>.
    /// The caller should quit once this returns.
    public async Task InstallAsync(bool reconnect)
    {
        if (Available == null || Busy) return;
        Busy = true;
        Progress = 0;
        Changed?.Invoke();
        try
        {
            // Admin-only folder: nothing running as the user can swap the file between the check and the launch.
            SecureFolder.Ensure(Setup.SecureDir);
            string dir = Path.Combine(Setup.SecureDir, "updates");
            Directory.CreateDirectory(dir);
            foreach (var old in Directory.GetFiles(dir)) try { File.Delete(old); } catch { }
            string path = Path.Combine(dir, installerName);

            using (var http = Client(true, 300))
            using (var resp = await http.GetAsync(DownloadUrl(Tag, installerName), HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? -1;
                using var src = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
                using var dst = File.Create(path);
                var buf = new byte[81920];
                long done = 0;
                int n;
                while ((n = await src.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false)) > 0)
                {
                    await dst.WriteAsync(buf, 0, n).ConfigureAwait(false);
                    done += n;
                    int pct = total > 0 ? (int)(done * 100 / total) : -1;
                    if (pct != Progress) { Progress = pct; Changed?.Invoke(); }
                }
            }

            using var held = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (!string.Equals(Setup.Hash(held), installerSha, StringComparison.OrdinalIgnoreCase))
                throw new UserError("The downloaded update didn't match its published checksum, so it wasn't run. Try again later.");
            Process.Start(new ProcessStartInfo(path, "/SILENT /SUPPRESSMSGBOXES /NORESTART /SP- /relaunch=" + (reconnect ? "connect" : "show")) { UseShellExecute = false });
            await Task.Delay(1500).ConfigureAwait(false);   // let it map itself before the file is released
        }
        finally
        {
            Busy = false;
            Progress = -1;
            Changed?.Invoke();
        }
    }
}
