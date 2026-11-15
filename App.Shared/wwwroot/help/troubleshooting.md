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

## Notebook shows a long id instead of its name

The note body is unchanged. Rename it (⋮ → Rename). Sync had been able to replace the sidebar name with the notebook id.

## I forgot my password

On the Password tab, **Forgot password?** Enter the emailed login code. That removes the password (and turns two-factor off) so you can **Add a password**. Do not use the normal **Login code (email)** tab if two-factor is on — that path still requires the password first.

## Magic link does nothing

If 2FA is on, enter your password first. Then use the email code or the link to finish. If you forgot the password, use **Forgot password?** instead.

## Agents: Could not publish this device

You can see your other devices, but the login server does not have the Agents API. Update and restart your Home Server from this build, then check **This device is the orchestrator** again. On wizionic.com, Agents works after that site is updated. Other installs also need this build before their skills appear.

## Another device is the orchestrator

Only one device can hold that role. Turn it off on the device that has it, or wait until that device is offline, then try again.

## No skills published

That install is an older build, or it has no enabled skills. Syncing skills does not publish them until that app is this version and online.

## I closed the app but it is still in the tray

On Windows and Linux desktop, the close button **hides** Wizionic so sync and scheduled workflows keep running. Right-click the tray icon and choose **Quit**, or use **Settings → Desktop → Quit Wizionic**. Turn off **Close window to system tray** if you want X to exit.

On Linux, KDE Plasma and Linux Mint Cinnamon show the tray icon. Stock GNOME often has no StatusNotifier watcher: close then **really quits** (or install the AppIndicator / KStatusNotifierItem extension).

## Updates (desktop)

Official builds check [GitHub Releases](https://github.com/Wizionic/wizionic/releases/latest), not wizionic.com, for new installers.

First-time Windows install: [Microsoft Store](https://apps.microsoft.com/detail/9MX9NNG2FN2B). Store installs update in the Store.
