using System;
using System.IO;
using Xunit;

namespace WarpSwitch.Tests;

/// Talks to the real GitHub releases, so it only runs when asked: set WARPSWITCH_LIVE_UPDATE_TEST=1.
/// Pretends to be version 2.0.0, finds the latest release, downloads its installer and checks it against
/// SHA256SUMS.txt — everything the in-app updater does except running the installer.
public class LiveUpdateTests
{
    [Fact]
    public void FindsDownloadsAndVerifiesTheLatestInstaller()
    {
        if (Environment.GetEnvironmentVariable("WARPSWITCH_LIVE_UPDATE_TEST") != "1") return;
        var u = new Updater();
        Assert.True(u.CheckAsync(new Version(2, 0, 0)).GetAwaiter().GetResult(), "no newer release with an installer found");
        Assert.True(u.Available > new Version(2, 0, 0));
        string dir = Path.Combine(Path.GetTempPath(), "warpswitch-live-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var held = u.DownloadVerifiedAsync(dir).GetAwaiter().GetResult();
            Assert.True(held.Length > 1_000_000);
            Assert.StartsWith("WarpSwitch-Setup-", Path.GetFileName(held.Name));
            Assert.Throws<IOException>(() => File.WriteAllText(held.Name, "tampered"));   // locked against changes
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }
}
