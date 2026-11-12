using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Networking.Connectivity;
using Windows.Networking.NetworkOperators;

namespace WarpSwitch;

static class WinRt
{
    // Minimal awaiters for WinRT async calls (avoids pulling in the full Windows SDK projection).
    public static Task<T> AsTask<T>(this IAsyncOperation<T> op)
    {
        var tcs = new TaskCompletionSource<T>();
        op.Completed = (o, status) =>
        {
            if (status == AsyncStatus.Completed) tcs.TrySetResult(o.GetResults());
            else if (status == AsyncStatus.Error) tcs.TrySetException(o.ErrorCode);
            else tcs.TrySetCanceled();
        };
        return tcs.Task;
    }

    public static Task AsTask(this IAsyncAction op)
    {
        var tcs = new TaskCompletionSource<bool>();
        op.Completed = (o, status) =>
        {
            if (status == AsyncStatus.Completed) tcs.TrySetResult(true);
            else if (status == AsyncStatus.Error) tcs.TrySetException(o.ErrorCode);
            else tcs.TrySetCanceled();
        };
        return tcs.Task;
    }
}

/// Windows Mobile Hotspot, driven through the official tethering API.
/// Starting it *from the WARP adapter's connection profile* makes Windows share the tunnel itself,
/// so devices on the hotspot go through WARP too. No poking at ICS behind Windows' back, and no
/// registry edits (an earlier script wiped the hotspot's saved settings that way).
sealed class Hotspot
{
    const int ErrorShownSeconds = 20;

    readonly object gate = new();
    public bool Available { get; private set; }
    public bool On { get; private set; }
    public bool Switching { get; private set; }   // Windows reports the hotspot mid-transition
    public bool Busy { get; private set; }        // our own on/off request is in flight
    public bool ViaWarp { get; private set; }
    public int Clients { get; private set; }
    public string Ssid { get; private set; }
    public string Passphrase { get; private set; }
    public string Error { get; private set; }
    /// Why the hotspot can't be used right now (only meaningful while !Available).
    public string Problem { get; private set; }
    public Action<string> Log = _ => { };
    public event Action Changed;
    DateTime errorAt;

    static NetworkOperatorTetheringManager Manager(ConnectionProfile p) =>
        p == null ? null : NetworkOperatorTetheringManager.CreateFromConnectionProfile(p);

    static ConnectionProfile ProfileOf(string adapterId)
    {
        if (adapterId == null || !Guid.TryParse(adapterId, out var g)) return null;
        return NetworkInformation.GetConnectionProfiles().FirstOrDefault(p => p.NetworkAdapter != null && p.NetworkAdapter.NetworkAdapterId == g);
    }

    /// Poll of the real state (runs off the UI thread). <paramref name="tunId"/> is the WARP adapter id while connected.
    public void Refresh(string tunId)
    {
        lock (gate)
        {
            bool wasOn = On;
            try
            {
                var m = Manager(NetworkInformation.GetInternetConnectionProfile()) ?? Manager(ProfileOf(tunId));
                Available = m != null;
                var state = m?.TetheringOperationalState ?? TetheringOperationalState.Unknown;
                On = state == TetheringOperationalState.On;
                Switching = state == TetheringOperationalState.InTransition;
                Clients = On ? (int)m.ClientCount : 0;
                ViaWarp = On && tunId != null && Net.Forwarding(tunId);
                // Read every time: the name or password may have been changed in Windows Settings meanwhile.
                if (m != null) ReadConfig(m);
                Problem = null;
            }
            catch (Exception ex)
            {
                Available = false;
                On = false;
                Problem = Describe(ex);
            }
            // An error is about the last attempt; once things move on (or after a while) it no longer applies.
            if (Error != null && (On != wasOn || (DateTime.UtcNow - errorAt).TotalSeconds > ErrorShownSeconds)) Error = null;
        }
        Changed?.Invoke();
    }

    void ReadConfig(NetworkOperatorTetheringManager m)
    {
        try
        {
            var c = m.GetCurrentAccessPointConfiguration();
            Ssid = string.IsNullOrEmpty(c.Ssid) ? null : c.Ssid;
            Passphrase = c.Passphrase;
        }
        catch (COMException) { Ssid = null; Passphrase = null; } // never set up (or settings lost)
    }

    /// Turn the hotspot on/off. When on and <paramref name="tunId"/> is given, it shares WARP;
    /// otherwise the normal internet connection. Turning on while already on re-points it.
    public async Task SetAsync(bool on, string tunId)
    {
        lock (gate)
        {
            if (Busy) return;
            Busy = true;
            Error = null;
        }
        Changed?.Invoke();
        try
        {
            ConnectionProfile source = null;
            if (on && tunId != null)
            {
                // Windows needs a moment to create the profile for a freshly connected tunnel.
                for (int i = 0; i < 20 && (source = ProfileOf(tunId)) == null; i++) await Task.Delay(500);
            }
            source ??= NetworkInformation.GetInternetConnectionProfile();
            var m = Manager(source) ?? throw new UserError("Connect to the internet first.");

            if (m.TetheringOperationalState == TetheringOperationalState.On)
            {
                Check(await m.StopTetheringAsync().AsTask());
                if (on) await Task.Delay(1500);
            }
            if (on)
            {
                await EnsureConfigAsync(m);
                Check(await m.StartTetheringAsync().AsTask());
                bool shared = tunId != null && source.NetworkAdapter != null && source.NetworkAdapter.NetworkAdapterId.ToString("B").Equals(tunId, StringComparison.OrdinalIgnoreCase);
                Log("· hotspot on, sharing " + (shared ? "WARP" : "the normal connection") + " (" + source.ProfileName + ")");
            }
            else Log("· hotspot off");
        }
        catch (Exception ex)
        {
            lock (gate)
            {
                Error = Describe(ex);
                errorAt = DateTime.UtcNow;
            }
            Log("· hotspot error: " + Error);
        }
        finally
        {
            lock (gate) Busy = false;
            Refresh(tunId);
        }
    }

    async Task EnsureConfigAsync(NetworkOperatorTetheringManager m)
    {
        NetworkOperatorTetheringAccessPointConfiguration c = null;
        try { c = m.GetCurrentAccessPointConfiguration(); } catch (COMException) { }
        if (c != null && !string.IsNullOrEmpty(c.Ssid) && (c.Passphrase ?? "").Length >= 8)
        {
            Ssid = c.Ssid;
            Passphrase = c.Passphrase;
            return;
        }
        // No saved name/password (fresh PC, or wiped by an older version of these tools): make one.
        c ??= new NetworkOperatorTetheringAccessPointConfiguration();
        c.Ssid = Environment.MachineName + " " + RandomString("0123456789", 4);
        c.Passphrase = RandomString("abcdefghjkmnpqrstuvwxyz23456789", 10);
        await m.ConfigureAccessPointAsync(c).AsTask();
        Ssid = c.Ssid;
        Passphrase = c.Passphrase;
        Log("· created hotspot settings: " + Ssid);
    }

    static string RandomString(string alphabet, int length)
    {
        var bytes = new byte[length];
        using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
        return new string(bytes.Select(b => alphabet[b % alphabet.Length]).ToArray());
    }

    static void Check(NetworkOperatorTetheringOperationResult r)
    {
        if (r.Status == TetheringOperationStatus.Success) return;
        string why = r.Status switch
        {
            TetheringOperationStatus.WiFiDeviceOff => "Wi-Fi is turned off.",
            TetheringOperationStatus.OperationInProgress => "Windows is still switching the hotspot. Try again in a moment.",
            TetheringOperationStatus.NetworkLimitedConnectivity => "The connection being shared has no internet.",
            TetheringOperationStatus.EntitlementCheckFailure or TetheringOperationStatus.EntitlementCheckTimeout => "Your network provider doesn't allow hotspot sharing.",
            _ => "Windows couldn't switch the hotspot (" + r.Status + ").",
        };
        if (!string.IsNullOrEmpty(r.AdditionalErrorMessage)) why += " " + r.AdditionalErrorMessage;
        throw new UserError(why);
    }

    static string Describe(Exception ex) => ex switch
    {
        UserError u => u.Message,
        COMException c => "Hotspot unavailable (0x" + c.HResult.ToString("X8") + ").",
        _ => ex.GetBaseException().Message,
    };

    /// Made-up state for screenshots and UI tests.
    internal void Preview(bool on, bool viaWarp, int clients, string ssid, string passphrase)
    {
        Available = true;
        On = on;
        ViaWarp = viaWarp;
        Clients = clients;
        Ssid = ssid;
        Passphrase = passphrase;
    }

    /// The standard "WIFI:" payload phone cameras understand, for the join-by-QR-code window.
    public static string JoinCode(string ssid, string passphrase)
    {
        static string Esc(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", @"([\\;,:""])", @"\$1");
        return "WIFI:T:WPA;S:" + Esc(ssid) + ";P:" + Esc(passphrase) + ";;";
    }
}
