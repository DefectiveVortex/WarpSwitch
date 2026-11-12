# Changelog

## 2.1.0

The first public release, with an installer and automatic updates.

### Fixed
- **Networks were mixed up.** Windows keeps a profile for every Wi-Fi you've ever joined, and WARP Switch read the
  first one instead of the connected one, so different networks got the same name. Moving from one to another
  was taken for an address change (no new route search, local-network routes left pointing at the old router),
  and one remembered route was used everywhere, which could keep you on slower TCP where QUIC works.
- **Waking from sleep gave up straight away.** Time asleep counted as time spent failing to reconnect.
  Sleep and wake are now handled, including when Windows doesn't announce them.
- **A slow start on your PC was blamed on the network.** The route timer now starts when WARP's helper begins
  connecting. If the helper itself can't start, you get the real reason instead of "the network blocked all
  8 routes".
- **The log flooded** with hundreds of identical lines a second when a connection died; repeats are now counted,
  and the log file stays capped while the app runs.
- A network change at the wrong moment could crash the app; a crash now puts DNS back before closing.
- DNS for a Wi-Fi adapter that was unplugged while connected is now restored when it comes back.
- The hotspot card no longer shows an old password after you change it in Windows Settings, and errors clear.
- The tray icon comes back after Explorer restarts.
- The log window no longer jumps to the bottom while you're reading it.
- "Starting…" can be cancelled.
- IPv6 can no longer go around the tunnel on networks that have it, and its DNS goes to Cloudflare too.
- With both Ethernet and Wi-Fi connected, the uplink Windows really uses is the one followed.
- Long status messages no longer spill over the info card.

### Security
- WARP's helper programs moved to an admin-only folder, are checked against their SHA-256 before every start, and
  can't be replaced while they run. Before, any program running as you could swap them and have them run with
  admin rights.
- *Start with Windows* is only offered by the installed copy, for the same reason.
- The crash-recovery record moved to the admin-only folder, and every entry is checked before it's acted on.

### New
- Installer (Program Files, Start menu, clean uninstall) and **automatic updates** from GitHub releases.
- Settings window.
- Kill switch.
- Split tunnel: addresses, ranges and host names that skip WARP.
- Connect automatically on networks you don't trust.
- Encrypted DNS outside the tunnel (Windows 11).
- Join the hotspot by QR code.
- Data used and ping in the info card.
- Notifications when WARP drops for more than a few seconds, comes back, gives up, or an update is out.
- Follows Windows' "Animation effects" setting.
- Forget learned routes; reset the WARP registration.

### Faster
- Routes and DNS are set through Windows' IP Helper API instead of launching netsh and route.exe a dozen times
  per connect.
- The traffic check after connecting no longer waits out failures that only mean Windows hasn't started using
  the new routes yet.
- Hotspot status is read off the UI thread.

## 2.0.0

Single-file app with route scouting and failover, Mobile Hotspot sharing, and the animated vortex.
