# Security Policy

## Supported versions

Security reports are accepted for the latest public release of Wizionic and for the `main` branch.

## Reporting a vulnerability

**Do not file a public GitHub issue for an undisclosed vulnerability.**

1. Prefer [GitHub Security Advisories](https://github.com/Wizionic/wizionic/security/advisories/new) on this repository.
2. Or email **daniellgoodwin@protonmail.com** with a description, impact, and steps to reproduce.

I aim to acknowledge reports within **7 days**.

## Scope

Wizionic is local-first. Chat, notes, gallery, and calendar **bodies** are stored on the device and encrypted at rest (AES-256-GCM). The hosted service at [wizionic.com](https://wizionic.com) (or your Home Server) handles authentication, presence, WebRTC signaling, optional tool/model proxies, and OAuth app credentials — not conversation and user content.

Please include whether the issue is in the desktop app, the browser client, or the hosted service.

## Home Server vs Wizionic.com

Wizionic can sign you in against **wizionic.com** or against a **Home Server** on a PC you control. Chat, notes, gallery, and calendar bodies are not stored on either one. What *does* live on the login host is the account record: email, password hash, optional 2FA settings, and the per-user encryption key that your devices use to decrypt synced data.

**wizionic.com** is a public host on the Internet. Anyone can open the site. The login APIs, the encryption-key endpoint, and the SignalR hub for presence/signaling are reachable from anywhere. We harden that surface (sessions, rate limits, hashed codes, new-device checks), but it is still a server you do not run, on a network you do not control. If that host is compromised, an attacker could mint sessions or read encryption keys for accounts stored there. They would still need a signed-in device (or a new login) to pull your actual notes over sync — the public server does not hold the note bodies — but the *keys* would be in play.

**Home Server** is the same login software installed on your PC (setup wizard). The desktop app talks to `http://localhost:5150`. The process listens on the LAN (`*:5150`) so a phone on your Wi-Fi can use `{hostname}.local:5150` or the PC’s LAN IP. A Windows Firewall **Private** rule opens 5150 for the local network, not for the public Internet.

That is the real improvement: **the login server is not a public website.** There is no wizionic.com account row, no public copy of your encryption key, and no Internet-facing `/api/auth/*` for your household. An attacker on the open Internet cannot hit your login page unless you (or your router) put it there.

What Home Server does **not** magically solve:

- It is reachable by others **on your LAN** (a guest on the Wi-Fi, a compromised IoT device). Treat the home network as the trust boundary.
- If you port-forward 5150, put the PC in a DMZ, or expose it through a tunnel, you have put a login server on the Internet — with HTTP, not the public site’s HTTPS cookie rules. Don’t do that unless you know you are offering a public host.
- Login codes still need email (or a password you set). The Home Server does not become “offline-only auth” unless you have set a password and do not rely on email codes.
- Your notes still live **on the devices**. Home Server vs public site changes where *login* lives, not where the chat history is stored.

For most people who care about this: use the desktop app, install Home Server, point Login server at This PC, and leave wizionic.com for people who only want the website.

## Data on your devices

The likely way someone reads your chats and notes is **not** by hacking Wizionic’s login page. It is by **using the device**: a stolen laptop, a phone left unlocked, a shared family PC, malware already on the machine, or a backup someone else can open.

Bodies are AES-256-GCM encrypted before they are written (IndexedDB in the browser, SQLite in the app). Metadata used for listing (titles, dates) is stored in the clear so the sidebar can load quickly. The encryption key is fetched after a successful sign-in and kept available while that app/browser stays signed in. If the OS is unlocked and Wizionic is signed in, the app can decrypt — that is required for you to read your own notes. Disk encryption and a lock screen on the device are the controls that matter first.

Extra protection **inside** Wizionic, for material you would not want visible in a quick look at an unlocked session:

- Password-protect a **chat**, **notebook**, or **gallery album**. Unlocking those asks for the account password (not 2FA). Locked items stay blocked from AI tools until you unlock them in the UI.

If a device is **lost or compromised**:

1. On a device you still trust, open **Account → Devices and sessions**.
2. Sign that device out, or **Sign out other devices**.
3. Change the password (that also signs other sessions out and emails you).

The lost device will need a fresh sign-in before it can fetch the key or sync. Local copies that were already decrypted on disk while it was signed in are an OS/disk-encryption problem; Wizionic cannot remotely wipe another phone. Signing out sessions stops *new* sync and *new* key fetch.

Sign-out does not delete the encrypted files on that device. The next time the same account signs in there, the same key is returned and the notes decrypt again. That is intentional so a forgotten session or a password change cannot destroy school notes or a family archive.


## What a session can do

A signed-in session cookie can fetch the account encryption key and join device sync. That is why sign-in is the sensitive surface.

- Cookies are HttpOnly, SameSite=Lax, and Secure on the public HTTPS site (`__Host-AppAuth`).
- Sessions live on the server and can be revoked (password change, **Sign out other devices**, or a single device) without rotating encryption keys or deleting notes.
- Existing cookies from before this change are upgraded to a server session on the next request — they are not rejected, so nobody is locked out of existing data.
- A bound session used from a **different** device id cannot fetch the encryption key or sync until that device signs in (login code or password). Old clients that omit the device header still work so updates cannot trap data.
- The per-user encryption key is created once and **never rotated** on login, password change, or session revoke. Re-signing in returns the same key.

## Sign-in

- **Login code** (email): 10-character one-time code, 15 minutes, hashed at rest. The account (and encryption key) is created only when the code is used. Emails contain the code to type; they do not contain a clickable sign-in URL (those opened the wrong app and mail scanners consumed the token).
- **Password**: optional. At least 8 characters; common and leaked passwords (Have I Been Pwned k-anonymity) are rejected. No composition theatre and no rotation. New hashes are Argon2id; older PBKDF2 hashes still verify and are upgraded on the next successful password login.
- **Two-factor** (optional): after password, an email or SMS code. Recovery codes are shown once when 2FA is turned on. A confirmed device is remembered for 30 days. 2FA is prompted, not mandatory — email delivery is not reliable enough to require it for everyone.
- Rate limits and a 15-minute lockout apply after repeated failures. There is no CAPTCHA on every login and no idle session expiry.

## Notifications

Email (Brevo, with SMTP fallback) is sent when:

- The password changes
- A **new device** signs in
- Two-factor is turned on or off, or the 2FA phone changes

A failed email never blocks the security action itself.

## Out of scope for reports (unless you found a bypass)

- Forcing the user to re-login on a device they already signed in on, after a routine server restart (Data Protection keys persist in SQLite).
- Home Server on HTTP (`localhost` / LAN) using non-`__Host-` cookies so Secure cookies can be stored.

## Local data (technical)

- Namespace `u-{userId}-` on the device. Sign-out does not wipe local encrypted notes; the next sign-in for that account decrypts them with the same key.
- Metadata (titles, dates) is stored cleartext for listing; message/note bodies are encrypted before persistence.
