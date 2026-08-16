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
- Desktop login server must match (wizionic.com vs local Home Server).

## Two-factor checkbox fails with a 400

The desktop app may be pointed at wizionic.com while you are testing a local build. Set **Settings → Login server** to `http://localhost:5136` (or your Home Server) or deploy the host that has 2FA.

## Notes will not unlock

Use the **account password**, not the email login code and not SMS.

## Magic link does nothing

If 2FA is on, enter your password first. Then use the email code or the link to finish.

## Updates (desktop)

Official builds check [GitHub Releases](https://github.com/Wizionic/wizionic/releases/latest), not wizionic.com, for new installers.
