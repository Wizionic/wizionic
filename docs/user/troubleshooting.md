---
id: troubleshooting
title: Troubleshooting
---

# Troubleshooting

## Local models do not appear

- Confirm Ollama or Lemonade is running.
- Click **Refresh models** on that page.
- From the **website**, set `OLLAMA_ORIGINS` / `LEMONADE_ALLOWED_ORIGINS` to `https://wizionic.com` and avoid mixed-content blocks. Prefer the desktop app for localhost AI.

## Sync does not see my other device

- Same account on both.
- Both online; open the Sync page on each.
- Desktop login server must match (wizionic.com vs the same Home Server). Other devices cannot use `localhost` — copy the network URL from the hosting PC’s Settings → Home Server.

## Other devices cannot open the Home Server

- On the hosting PC, Settings → Home Server should list an **On your network** URL (`pc-name.local:5150`) and an IP URL. Copy one of those — not `localhost`.
- Port is **5150** (not 5050). Prefer the **IP** on Android; `.local` often does not resolve there.
- The Home Server must be **running**. After install, Wizionic signs you out and restarts so you are not still on wizionic.com.

## Two-factor checkbox fails with a 400

The desktop app may be pointed at wizionic.com while you are testing a local build. Set **Settings → Login server** to `http://localhost:5136` (or your Home Server) or deploy the host that has 2FA.

## Notes will not unlock

Use the **account password**, not the email login code and not SMS.

## Magic link does nothing

If 2FA is on, enter your password first. Then use the email code or the link to finish.

## I closed the app but it is still in the tray

On Windows and Linux desktop, the close button **hides** Wizionic so sync and scheduled workflows keep running. Right-click the tray icon and choose **Quit**, or use **Settings → Desktop → Quit Wizionic**. Turn off **Close window to system tray** if you want X to exit.

On Linux, KDE Plasma and Linux Mint Cinnamon show the tray icon. Stock GNOME often has no StatusNotifier watcher: close then **really quits** (or install the AppIndicator / KStatusNotifierItem extension).

## Updates (desktop)

Official builds check [GitHub Releases](https://github.com/Wizionic/wizionic/releases/latest), not wizionic.com, for new installers.

First-time Windows install: prefer `irm https://wizionic.com/install.ps1 | iex` in PowerShell. Browser downloads of the unsigned `Setup.exe` get Mark of the Web; Edge then hides a clear Run path behind Keep / SmartScreen.
