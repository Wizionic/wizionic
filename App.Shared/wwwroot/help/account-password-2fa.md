---
id: account
title: Account, password, and 2FA
---

# Account, password, and 2FA

## Sign in

- **Login code (email)** — we email a one-time code. Type it in the app or site. Email links do not sign you in (they opened the wrong app, and mail scanners would use the code).
- **Password** — if you set one. Faster than waiting for email.

Sign in is required. The same account (and encryption key) is what lets other devices decrypt your data. Changing password or signing out a session never deletes notes or chats; that device just signs in again and gets the same key.

## Password

Optional. Used to:

- Sign in without an email code
- Unlock protected notebooks, chats, and albums

Use at least 8 characters. Common and leaked passwords are rejected. We do not force rotation or extra symbol rules.

Changing your password emails you and signs other devices out. Notes on those devices stay; they sign in again.

Forgot the password? On the Password tab, **Forgot password?** (or **Forgot current password?** on the account page). We email a login code. After you enter it, the password is removed and two-factor is turned off so you can **Add a password** again. Other devices are signed out. The encryption key is not changed. Protected notebooks, chats, and albums unlock with the **new** password after you set one.

Unlocking a notebook still asks **only** for this password. It does not ask for 2FA.

## Two-factor sign-in

On the account page, **Two-factor sign-in** (hidden behind a link). When it is on:

1. Enter email and password.
2. Enter an email code, or an SMS code if you enrolled a phone, or a recovery code.

A device you have already confirmed is remembered for 30 days so you are not asked every time.

When you turn 2FA on, save the recovery codes shown once. Each code works one time if email or SMS is delayed.

A cold click of an old magic link does **not** sign you in. Type the code.

SMS uses Twilio Verify on the host you are pointed at. If that host has no Twilio config, you can still use email as the second factor.

## Devices and sessions

On the account page, **Devices and sessions**. Sign out one device or all others. A new device that uses a stolen session cannot sync until it signs in (login code or password). You get an email when a new device signs in.

